using System.Diagnostics;
using SharpCompress.Common;
using SharpCompress.Writers;
using U7D2D.Core.Archives;
using U7D2D.Core.Data;
using U7D2D.Core.I18n;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Mods;

/// <summary>ZIP-based backups of mod folders, with metadata stored in SQLite.</summary>
public sealed class BackupService
{
    private readonly AppPaths _paths;
    private readonly BackupStore _store;

    public BackupService(AppPaths paths, BackupStore store)
    {
        _paths = paths;
        _store = store;
    }

    public string ResolveBackupsRoot(AppSettings settings)
    {
        var root = string.IsNullOrWhiteSpace(settings.BackupsPath)
            ? Path.Combine(_paths.Root, "Backups")
            : PathSafety.NormalizeDirectory(settings.BackupsPath);

        if (!PathSafety.IsDirectoryWritable(root))
            throw new IOException(Localization.SF("bak.folderNotWritable", root));

        return root;
    }

    public BackupRecord Create(string modFolderPath, AppSettings settings, string reason, ModMetadata? metadata = null)
    {
        if (!Directory.Exists(modFolderPath))
            throw new DirectoryNotFoundException(Localization.SF("bak2.modFolderMissing", modFolderPath));

        var backupsRoot = ResolveBackupsRoot(settings);
        var folderName = Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar));
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var archivePath = Path.Combine(backupsRoot, $"{folderName}-{stamp}.zip");

        var counter = 1;

        // Reserve the name atomically: two backups of the same folder in one second must not open
        // the same file for writing.
        while (true)
        {
            try
            {
                using (new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                break;
            }
            catch (IOException) when (counter < 1000)
            {
                archivePath = Path.Combine(backupsRoot, $"{folderName}-{stamp}-{counter}.zip");
                counter++;
            }
        }

        var options = new WriterOptions(CompressionType.Deflate) { LeaveStreamOpen = false };

        try
        {
            using (var writer = WriterFactory.OpenWriter(archivePath, ArchiveType.Zip, options))
            {
                foreach (var file in Directory.EnumerateFiles(modFolderPath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(modFolderPath, file).Replace('\\', '/');
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    writer.Write(relative, stream, File.GetLastWriteTimeUtc(file));
                }
            }
        }
        catch
        {
            // Do not leave a half-written archive in the list.
            try { File.Delete(archivePath); } catch (Exception) { }
            throw;
        }

        var record = new BackupRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ModFolder = folderName,
            ArchivePath = archivePath,
            SizeBytes = new FileInfo(archivePath).Length,
            ModName = metadata?.EffectiveName ?? folderName,
            ModVersion = metadata?.Version ?? string.Empty,
            Reason = reason,
            CreatedUtc = DateTime.UtcNow
        };

        _store.Add(record);
        return record;
    }

    public List<BackupRecord> List()
    {
        var records = _store.List();
        var available = new List<BackupRecord>();

        foreach (var record in records)
        {
            if (File.Exists(record.ArchivePath))
            {
                available.Add(record);
                continue;
            }

            // A missing file means the user deleted it only when its folder is still there. If the
            // folder itself is gone, the drive is offline: keep the record so the backup comes
            // back with the drive instead of vanishing from the list for good.
            var folder = Path.GetDirectoryName(record.ArchivePath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                _store.Remove(record.Id);
        }

        return available;
    }

    public RestoreOutcome Restore(BackupRecord record, AppSettings settings, bool createSafetyBackup = true)
    {
        var outcome = new RestoreOutcome();
        string? staging = null;
        var keepStaging = false;

        try
        {
            if (!File.Exists(record.ArchivePath))
            {
                outcome.Error = Localization.S("bak2.fileNotFound");
                return outcome;
            }

            if (string.IsNullOrWhiteSpace(settings.ModsPath))
            {
                outcome.Error = Localization.S("bak2.modsPathMissing");
                return outcome;
            }

            if (!PathSafety.IsDirectoryWritable(settings.ModsPath))
            {
                outcome.Error = Localization.SF("mod.modsNotWritable", settings.ModsPath);
                return outcome;
            }

            Directory.CreateDirectory(settings.ModsPath);
            var target = Path.Combine(settings.ModsPath, PathSafety.SanitizeFolderName(record.ModFolder, "Mod"));
            if (!PathSafety.IsUnderRoot(settings.ModsPath, target))
            {
                outcome.Error = Localization.S("bak2.invalidRestorePath");
                return outcome;
            }

            outcome.TargetPath = target;

            if (Directory.Exists(target) && createSafetyBackup)
            {
                var safety = Create(target, settings,
                    Localization.SF("bak2.beforeRestore", record.CreatedUtc.ToString("yyyy-MM-dd HH:mm")));
                outcome.SafetyBackupCreated = true;
                outcome.SafetyBackupPath = safety.ArchivePath;
            }

            staging = _paths.CreateTempDirectory("restore");
            using (var handle = ArchiveReader.Open(record.ArchivePath, _paths.TempDirectory))
            {
                var extraction = ArchiveReader.ExtractSubtree(handle, string.Empty, staging, overwrite: true);
                if (!extraction.Success)
                {
                    outcome.Error = Localization.SF("bak2.extractFailed", extraction.Error ?? string.Empty);
                    return outcome;
                }
                outcome.FilesWritten = extraction.Files.Count;

                if (extraction.SkippedUnsafe.Count > 0)
                    outcome.Warnings.Add(Localization.SF("mod.unsafeSkipped", extraction.SkippedUnsafe.Count));
            }

            if (outcome.FilesWritten == 0)
            {
                outcome.Error = Localization.S("bak2.emptyBackup");
                return outcome;
            }

            var commit = FileOps.CommitDirectory(staging, target);

            if (!commit.Success)
            {
                outcome.RolledBack = commit.RolledBack;
                outcome.Error = DescribeCommitFailure(commit);

                // The folder is gone and the old copy could not be moved back. Rebuild it from the
                // safety backup taken a moment ago, so the user never ends up with neither version.
                if (!commit.RolledBack && outcome.SafetyBackupPath is { } safety && File.Exists(safety))
                {
                    if (TryRecoverFromSafety(safety, target, out var recoveryError))
                    {
                        outcome.RecoveredFromSafety = true;
                        outcome.Error = Localization.S("bak2.safetyRecovered") + " " + outcome.Error;
                    }
                    else
                    {
                        outcome.Error = Localization.SF("bak2.safetyRecoveryFailed", safety) +
                                        (string.IsNullOrWhiteSpace(recoveryError) ? string.Empty : " " + recoveryError);
                    }

                    keepStaging = !outcome.RecoveredFromSafety;
                }
                else
                {
                    keepStaging = !commit.RolledBack;
                }

                if (commit.LikelyLocked)
                    outcome.Error += " " + Localization.S("mod.lockedHint");

                return outcome;
            }

            if (commit.Warning is not null) outcome.Warnings.Add(commit.Warning);

            FileOps.TryDeleteDirectory(staging);
            staging = null;
            outcome.Success = true;
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
        }
    }

    private static string DescribeCommitFailure(FileOps.DirectoryCommitResult commit)
    {
        var text = commit.Error ?? Localization.S("bak2.replaceFailed");
        if (commit.RolledBack) text += " " + Localization.S("bak2.restoreRolledBack");
        return text;
    }

    /// <summary>Rebuilds the target from the safety backup after a failed swap.</summary>
    private bool TryRecoverFromSafety(string safetyArchive, string target, out string? error)
    {
        error = null;
        string? recovery = null;

        try
        {
            recovery = _paths.CreateTempDirectory("recover");

            using (var handle = ArchiveReader.Open(safetyArchive, _paths.TempDirectory))
            {
                var extraction = ArchiveReader.ExtractSubtree(handle, string.Empty, recovery, overwrite: true);
                if (!extraction.Success || extraction.Files.Count == 0)
                {
                    error = extraction.Error;
                    return false;
                }
            }

            var commit = FileOps.CommitDirectory(recovery, target);
            if (!commit.Success)
            {
                error = commit.Error;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (recovery is not null) FileOps.TryDeleteDirectory(recovery);
        }
    }

    public bool Delete(BackupRecord record)
    {
        try
        {
            if (File.Exists(record.ArchivePath)) File.Delete(record.ArchivePath);
            _store.Remove(record.Id);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
