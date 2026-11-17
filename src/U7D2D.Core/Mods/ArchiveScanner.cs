using System.Diagnostics;
using System.Text.RegularExpressions;
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
    public const int Version = 6;

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
                // A ModInfo.xml may sit several folders deep. Only the outermost root is
                // installed: a nested copy would land inside another mod folder where the game
                // does not scan for it.
                var roots = new List<string>();
                var entriesByRoot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in modInfoEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var root = GetDirectory(entry.Key);
                    if (entriesByRoot.ContainsKey(root)) continue;
                    roots.Add(root);
                    entriesByRoot[root] = entry.Key;
                }

                var installedRoots = roots
                    .Where(root => !roots.Any(other => IsNestedRoot(root, other)))
                    .ToList();

                if (installedRoots.Count != roots.Count)
                    result.Warnings.Add(Localization.SF("mod.nestedModInfo", roots.Count - installedRoots.Count));

                foreach (var root in installedRoots)
                {
                    var entryKey = entriesByRoot[root];

                    ModMetadata metadata;
                    var hasModInfo = true;
                    var warning = (string?)null;

                    try
                    {
                        var bytes = handle.ReadEntryBytes(entryKey, MaxModInfoBytes);
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
                    var package = CreatePackage(fullPath, fileInfo, handle.FormatLabel, root, metadata, hasModInfo, warning, profile, handle.Entries.Count, sourceRoot);
                    ApplyRootCandidates(package, fullPath, root, roots, handle.Entries);
                    result.Mods.Add(package);
                }

                DetectComposites(result, handle, fullPath, fileInfo, sourceRoot);

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

    // "0-Core", "1-NPCMod", "zzz_Last": prefixes that decide the load order in the game.
    private static readonly Regex LoadOrderPrefix = new(
        @"^(?:\d+[-_ ]|z{2,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Fills the root choice data. A normal archive gets no candidates: the scanner is sure and the
    /// install stays one click. Candidates appear only when the folder name or the content root is
    /// genuinely ambiguous.
    /// </summary>
    private static void ApplyRootCandidates(
        ModPackage package,
        string archivePath,
        string root,
        List<string> allRoots,
        IReadOnlyList<ArchiveEntryInfo> entries)
    {
        var defaultName = package.FolderName;
        var candidates = new List<ModRootCandidate>();
        var nested = allRoots.Where(other => IsNestedRoot(other, root)).ToList();

        var cumulative = string.Empty;
        foreach (var segment in root.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cumulative = cumulative.Length == 0 ? segment : cumulative + "/" + segment;
            if (GenericWrapperNames.Contains(segment)) continue;

            candidates.Add(new ModRootCandidate
            {
                Name = segment,
                ContentRoot = root,
                DisplayPath = cumulative,
                IsDefault = string.Equals(segment, defaultName, StringComparison.OrdinalIgnoreCase)
            });
        }

        foreach (var inner in nested)
        {
            var name = GuessFolderName(archivePath, inner);
            if (string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase))
                name = inner.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;

            candidates.Add(new ModRootCandidate
            {
                Name = name,
                ContentRoot = inner,
                DisplayPath = inner,
                IsDefault = false
            });
        }

        if (candidates.All(candidate => !candidate.IsDefault))
        {
            candidates.Insert(0, new ModRootCandidate
            {
                Name = defaultName,
                ContentRoot = root,
                DisplayPath = string.IsNullOrWhiteSpace(root) ? package.ArchiveFileName : root,
                IsDefault = true
            });
        }

        var previews = new Dictionary<string, (List<string> Names, int Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!previews.TryGetValue(candidate.ContentRoot, out var preview))
            {
                preview = BuildPreview(entries, candidate.ContentRoot);
                previews[candidate.ContentRoot] = preview;
            }

            candidate.TopLevelEntries = preview.Names;
            candidate.FileCount = preview.Files;
        }

        var prefixAbove = HasPrefixedAncestor(root, defaultName);
        package.NeedsRootChoice = prefixAbove || nested.Count > 0;
        package.AmbiguityReason = (prefixAbove, nested.Count > 0) switch
        {
            (true, true) => "nested+prefix",
            (true, false) => "prefix",
            (false, true) => "nested",
            _ => null
        };

        package.RootCandidates = package.NeedsRootChoice
            ? candidates
                .GroupBy(c => c.Name + "|" + c.ContentRoot, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList()
            : new List<ModRootCandidate>();
    }

    private static void DetectComposites(
        ArchiveScanResult result,
        ArchiveHandle handle,
        string archivePath,
        FileInfo archiveInfo,
        string? sourceRoot)
    {
        var packages = result.Mods.Where(package => !package.IsComposite).ToList();
        if (packages.Count < 2) return;

        var groups = new Dictionary<string, List<ModPackage>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            var container = FindSharedContainer(package.RootInArchive, packages, handle.Entries);
            if (container is null) continue;

            if (!groups.TryGetValue(container, out var list))
                groups[container] = list = new List<ModPackage>();
            list.Add(package);
        }

        foreach (var (container, components) in groups)
        {
            if (components.Count < 2) continue;

            var shared = DirectFiles(handle.Entries, container);
            if (shared.Count == 0 || !shared.Any(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                continue;

            var composite = CreateCompositePackage(
                archivePath, archiveInfo, container, components, shared, handle.Entries, sourceRoot);

            foreach (var component in components) result.Mods.Remove(component);
            result.Mods.Add(composite);
        }
    }

    private static string? FindSharedContainer(
        string root,
        List<ModPackage> packages,
        IReadOnlyList<ArchiveEntryInfo> entries)
    {
        var best = (string?)null;
        var candidate = root;

        while (true)
        {
            var cut = candidate.LastIndexOf('/');
            candidate = cut >= 0 ? candidate[..cut] : string.Empty;

            var isPackageRoot = packages.Any(package =>
                string.Equals(package.RootInArchive, candidate, StringComparison.OrdinalIgnoreCase));
            if (!isPackageRoot)
            {
                var nested = packages.Count(package =>
                    IsUnder(package.RootInArchive, candidate));
                var hasDll = DirectFiles(entries, candidate)
                    .Any(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

                if (nested >= 2 && hasDll) best = candidate;
            }

            if (candidate.Length == 0) break;
        }

        return best;
    }

    private static bool IsUnder(string root, string container)
    {
        if (container.Length == 0) return root.Length > 0;
        return root.StartsWith(container + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> DirectFiles(IReadOnlyList<ArchiveEntryInfo> entries, string container)
    {
        var prefix = container.Length == 0 ? string.Empty : container + "/";
        var names = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.IsDirectory) continue;
            if (prefix.Length > 0 && !entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = entry.Key[prefix.Length..];
            if (relative.Length == 0 || relative.Contains('/', StringComparison.Ordinal)) continue;
            if (!names.Contains(relative, StringComparer.OrdinalIgnoreCase)) names.Add(relative);
        }

        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ModPackage CreateCompositePackage(
        string archivePath,
        FileInfo archiveInfo,
        string container,
        List<ModPackage> components,
        List<string> sharedFiles,
        IReadOnlyList<ArchiveEntryInfo> entries,
        string? sourceRoot)
    {
        var label = GuessFolderName(archivePath, container);
        var folder = "000-" + PathSafety.SanitizeFolderName(label, "Shared");
        var technical = new string(folder.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());

        var fingerprint = HashUtil.Sha256Hex(
            $"{archivePath.ToLowerInvariant()}|{archiveInfo.Length}|{archiveInfo.LastWriteTimeUtc.Ticks}|" +
            $"composite|{container}|{string.Join(',', components.Select(c => c.RootInArchive))}");

        var package = new ModPackage
        {
            Id = fingerprint[..16],
            ArchivePath = archivePath,
            ArchiveFileName = Path.GetFileName(archivePath),
            RootInArchive = container,
            FolderName = folder,
            SharedFolder = folder,
            IsComposite = true,
            GeneratesModInfo = true,
            Components = components,
            SharedFiles = sharedFiles,
            ExcludeRoots = components.Select(c => c.RootInArchive).ToList(),
            Info = new ModMetadata
            {
                Name = technical,
                DisplayName = $"{label} (shared files)",
                Version = "1.0.0",
                Author = "archive"
            },
            Content = BuildProfile(entries, container),
            ArchiveSize = archiveInfo.Length,
            ArchiveModifiedUtc = archiveInfo.LastWriteTimeUtc,
            HasModInfo = false,
            Fingerprint = fingerprint,
            SourceRoot = sourceRoot ?? string.Empty
        };

        return package;
    }

    private static (List<string> Names, int Files) BuildPreview(
        IReadOnlyList<ArchiveEntryInfo> entries, string root)
    {
        var prefix = string.IsNullOrWhiteSpace(root) ? string.Empty : root + "/";
        var names = new List<string>();
        var files = 0;

        foreach (var entry in entries)
        {
            if (entry.IsDirectory) continue;
            if (!entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = entry.Key[prefix.Length..];
            if (relative.Length == 0) continue;

            files++;
            var slash = relative.IndexOf('/');
            var name = slash >= 0 ? relative[..(slash + 1)] : relative;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        var ordered = names
            .OrderByDescending(name => name.Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(name => name.EndsWith("/", StringComparison.Ordinal))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        return (ordered, files);
    }

    private static bool HasPrefixedAncestor(string root, string defaultName)
    {
        var segments = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var chosen = Array.FindLastIndex(segments,
            segment => string.Equals(segment, defaultName, StringComparison.OrdinalIgnoreCase));

        // The name may come from the archive file, not from a folder inside it.
        var limit = chosen >= 0 ? chosen : segments.Length;
        for (var i = 0; i < limit; i++)
            if (LoadOrderPrefix.IsMatch(segments[i])) return true;

        return false;
    }

    private static readonly HashSet<string> GenericWrapperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mod", "mods", "mod folder", "modfile", "modfiles", "files", "file", "content",
        "package", "archive", "root", "game", "7dtd", "7daystodie", "7 days to die"
    };

    /// <summary>
    /// Folder name the mod gets inside Mods. The deepest real folder wins: that is the name the
    /// author ships and the game sorts by. Generic wrappers are skipped, so "0-Core/mod/ModInfo.xml"
    /// stays "0-Core" and keeps its load-order prefix.
    /// </summary>
    private static string GuessFolderName(string archivePath, string root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            var segments = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = segments.Length - 1; i >= 0; i--)
            {
                var name = segments[i].Trim();
                if (name.Length > 0 && !GenericWrapperNames.Contains(name)) return name;
            }
        }

        return BaseNameWithoutArchiveExtension(archivePath);
    }

    /// <summary>True when a mod root sits inside another root of the same archive.</summary>
    private static bool IsNestedRoot(string root, string other)
    {
        if (string.IsNullOrWhiteSpace(other) || string.IsNullOrWhiteSpace(root)) return false;
        if (string.Equals(root, other, StringComparison.OrdinalIgnoreCase)) return false;
        return root.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase);
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
