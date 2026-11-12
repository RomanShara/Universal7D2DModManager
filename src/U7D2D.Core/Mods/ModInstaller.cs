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
        var stopwatch = Stopwatch.StartNew();
        var outcome = new InstallOutcome { ModName = package.DisplayTitle };
        string? staging = null;

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

            progress?.Report(new InstallProgress { Stage = Localization.S("mod.stageExtract") });
            staging = _paths.CreateTempDirectory("install");

            using (var handle = ArchiveReader.Open(package.ArchivePath, _paths.TempDirectory, cancellationToken))
            {
                var extraction = ArchiveReader.ExtractSubtree(
                    handle,
                    package.RootInArchive,
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

            if (Directory.Exists(target) && !FileOps.DeleteDirectory(target))
            {
                outcome.Error = Localization.S("mod.replaceFailed");
                return outcome;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            FileOps.MoveDirectory(staging, target);
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
            if (staging is not null) FileOps.TryDeleteDirectory(staging);
            outcome.Duration = stopwatch.Elapsed;
        }
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

            if (!FileOps.DeleteDirectory(mod.FullPath))
            {
                outcome.Error = Localization.S("mod.deleteFailed");
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
