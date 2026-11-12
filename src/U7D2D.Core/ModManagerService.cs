using System.Collections.Concurrent;
using System.Diagnostics;
using U7D2D.Core.Analysis;
using U7D2D.Core.Archives;
using U7D2D.Core.Data;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;
using U7D2D.Core.Nexus;

namespace U7D2D.Core;

/// <summary>High-level facade used by the UI. All operations are real and write to disk/SQLite.</summary>
public sealed class ModManagerService
{
    private readonly AppPaths _paths;

    /// <summary>Folders the manager works with. Used by the diagnostics report.</summary>
    public AppPaths Paths => _paths;
    private readonly SettingsStore _settingsStore;
    private readonly CatalogStore _catalog;
    private readonly InstalledStore _installedStore;
    private readonly LogStore _log;
    private readonly ArchiveScanner _scanner;
    private readonly InstalledModScanner _installedScanner;
    private readonly ModInstaller _installer;
    private readonly BackupService _backups;
    private readonly GameProbe _probe;
    private readonly CompatibilityAnalyzer _compat;
    private readonly ConflictAnalyzer _conflicts;
    private readonly ModConfigService _configs;
    private readonly NexusUpdateService _nexus;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModManagerService(
        AppPaths paths,
        SettingsStore settingsStore,
        CatalogStore catalog,
        InstalledStore installedStore,
        BackupStore backupStore,
        LogStore log,
        ArchiveScanner scanner,
        InstalledModScanner installedScanner,
        ModInstaller installer,
        BackupService backups,
        GameProbe probe,
        CompatibilityAnalyzer compat,
        ConflictAnalyzer conflicts,
        ModConfigService configs,
        NexusUpdateService nexus)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _catalog = catalog;
        _installedStore = installedStore;
        _log = log;
        _scanner = scanner;
        _installedScanner = installedScanner;
        _installer = installer;
        _backups = backups;
        _probe = probe;
        _compat = compat;
        _conflicts = conflicts;
        _configs = configs;
        _nexus = nexus;
        Settings = settingsStore.Load();
        _paths.CleanupTemp();
    }

    public AppSettings Settings { get; private set; }
    public string DatabasePath => _paths.DatabasePath;
    public string AppDataRoot => _paths.Root;

    public AppSettings ReloadSettings()
    {
        Settings = _settingsStore.Load();
        return Settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        _settingsStore.Save(settings);
        _log.Add("settings", Localization.S("svc.settingsSaved"),
            Localization.SF("svc.settingsSavedDetail", settings.GamePath, settings.ModsPath, settings.ArchivePaths.Count));
    }

    public GameInstallInfo InspectGame()
    {
        var info = _probe.Inspect(Settings.GamePath, Settings.GameVersionOverride);
        if (info.IsValid && string.IsNullOrWhiteSpace(Settings.ModsPath) && info.ModsPath is not null)
        {
            Settings.ModsPath = info.ModsPath;
            _settingsStore.Save(Settings);
        }
        return info;
    }

    public async Task<ScanSummary> ScanArchivesAsync(
        IProgress<ScanProgressInfo>? progress = null,
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var summary = new ScanSummary();
            var roots = Settings.ValidArchivePaths.ToList();

            if (roots.Count == 0)
            {
                summary.SkippedFolders.Add(Localization.S("svc.noArchiveFolders"));
                return summary;
            }

            var files = ArchiveLocator.EnumerateArchives(
                roots, Settings.ScanRecursively, Settings.MaxScanDepth, progress, summary.SkippedFolders, cancellationToken);

            var results = new ConcurrentBag<ArchiveScanResult>();
            var completed = 0;
            var total = files.Count;

            progress?.Report(new ScanProgressInfo
            {
                Phase = ScanPhase.Preparing,
                Total = total,
                Message = Localization.SF("svc.foundArchives", total)
            });

            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
                CancellationToken = cancellationToken
            }, (file, token) =>
            {
                ArchiveScanResult result;

                if (useCache)
                {
                    var info = new FileInfo(file.Path);
                    var cached = _catalog.TryGetCached(file.Path, info.Length, info.LastWriteTimeUtc, ArchiveScanner.Version, file.SourceRoot);
                    if (cached is not null)
                    {
                        result = cached;
                        results.Add(result);
                        ReportProgress(progress, file.Path, Interlocked.Increment(ref completed), total, cached.Mods.Count);
                        return ValueTask.CompletedTask;
                    }
                }

                result = _scanner.Scan(file.Path, file.SourceRoot, token);
                results.Add(result);
                try { _catalog.Save(result); } catch (Exception) { /* cache is best-effort */ }

                ReportProgress(progress, file.Path, Interlocked.Increment(ref completed), total, result.Mods.Count);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            summary.Archives = results.OrderBy(r => r.ArchivePath, StringComparer.OrdinalIgnoreCase).ToList();
            summary.AllMods = summary.Archives.Where(a => a.Success).SelectMany(a => a.Mods)
                .OrderBy(m => m.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            summary.Duration = stopwatch.Elapsed;

            _catalog.RemoveStale(files.Select(f => f.Path));
            _log.Add("scan",
                Localization.SF("svc.scanDone", summary.ArchivesScanned, summary.ModsFound),
                Localization.SF("svc.scanDoneDetail", summary.ArchivesFailed, roots.Count));

            progress?.Report(new ScanProgressInfo
            {
                Phase = ScanPhase.Completed,
                Current = total,
                Total = total,
                Message = Localization.SF("svc.scanComplete", summary.ModsFound, summary.ArchivesScanned)
            });

            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ReportProgress(IProgress<ScanProgressInfo>? progress, string file, int completed, int total, int mods)
    {
        progress?.Report(new ScanProgressInfo
        {
            Phase = ScanPhase.DetectingMods,
            Current = completed,
            Total = total,
            Item = Path.GetFileName(file),
            Message = Localization.SF("svc.progress", completed, total, mods)
        });
    }

    /// <summary>Cached scan results for instant library population on startup.</summary>
    public ScanSummary GetCachedScanSummary()
    {
        var summary = new ScanSummary();
        try
        {
            summary.Archives = _catalog.LoadAll(ArchiveScanner.Version)
                .OrderBy(r => r.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return summary;
        }

        summary.AllMods = summary.Archives.Where(a => a.Success).SelectMany(a => a.Mods)
            .OrderBy(m => m.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ArchiveFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return summary;
    }

    public List<InstalledMod> GetInstalled()
    {
        var mods = _installedScanner.Scan(Settings.ModsPath);
        return mods.OrderBy(m => m.DisplayTitle, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public InstallOutcome Install(ModPackage package, InstallOptions options, IProgress<InstallProgress>? progress = null)
    {
        var outcome = _installer.Install(package, Settings, options, progress);

        _log.Add(outcome.Success ? "install" : "install-failed",
            outcome.Success
                ? Localization.SF("svc.installOk", outcome.ModName, outcome.TargetPath)
                : Localization.SF("svc.installFailed", package.DisplayTitle, outcome.Error ?? string.Empty),
            Localization.SF("svc.installDetail", outcome.FilesWritten,
                outcome.BackupCreated ? outcome.BackupPath ?? string.Empty : Localization.S("svc.none"), outcome.ReplacedExisting));

        return outcome;
    }

    public List<InstallOutcome> InstallMany(
        IEnumerable<ModPackage> packages,
        InstallOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<InstallOutcome>();
        var list = packages.ToList();

        foreach (var package in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress { Stage = Localization.SF("svc.installing", package.DisplayTitle) });
            outcomes.Add(Install(package, options, progress));
        }

        return outcomes;
    }

    public UninstallOutcome Uninstall(InstalledMod mod, bool createBackup = true)
    {
        var outcome = _installer.Uninstall(mod, Settings, createBackup);

        _log.Add(outcome.Success ? "uninstall" : "uninstall-failed",
            outcome.Success
                ? Localization.SF("svc.uninstallOk", outcome.ModName)
                : Localization.SF("svc.uninstallFailed", mod.DisplayTitle, outcome.Error ?? string.Empty),
            Localization.SF("svc.uninstallDetail", outcome.FolderPath,
                outcome.BackupCreated ? outcome.BackupPath ?? string.Empty : Localization.S("svc.none")));

        return outcome;
    }

    public List<BackupRecord> GetBackups() => _backups.List();

    public BackupRecord CreateBackup(InstalledMod mod)
    {
        var record = _backups.Create(mod.FullPath, Settings, Localization.S("svc.manualBackup"), mod.Info);
        _log.Add("backup", Localization.SF("svc.backupCreated", mod.DisplayTitle), record.ArchivePath);
        return record;
    }

    public RestoreOutcome Restore(BackupRecord record)
    {
        var outcome = _backups.Restore(record, Settings);
        _log.Add(outcome.Success ? "restore" : "restore-failed",
            outcome.Success
                ? Localization.SF("svc.restoreOk", record.ModName, record.CreatedUtc.ToString("yyyy-MM-dd HH:mm"))
                : Localization.SF("svc.restoreFailed", outcome.Error ?? string.Empty),
            Localization.SF("svc.restoreDetail", outcome.TargetPath, outcome.FilesWritten));
        return outcome;
    }

    public bool DeleteBackup(BackupRecord record)
    {
        var deleted = _backups.Delete(record);
        _log.Add("backup-delete",
            deleted
                ? Localization.SF("svc.backupDeleted", record.ArchivePath)
                : Localization.SF("svc.backupDeleteFailed", record.ArchivePath),
            string.Empty);
        return deleted;
    }

    public CompatibilityReport Analyze(ModPackage package, CancellationToken cancellationToken = default)
    {
        var game = InspectGame();
        var installed = GetInstalled();
        var report = _compat.Analyze(package, Settings, game, installed, cancellationToken);

        _log.Add("analysis",
            Localization.SF("svc.analysisLog", package.DisplayTitle, VerdictText(report.Verdict), report.Confidence.ToString("P0")),
            string.Join(" | ", report.Findings.Take(6).Select(f => $"{f.Severity}: {f.Title}")));

        return report;
    }

    public ConflictReport AnalyzeConflicts()
    {
        var resolver = new VanillaConfigResolver(Settings.GamePath);
        var report = _conflicts.AnalyzeInstalled(GetInstalled(), resolver);
        _log.Add("conflicts", Localization.SF("svc.conflictsLog", report.Conflicts.Count),
            string.Join(" | ", report.Conflicts.Take(5).Select(c => $"{c.Severity}: {c.Subject}")));
        return report;
    }

    public ConflictReport AnalyzePackageConflicts(ModPackage package) =>
        _conflicts.AnalyzePackage(package, GetInstalled());

    private static string VerdictText(CompatibilityVerdict verdict) => verdict switch
    {
        CompatibilityVerdict.Incompatible => Localization.S("svc.verdictIncompatible"),
        CompatibilityVerdict.LikelyIncompatible => Localization.S("svc.verdictLikelyIncompatible"),
        CompatibilityVerdict.Risky => Localization.S("svc.verdictRisky"),
        CompatibilityVerdict.LikelyCompatible => Localization.S("svc.verdictLikelyCompatible"),
        CompatibilityVerdict.Compatible => Localization.S("svc.verdictCompatible"),
        _ => Localization.S("svc.verdictUnknown")
    };

    // ---------------------------------------------------------------- mod configs

    public IReadOnlyList<ModConfigFile> GetModConfigs(InstalledMod mod) => _configs.ListConfigs(mod);

    public ModConfigDocument ReadModConfig(ModConfigFile file) => _configs.Read(file);

    public ConfigSaveResult SaveModConfig(ModConfigFile file, InstalledMod mod, IReadOnlyList<ConfigFieldChange> changes)
    {
        if (!PathSafety.IsUnderRoot(mod.FullPath, file.FullPath))
        {
            return new ConfigSaveResult { Error = Localization.S("svc.fileOutsideMod") };
        }

        var result = _configs.Save(file, mod.FolderName, changes);

        _log.Add("config",
            result.Success
                ? Localization.SF("svc.configSaved", file.DisplayName, mod.DisplayTitle)
                : Localization.SF("svc.configSaveFailed", file.DisplayName, result.Error ?? string.Empty),
            result.Success
                ? Localization.SF("svc.configSavedDetail", result.Changed, result.BackupPath ?? Localization.S("svc.none"))
                : string.Empty);

        return result;
    }

    public List<OperationLogEntry> GetLog(int limit = 200) => _log.List(limit);

    public void ClearCatalog()
    {
        _catalog.Clear();
        _log.Add("catalog", Localization.S("svc.catalogCleared"), string.Empty);
    }

    public void ClearLog() => _log.Clear();

    // ---------------------------------------------------------------- Nexus Mods

    /// <summary>Returns the plain API key for display/editing. It is decrypted in memory only.</summary>
    public string GetNexusApiKey() => NexusKeyProtection.Unprotect(Settings.NexusApiKey);

    public bool HasNexusApiKey => !string.IsNullOrWhiteSpace(GetNexusApiKey());

    public void SaveNexusApiKey(string plainKey)
    {
        Settings.NexusApiKey = NexusKeyProtection.Protect(plainKey.Trim());
        _settingsStore.Save(Settings);
        _log.Add("nexus", string.IsNullOrWhiteSpace(plainKey)
            ? Localization.S("svc.nexusKeyRemoved")
            : Localization.S("svc.nexusKeySaved"), string.Empty);
    }

    public Task<NexusApiResult<NexusUserInfo>> ValidateNexusKeyAsync(
        string plainKey, CancellationToken ct = default) =>
        _nexus.ValidateKeyAsync(plainKey, ct);

    /// <summary>
    /// Checks installed mods against Nexus. Called ONLY from the explicit UI button.
    /// Never downloads anything and never touches the game's Mods folder.
    /// </summary>
    public Task<NexusCheckReport> CheckNexusUpdatesAsync(
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        _nexus.CheckAsync(GetInstalled(), GetNexusApiKey(), progress, ct);

    public Dictionary<string, NexusLink> GetNexusLinks() => _nexus.GetLinks();

    public void SetNexusLink(string folderName, int modId, string? gameDomain = null) =>
        _nexus.SetLink(folderName, modId, gameDomain);

    public void RemoveNexusLink(string folderName) => _nexus.RemoveLink(folderName);

    public List<NexusModUpdate> GetCachedNexusResults() => _nexus.GetCachedResults();

    public void ClearNexusResults() => _nexus.ClearCachedResults();
}
