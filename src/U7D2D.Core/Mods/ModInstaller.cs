using System.Diagnostics;
using U7D2D.Core.Archives;
using U7D2D.Core.Data;
using U7D2D.Core.I18n;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Mods;

/// <summary>
/// Installs, updates and removes mod folders. Every destructive step is preceded by a backup
/// and staged through a temp directory, so a failed extraction never corrupts the game folder.
/// </summary>
public sealed class ModInstaller
{
    private readonly AppPaths _paths;
    private readonly BackupService _backups;
    private readonly InstalledStore _store;

    public ModInstaller(AppPaths paths, BackupService backups, InstalledStore store)
    {
        _paths = paths;
        _backups = backups;
        _store = store;
    }

    public InstallOutcome Install(
        ModPackage package,
        AppSettings settings,
        InstallOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // A composite archive is a container with shared files and several child mods: the shared
        // files become a generated mod of their own, children are installed next to it.
        if (package.IsComposite)
            return InstallComposite(package, settings, options, progress, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var outcome = new InstallOutcome { ModName = package.DisplayTitle };
        string? staging = null;
        var keepStaging = false;

        try
        {
            if (string.IsNullOrWhiteSpace(settings.ModsPath))
            {
                outcome.Error = Localization.S("mod.modsPathNotSet");
                return outcome;
            }

            if (!File.Exists(package.ArchivePath))
            {
                outcome.Error = Localization.SF("mod.archiveMissing", package.ArchivePath);
                return outcome;
            }

            // A folder that cannot be written fails before anything is extracted, with a message
            // the user can act on instead of the raw access denied text from the file system.
            if (!PathSafety.IsDirectoryWritable(settings.ModsPath))
            {
                outcome.Error = Localization.SF("mod.modsNotWritable", settings.ModsPath);
                return outcome;
            }

            Directory.CreateDirectory(settings.ModsPath);

            var folderName = PathSafety.SanitizeFolderName(options.FolderNameOverride ?? package.FolderName, "Mod");
            var target = Path.Combine(settings.ModsPath, folderName);

            if (!PathSafety.IsUnderRoot(settings.ModsPath, target))
            {
                outcome.Error = Localization.S("mod.invalidInstallPath");
                return outcome;
            }

            outcome.TargetPath = target;
            var exists = Directory.Exists(target);

            if (exists && !options.AllowReplace)
            {
                outcome.Error = Localization.SF("mod.folderExists", folderName);
                return outcome;
            }

            if (exists)
            {
                outcome.ReplacedExisting = true;
                var existingMetadata = TryReadMetadata(target);
                if (existingMetadata is not null &&
                    !string.Equals(existingMetadata.EffectiveName, package.Info.EffectiveName, StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Warnings.Add(Localization.SF("mod.folderHadOtherMod",
                        folderName, existingMetadata.EffectiveName));
                }

                if (options.CreateBackup)
                {
                    var backup = _backups.Create(target, settings, Localization.S("mod.reasonBeforeUpdate"), existingMetadata);
                    outcome.BackupCreated = true;
                    outcome.BackupPath = backup.ArchivePath;
                }
            }

            // A nested root picked in the dialog replaces the package root; anything outside
            // the package is rejected so a crafted archive cannot point the extraction elsewhere.
            var contentRoot = package.RootInArchive;
            if (!string.IsNullOrWhiteSpace(options.ContentRootOverride))
            {
                var requested = options.ContentRootOverride.Replace('\\', '/').Trim('/');
                if (!IsContentRootAllowed(package.RootInArchive, requested))
                {
                    outcome.Error = Localization.S("mod.invalidContentRoot");
                    return outcome;
                }

                contentRoot = requested;
            }

            progress?.Report(new InstallProgress { Stage = Localization.S("mod.stageExtract") });
            staging = _paths.CreateTempDirectory("install");

            using (var handle = ArchiveReader.Open(package.ArchivePath, _paths.TempDirectory, cancellationToken))
            {
                var extraction = ArchiveReader.ExtractSubtree(
                    handle,
                    contentRoot,
                    staging,
                    overwrite: true,
                    progress: new Progress<(int Processed, string Current)>(p =>
                        progress?.Report(new InstallProgress
                        {
                            Stage = Localization.S("mod.stageExtract"),
                            Processed = p.Processed,
                            CurrentFile = p.Current
                        })),
                    cancellationToken: cancellationToken);

                if (!extraction.Success)
                {
                    outcome.Error = Localization.SF("mod.extractFailed", extraction.Error ?? string.Empty);
                    return outcome;
                }

                outcome.FilesWritten = extraction.Files.Count;
                if (extraction.SkippedUnsafe.Count > 0)
                    outcome.Warnings.Add(Localization.SF("mod.unsafeSkipped", extraction.SkippedUnsafe.Count));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (outcome.FilesWritten == 0)
            {
                outcome.Error = Localization.S("mod.noFilesToInstall");
                return outcome;
            }

            if (package.HasModInfo && !Directory.EnumerateFiles(staging, "ModInfo.xml", SearchOption.AllDirectories).Any())
            {
                outcome.Error = Localization.S("mod.modInfoMissingAfterExtract");
                return outcome;
            }

            progress?.Report(new InstallProgress { Stage = Localization.S("mod.stageCommit") });

            // The prepared folder is copied next to the target and swapped in by rename. The old
            // folder is kept until the new one is in place, so a failure can be undone even when
            // the user turned backups off.
            var commit = FileOps.CommitDirectory(staging, target);

            if (!commit.Success)
            {
                outcome.RolledBack = commit.RolledBack;
                outcome.KeptOldPath = commit.KeptOldPath;
                outcome.KeptNewPath = commit.KeptNewPath;
                outcome.Error = DescribeCommitFailure(commit, target, staging);

                // Keep the staged copy only when nothing was restored; otherwise it is just junk.
                keepStaging = !commit.RolledBack;
                return outcome;
            }

            if (commit.Warning is not null) outcome.Warnings.Add(commit.Warning);

            FileOps.TryDeleteDirectory(staging);
            staging = null;

            outcome.BytesWritten = FileOps.GetDirectorySize(target);

            var metadata = TryReadMetadata(target) ?? package.Info;
            _store.Upsert(new InstalledMod
            {
                FolderName = folderName,
                FullPath = target,
                Info = metadata,
                Content = InstalledModScanner.BuildProfile(target),
                FileCount = FileOps.CountFiles(target),
                SizeBytes = outcome.BytesWritten,
                HasModInfo = File.Exists(Path.Combine(target, "ModInfo.xml")),
                LastWriteUtc = Directory.GetLastWriteTimeUtc(target),
                ContentHash = HashUtil.FingerprintDirectory(target),
                SourceArchive = package.ArchivePath,
                InstalledUtc = DateTime.UtcNow
            });

            outcome.Success = true;
            return outcome;
        }
        catch (OperationCanceledException)
        {
            outcome.Error = Localization.S("mod.installCancelled");
            return outcome;
        }
        catch (Exception ex)
        {
            outcome.Error = ex.Message;
            return outcome;
        }
        finally
        {
            if (staging is not null && !keepStaging) FileOps.TryDeleteDirectory(staging);
            outcome.Duration = stopwatch.Elapsed;
        }
    }

    private static string DescribeCommitFailure(
        FileOps.DirectoryCommitResult commit, string target, string staging)
    {
        var text = commit.Error ?? Localization.S("mod.replaceFailed");

        if (commit.RolledBack)
            text += " " + Localization.SF("mod.commitRolledBack", target);
        else if (commit.KeptOldPath is not null)
            text += " " + Localization.SF("mod.commitKeptOld", commit.KeptOldPath, commit.KeptNewPath ?? staging);

        if (commit.LikelyLocked)
            text += " " + Localization.S("mod.lockedHint");

        return text;
    }

    public UninstallOutcome Uninstall(InstalledMod mod, AppSettings settings, bool createBackup)
    {
        var outcome = new UninstallOutcome { ModName = mod.DisplayTitle, FolderPath = mod.FullPath };

        try
        {
            if (string.IsNullOrWhiteSpace(settings.ModsPath) || !PathSafety.IsUnderRoot(settings.ModsPath, mod.FullPath))
            {
                outcome.Error = Localization.S("mod.outsideMods");
                return outcome;
            }

            if (!PathSafety.IsDirectoryWritable(settings.ModsPath))
            {
                outcome.Error = Localization.SF("mod.modsNotWritable", settings.ModsPath);
                return outcome;
            }

            // The folder itself is inside Mods by definition; refusing the root keeps a bad record
            // from turning one removal into the removal of every installed mod.
            if (SamePath(settings.ModsPath, mod.FullPath))
            {
                outcome.Error = Localization.S("mod.refuseModsRoot");
                return outcome;
            }

            if (!Directory.Exists(mod.FullPath))
            {
                outcome.Error = Localization.S("mod.folderAlreadyGone");
                return outcome;
            }

            if (!File.Exists(Path.Combine(mod.FullPath, "ModInfo.xml")) && mod.HasModInfo)
            {
                outcome.Error = Localization.S("mod.noModInfoUninstall");
                return outcome;
            }

            if (createBackup)
            {
                var backup = _backups.Create(mod.FullPath, settings, Localization.S("mod.reasonBeforeRemove"), mod.Info);
                outcome.BackupCreated = true;
                outcome.BackupPath = backup.ArchivePath;
            }

            var parent = Path.GetDirectoryName(mod.FullPath);
            if (string.IsNullOrEmpty(parent))
            {
                outcome.Error = Localization.S("mod.deleteFailed");
                return outcome;
            }

            // Rename first, delete second. A folder that cannot be deleted stays whole and is put
            // back, instead of being left half-removed.
            var aside = Path.Combine(parent, $".{mod.FolderName}.u7d2d-removed-{Guid.NewGuid():N}");

            try
            {
                Directory.Move(mod.FullPath, aside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome.Error = Localization.S("mod.deleteFailed") + " " + Localization.S("mod.lockedHint");
                return outcome;
            }

            if (!FileOps.DeleteDirectory(aside))
            {
                try { Directory.Move(aside, mod.FullPath); } catch (Exception) { }

                outcome.Error = Localization.S("mod.deleteFailed") + " " + Localization.S("mod.lockedHint");
                return outcome;
            }

            _store.Remove(mod.FolderName);
            outcome.Success = true;
            return outcome;
        }
        catch (Exception ex)
        {
            outcome.Error = ex.Message;
            return outcome;
        }
    }

    private static bool SamePath(string left, string right)
    {
        static string Normalize(string value) =>
            Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private InstallOutcome InstallComposite(
        ModPackage package,
        AppSettings settings,
        InstallOptions options,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new List<InstallOutcome>();

        var shared = InstallSharedFiles(package, settings, options, progress, cancellationToken);
        results.Add(shared);

        // Children are installed only when their shared dependency landed; otherwise both would be
        // broken in the same way the archive was before.
        if (shared.Success)
        {
            foreach (var component in package.Components)
            {
                var childOptions = new InstallOptions
                {
                    AllowReplace = options.AllowReplace,
                    CreateBackup = options.CreateBackup,
                    FolderNameOverride = component.FolderName
                };

                results.Add(Install(component, settings, childOptions, progress, cancellationToken));
            }
        }

        var outcome = new InstallOutcome
        {
            ModName = package.DisplayTitle,
            TargetPath = shared.TargetPath,
            Success = results.All(result => result.Success),
            Error = results.FirstOrDefault(result => !result.Success)?.Error,
            FilesWritten = results.Sum(result => result.FilesWritten),
            BytesWritten = results.Sum(result => result.BytesWritten),
            ReplacedExisting = results.Any(result => result.ReplacedExisting),
            BackupCreated = results.Any(result => result.BackupCreated),
            BackupPath = results.FirstOrDefault(result => result.BackupPath is not null)?.BackupPath,
            Duration = stopwatch.Elapsed
        };

        foreach (var warning in results.SelectMany(result => result.Warnings).Distinct(StringComparer.Ordinal))
            outcome.Warnings.Add(warning);

        return outcome;
    }

    /// <summary>
    /// Installs the container files (DLLs and other shared payload) as a top-level mod with a
    /// generated ModInfo.xml. Child mod folders are excluded: they are installed separately.
    /// </summary>
    private InstallOutcome InstallSharedFiles(
        ModPackage package,
        AppSettings settings,
        InstallOptions options,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var outcome = new InstallOutcome { ModName = package.DisplayTitle };
        string? staging = null;
        string? prepared = null;

        try
        {
            if (string.IsNullOrWhiteSpace(settings.ModsPath))
            {
                outcome.Error = Localization.S("mod.modsPathNotSet");
                return outcome;
            }

            if (!PathSafety.IsDirectoryWritable(settings.ModsPath))
            {
                outcome.Error = Localization.SF("mod.modsNotWritable", settings.ModsPath);
                return outcome;
            }

            Directory.CreateDirectory(settings.ModsPath);

            var folderName = PathSafety.SanitizeFolderName(
                options.FolderNameOverride ?? package.SharedFolder ?? package.FolderName, "Shared");
            var target = Path.Combine(settings.ModsPath, folderName);

            if (!PathSafety.IsUnderRoot(settings.ModsPath, target))
            {
                outcome.Error = Localization.S("mod.invalidInstallPath");
                return outcome;
            }

            outcome.TargetPath = target;
            var exists = Directory.Exists(target);

            if (exists && !options.AllowReplace)
            {
                outcome.Error = Localization.SF("mod.folderExists", folderName);
                return outcome;
            }

            if (exists)
            {
                outcome.ReplacedExisting = true;
                if (options.CreateBackup)
                {
                    var backup = _backups.Create(target, settings,
                        Localization.S("mod.reasonBeforeUpdate"), package.Info);
                    outcome.BackupCreated = true;
                    outcome.BackupPath = backup.ArchivePath;
                }
            }

            progress?.Report(new InstallProgress { Stage = Localization.S("mod.stageExtract") });
            staging = _paths.CreateTempDirectory("shared");

            using (var handle = ArchiveReader.Open(package.ArchivePath, _paths.TempDirectory, cancellationToken))
            {
                var extraction = ArchiveReader.ExtractSubtree(
                    handle, package.RootInArchive, staging, overwrite: true,
                    progress: new Progress<(int Processed, string Current)>(p =>
                        progress?.Report(new InstallProgress
                        {
                            Stage = Localization.S("mod.stageExtract"),
                            Processed = p.Processed,
                            CurrentFile = p.Current
                        })),
                    cancellationToken: cancellationToken);

                if (!extraction.Success)
                {
                    outcome.Error = Localization.SF("mod.extractFailed", extraction.Error ?? string.Empty);
                    return outcome;
                }
            }

            prepared = _paths.CreateTempDirectory("shared-out");
            var copied = CopySharedPayload(staging, prepared, package, out var bytes);
            WriteGeneratedModInfo(prepared, package.Info);

            outcome.FilesWritten = copied + 1;
            outcome.BytesWritten = bytes;

            var commit = FileOps.CommitDirectory(prepared, target);
            if (!commit.Success)
            {
                outcome.Error = commit.Error;
                outcome.RolledBack = commit.RolledBack;
                return outcome;
            }

            prepared = null;
            outcome.Success = true;

            _store.Upsert(new InstalledMod
            {
                FolderName = folderName,
                FullPath = target,
                Info = package.Info,
                FileCount = outcome.FilesWritten,
                SizeBytes = bytes,
                HasModInfo = true,
                InstalledUtc = DateTime.UtcNow,
                SourceArchive = package.ArchivePath
            });

            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome.Error = ex.Message;
            return outcome;
        }
        finally
        {
            foreach (var directory in new[] { staging, prepared })
            {
                if (directory is null || !Directory.Exists(directory)) continue;
                try { Directory.Delete(directory, recursive: true); } catch (Exception) { }
            }
        }
    }

    private static int CopySharedPayload(
        string staging,
        string destination,
        ModPackage package,
        out long bytes)
    {
        bytes = 0;
        var copied = 0;
        var root = package.RootInArchive.Trim('/');

        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
            if (relative.Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsInsideExcludedRoot(relative, package, root)) continue;

            var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.Copy(file, target, overwrite: true);
            bytes += new FileInfo(target).Length;
            copied++;
        }

        return copied;
    }

    private static bool IsInsideExcludedRoot(string relative, ModPackage package, string packageRoot)
    {
        foreach (var excluded in package.ExcludeRoots)
        {
            var root = excluded.Trim('/');
            if (packageRoot.Length > 0 && root.StartsWith(packageRoot + "/", StringComparison.OrdinalIgnoreCase))
                root = root[(packageRoot.Length + 1)..];

            if (relative.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void WriteGeneratedModInfo(string folder, ModMetadata info)
    {
        static string Escape(string value) =>
            System.Security.SecurityElement.Escape(value) ?? string.Empty;

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8" ?>
            <ModInfo>
              <Name value="{Escape(info.Name)}" />
              <DisplayName value="{Escape(info.DisplayName)}" />
              <Version value="{Escape(info.Version)}" />
              <Author value="{Escape(info.Author)}" />
            </ModInfo>
            """;

        File.WriteAllText(Path.Combine(folder, "ModInfo.xml"), xml, new System.Text.UTF8Encoding(false));
    }

    private static bool IsContentRootAllowed(string packageRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (candidate.Contains("..", StringComparison.Ordinal)) return false;

        var root = packageRoot.Replace('\\', '/').Trim('/');
        if (root.Length == 0) return true;

        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static ModMetadata? TryReadMetadata(string folder)
    {
        var path = Path.Combine(folder, "ModInfo.xml");
        if (!File.Exists(path)) return null;

        try
        {
            return ModInfoParser.Parse(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
