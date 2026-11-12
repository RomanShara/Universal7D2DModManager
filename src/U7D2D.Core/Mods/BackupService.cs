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
            : settings.BackupsPath;
        Directory.CreateDirectory(root);
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
        while (File.Exists(archivePath))
        {
            archivePath = Path.Combine(backupsRoot, $"{folderName}-{stamp}-{counter}.zip");
            counter++;
        }

        var options = new WriterOptions(CompressionType.Deflate) { LeaveStreamOpen = false };
        using (var writer = WriterFactory.OpenWriter(archivePath, ArchiveType.Zip, options))
        {
            foreach (var file in Directory.EnumerateFiles(modFolderPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(modFolderPath, file).Replace('\\', '/');
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                writer.Write(relative, stream, File.GetLastWriteTimeUtc(file));
            }
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
        foreach (var record in records.Where(r => !File.Exists(r.ArchivePath)).ToList())
            _store.Remove(record.Id);

        return records.Where(r => File.Exists(r.ArchivePath)).ToList();
    }

    public RestoreOutcome Restore(BackupRecord record, AppSettings settings, bool createSafetyBackup = true)
    {
        var outcome = new RestoreOutcome();
        string? staging = null;

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
            }

            if (outcome.FilesWritten == 0)
            {
                outcome.Error = Localization.S("bak2.emptyBackup");
                return outcome;
            }

            if (Directory.Exists(target) && !FileOps.DeleteDirectory(target))
            {
                outcome.Error = Localization.S("bak2.replaceFailed");
                return outcome;
            }

            FileOps.MoveDirectory(staging, target);
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
            if (staging is not null) FileOps.TryDeleteDirectory(staging);
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
