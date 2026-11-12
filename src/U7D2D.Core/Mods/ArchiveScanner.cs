using System.Diagnostics;
using U7D2D.Core.Archives;
using U7D2D.Core.I18n;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Mods;

/// <summary>An archive file together with the configured root folder that owns it.</summary>
public sealed record ArchiveFile(string Path, string SourceRoot);

/// <summary>Recursively locates archive files in configured source folders.</summary>
public static class ArchiveLocator
{
    /// <summary>
    /// Enumerates archives in all configured roots. When one root is nested inside another
    /// (for example D://Mods and D://Mods//1), the most specific root wins: every archive is listed
    /// exactly once and stays attached to the folder the user actually organised it in.
    /// </summary>
    public static List<ArchiveFile> EnumerateArchives(
        IEnumerable<string> roots,
        bool recursive,
        int maxDepth,
        IProgress<ScanProgressInfo>? progress = null,
        List<string>? skipped = null,
        CancellationToken cancellationToken = default)
    {
        var found = new List<ArchiveFile>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Longest paths first so nested roots claim their own archives before the parent root walks in.
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).OrderByDescending(r => r.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(root))
            {
                skipped?.Add(Localization.SF("mod.folderUnavailable", root));
                continue;
            }

            var stack = new Stack<(string Directory, int Depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = stack.Pop();

                if (!visited.Add(directory)) continue;

                progress?.Report(new ScanProgressInfo
                {
                    Phase = ScanPhase.Enumerating,
                    Item = directory,
                    Message = Localization.SF("mod.searchingArchives", directory)
                });

                string[] files;
                try
                {
                    files = Directory.GetFiles(directory);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    skipped?.Add(Localization.SF("mod.noAccess", directory));
                    continue;
                }

                foreach (var file in files)
                {
                    if (!ArchiveFormats.IsSupportedArchive(file)) continue;

                    var full = Path.GetFullPath(file);
                    if (!claimed.Add(full)) continue;

                    found.Add(new ArchiveFile(full, root));
                }

                if (!recursive || depth >= maxDepth) continue;

                string[] subdirectories;
                try
                {
                    subdirectories = Directory.GetDirectories(directory);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    skipped?.Add(Localization.SF("mod.noAccess", directory));
                    continue;
                }

                foreach (var subdirectory in subdirectories)
                {
                    try
                    {
                        var info = new DirectoryInfo(subdirectory);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        stack.Push((subdirectory, depth + 1));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        skipped?.Add(Localization.SF("mod.skippedFolder", subdirectory));
                    }
                }
            }
        }

        found.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return found;
    }
}

/// <summary>Reads a single archive and extracts every mod it contains.</summary>
public sealed class ArchiveScanner
{
    /// <summary>Bump whenever parsing/detection changes so stale cached scans are ignored.</summary>
    public const int Version = 3;

    private const long MaxModInfoBytes = 4 * 1024 * 1024;
    private readonly AppPaths _paths;

    public ArchiveScanner(AppPaths paths) => _paths = paths;

    public ArchiveScanResult Scan(string archivePath, string sourceRoot = "", CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var fullPath = Path.GetFullPath(archivePath);
        var fileInfo = new FileInfo(fullPath);

        var result = new ArchiveScanResult
        {
            ArchivePath = fullPath,
            ArchiveSize = fileInfo.Exists ? fileInfo.Length : 0,
            ArchiveModifiedUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue,
            ScannerVersion = Version,
            SourceRoot = sourceRoot ?? string.Empty
        };

        if (!fileInfo.Exists)
        {
            result.Error = Localization.S("mod.archiveNotFound");
            result.Duration = stopwatch.Elapsed;
            return result;
        }

        try
        {
            using var handle = ArchiveReader.Open(fullPath, _paths.TempDirectory, cancellationToken);
            result.Format = handle.FormatLabel;
            result.EntryCount = handle.Entries.Count;

            if (handle.IsEncrypted)
                result.Warnings.Add(Localization.S("mod.archiveEncrypted"));

            var modInfoEntries = handle.Entries
                .Where(e => !e.IsDirectory && IsFileName(e.Key, "ModInfo.xml"))
                .ToList();

            if (modInfoEntries.Count == 0)
            {
                AddFallbackPackage(handle, result);
            }
            else
            {
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in modInfoEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var root = GetDirectory(entry.Key);
                    if (!roots.Add(root)) continue;

                    ModMetadata metadata;
                    var hasModInfo = true;
                    var warning = (string?)null;

                    try
                    {
                        var bytes = handle.ReadEntryBytes(entry.Key, MaxModInfoBytes);
                        if (!ModInfoParser.TryParse(bytes, out metadata, out var error))
                        {
                            metadata = ModMetadata.Unknown(GuessFolderName(fullPath, root));
                            hasModInfo = false;
                            warning = Localization.SF("mod.modInfoCorrupt", error ?? string.Empty);
                            result.Warnings.Add($"{Path.GetFileName(fullPath)}: {warning}");
                        }
                    }
                    catch (Exception ex)
                    {
                        metadata = ModMetadata.Unknown(GuessFolderName(fullPath, root));
                        hasModInfo = false;
                        warning = Localization.SF("mod.modInfoReadFailed", ex.Message);
                        result.Warnings.Add($"{Path.GetFileName(fullPath)}: {warning}");
                    }

                    var profile = BuildProfile(handle.Entries, root);
                    result.Mods.Add(CreatePackage(fullPath, fileInfo, handle.FormatLabel, root, metadata, hasModInfo, warning, profile, handle.Entries.Count, sourceRoot));
                }

                if (result.Mods.Count > 1)
                    result.Warnings.Add(Localization.SF("mod.multipleMods", result.Mods.Count));
            }

            result.Success = true;
        }
        catch (ArchiveOpenException ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = Localization.SF("mod.archiveReadFailed", ex.Message);
        }

        result.Duration = stopwatch.Elapsed;
        return result;
    }

    private void AddFallbackPackage(ArchiveHandle handle, ArchiveScanResult result)
    {
        var profile = BuildProfile(handle.Entries, string.Empty);
        var looksLikeMod = profile.HasConfig || profile.HasHarmonyFolder || profile.HasDlls;

        if (!looksLikeMod)
        {
            result.Warnings.Add(Localization.S("mod.noModSigns"));
            return;
        }

        var folder = PathSafety.SanitizeFolderName(
            BaseNameWithoutArchiveExtension(handle.OriginalPath), "Mod");

        var metadata = ModMetadata.Unknown(folder);
        var info = new FileInfo(handle.OriginalPath);
        var package = CreatePackage(
            handle.OriginalPath, info, handle.FormatLabel, string.Empty, metadata,
            hasModInfo: false,
            warning: Localization.S("mod.modInfoMissingFallback"),
            profile, handle.Entries.Count, result.SourceRoot ?? string.Empty);

        result.Mods.Add(package);
        result.Warnings.Add(package.Warning!);
    }

    private static ModPackage CreatePackage(
        string archivePath,
        FileInfo archiveInfo,
        string format,
        string root,
        ModMetadata metadata,
        bool hasModInfo,
        string? warning,
        ModContentProfile profile,
        int entryCount,
        string? sourceRoot)
    {
        var folderName = PathSafety.SanitizeFolderName(GuessFolderName(archivePath, root), "Mod");
        var fingerprint = HashUtil.Sha256Hex(
            $"{archivePath.ToLowerInvariant()}|{archiveInfo.Length}|{archiveInfo.LastWriteTimeUtc.Ticks}|{root}|{entryCount}|{format}");

        return new ModPackage
        {
            Id = fingerprint[..16],
            ArchivePath = archivePath,
            ArchiveFileName = Path.GetFileName(archivePath),
            RootInArchive = root,
            FolderName = folderName,
            Info = metadata,
            Content = profile,
            ArchiveSize = archiveInfo.Length,
            ArchiveModifiedUtc = archiveInfo.LastWriteTimeUtc,
            HasModInfo = hasModInfo,
            Warning = warning,
            Fingerprint = fingerprint,
            SourceRoot = sourceRoot ?? string.Empty
        };
    }

    private static string GuessFolderName(string archivePath, string root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            var index = root.LastIndexOf('/');
            var name = index >= 0 ? root[(index + 1)..] : root;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return BaseNameWithoutArchiveExtension(archivePath);
    }

    /// <summary>Strips compound archive extensions so "Mod.tar.gz" becomes "Mod", not "Mod.tar".</summary>
    internal static string BaseNameWithoutArchiveExtension(string path)
    {
        var name = Path.GetFileName(path);
        var lower = name.ToLowerInvariant();
        string[] compound = [".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".tbz", ".txz"];

        foreach (var extension in compound)
        {
            if (lower.EndsWith(extension, StringComparison.Ordinal))
                return name[..^extension.Length];
        }

        return Path.GetFileNameWithoutExtension(name);
    }

    private static string GetDirectory(string key)
    {
        var index = key.LastIndexOf('/');
        return index <= 0 ? string.Empty : key[..index];
    }

    private static bool IsFileName(string key, string fileName)
    {
        var index = key.LastIndexOf('/');
        var name = index >= 0 ? key[(index + 1)..] : key;
        return name.Equals(fileName, StringComparison.OrdinalIgnoreCase);
    }

    public static ModContentProfile BuildProfile(IEnumerable<ArchiveEntryInfo> entries, string root)
    {
        var profile = new ModContentProfile();
        var prefix = string.IsNullOrEmpty(root) ? string.Empty : root + "/";
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.IsDirectory) continue;
            if (prefix.Length > 0 && !entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = prefix.Length == 0 ? entry.Key : entry.Key[prefix.Length..];
            if (string.IsNullOrWhiteSpace(relative)) continue;

            profile.FileCount++;
            profile.TotalBytes += entry.Size;

            var lower = relative.ToLowerInvariant();
            var slash = relative.IndexOf('/');
            if (slash > 0) folders.Add(relative[..slash]);

            if (lower.EndsWith(".xml"))
            {
                if (profile.XmlFiles.Count < 500) profile.XmlFiles.Add(relative);
                if (lower.StartsWith("config/") && profile.ConfigFiles.Count < 500) profile.ConfigFiles.Add(relative);
            }

            if (lower.EndsWith(".dll") && profile.DllFiles.Count < 100)
                profile.DllFiles.Add(relative);

            if (lower.StartsWith("harmony/")) profile.HasHarmonyFolder = true;
            if (lower.StartsWith("config/xui/") || lower.Contains("/xui/")) profile.HasXui = true;
            if (lower.Contains("localization")) profile.HasLocalization = true;
            if (lower.StartsWith("assets/") || lower.EndsWith(".unity3d") || lower.EndsWith(".bundle")) profile.HasAssets = true;
        }

        profile.TopLevelFolders = folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        return profile;
    }
}
