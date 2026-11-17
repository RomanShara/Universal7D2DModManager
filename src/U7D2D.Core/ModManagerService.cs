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
    private readonly NexusOAuthService _nexusOAuth;
    private readonly RootChoiceStore _rootChoices;
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
        NexusUpdateService nexus,
        NexusOAuthService nexusOAuth,
        RootChoiceStore rootChoices)
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
        _nexusOAuth = nexusOAuth;
        _rootChoices = rootChoices;
        Settings = settingsStore.Load();
        _paths.CleanupTemp();
    }

    /// <summary>Root the player picked for this archive before, if the archive is unchanged.</summary>
    public (string Name, string ContentRoot)? GetRememberedRoot(string fingerprint) =>
        _rootChoices.Get(fingerprint);

    /// <summary>Stores the choice so a reinstall of the same archive does not ask again.</summary>
    public void RememberRoot(string fingerprint, string name, string contentRoot) =>
        _rootChoices.Save(fingerprint, name, contentRoot);

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

    /// <summary>
    /// File versions the player already downloaded, keyed by Nexus mod id. Read from archive names
    /// the site generated ("Onsoku 12249 3.4 2026-10-11T..."), so a build sitting in the library is
    /// not reported as an available update.
    /// </summary>
    public IReadOnlyDictionary<int, List<string>> GetLibraryVersions()
    {
        var map = new Dictionary<int, List<string>>();
        try
        {
            foreach (var archive in _catalog.LoadAll(ArchiveScanner.Version))
            {
                if (!NexusArchiveName.TryRead(archive.ArchivePath, out var id, out var version)) continue;
                if (!map.TryGetValue(id, out var versions)) map[id] = versions = new List<string>();
                if (!versions.Contains(version, StringComparer.OrdinalIgnoreCase)) versions.Add(version);
            }
        }
        catch (Exception)
        {
            // The library is optional: without it the verdict falls back to version comparison.
        }

        return map;
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

    /// <summary>
    /// True when the user pointed the backups folder at a place that is not reachable right now
    /// (an unplugged drive, a network share). Existing records are kept; the page says so instead
    /// of pretending there are no backups.
    /// </summary>
    public bool BackupsFolderMissing =>
        !string.IsNullOrWhiteSpace(Settings.BackupsPath) && !Directory.Exists(Settings.BackupsPath);

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

    /// <summary>
    /// Reads the game logs, maps stack frames to the mods that define those types and reports what
    /// went wrong. Nothing is written to the game folder.
    ///
    /// The method is synchronous and does not touch the database: the caller runs it on a worker
    /// thread and writes the operation log itself, so the UI thread never shares the connection.
    /// </summary>
    public Logs.LogDiagnosisReport AnalyzeLogsCore(
        string gamePath,
        string modsPath,
        IProgress<Logs.LogProgress>? progress = null,
        CancellationToken ct = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var report = new Logs.LogDiagnosisReport();

        var files = Logs.GameLogLocator.Locate(gamePath)
            .Take(2)
            .ToList();

        if (files.Count == 0)
        {
            report.Error = Localization.S("log.noFiles");
            return report;
        }

        var dllTotal = CountDlls(modsPath);
        var dllDone = 0;

        progress?.Report(new Logs.LogProgress
        {
            Stage = Localization.S("logs.stageIndex"), Done = 0, Total = dllTotal
        });

        var dllProgress = new DirectProgress<int>(done =>
        {
            dllDone = done;
            progress?.Report(new Logs.LogProgress
            {
                Stage = Localization.S("logs.stageIndex"), Done = done, Total = dllTotal
            });
        });

        var index = Logs.ModTypeIndex.BuildFromFolder(
            modsPath,
            Path.Combine(_paths.CacheDirectory, "log-index-v2.json"),
            ct,
            dllProgress);

        report.IndexDuration = watch.Elapsed;

        foreach (var (dll, count) in index.SkippedAttributes)
            report.IndexWarnings.Add(Localization.SF("log.skippedHarmony", count, dll));
        _ = dllDone;

        var entries = new List<Logs.LogEntry>();
        var done = 0;

        // The current log feeds the comparison while it is parsed: reading the same large file a
        // second time just to collect mod names doubled the read stage.
        Logs.LogComparison.ScanState? currentScan = null;

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            ct.ThrowIfCancellationRequested();

            progress?.Report(new Logs.LogProgress
            {
                Stage = Localization.S("logs.stageRead"),
                Done = done,
                Total = files.Count
            });

            var scan = i == 0 ? new Logs.LogComparison.ScanState() : null;
            Action<string>? observe = null;
            if (scan is not null) observe = scan.Observe;

            try
            {
                // Only relevant entries are kept, so a huge log does not fill memory with noise.
                var parsed = Logs.GameLogParser.Parse(
                    Logs.GameLogReader.ReadLines(file.Path),
                    out var lines,
                    Logs.LogAnalyzer.IsRelevant,
                    ct,
                    observe);

                entries.AddRange(parsed);
                report.Sources.Add($"{file.Label}: {Path.GetFileName(file.Path)}");
                report.LinesRead += lines;

                if (scan is not null) currentScan = scan;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report.Skipped.Add($"{Path.GetFileName(file.Path)}: {e.Message}");
            }

            done++;
        }

        report.ReadDuration = watch.Elapsed - report.IndexDuration;

        progress?.Report(new Logs.LogProgress { Stage = Localization.S("logs.stageAnalyze"), Done = 0, Total = 1 });
        ct.ThrowIfCancellationRequested();

        var analyzed = new Logs.LogAnalyzer().Analyze(entries, index);

        try
        {
            // No scan state means the current log could not be read, so there is nothing to
            // compare and no reason to open that file again.
            if (currentScan is { } scanState)
            {
                var current = scanState.Build();
                var previous = files.Count > 1
                    ? Logs.LogComparison.Scan(Logs.GameLogReader.ReadLines(files[1].Path), ct).Mods
                    : null;

                var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (assembly, folder) in index.AssemblyFolders) aliases[assembly] = folder;
                foreach (var (declared, folder) in index.ModInfoAliases) aliases[declared] = folder;

                report.Comparison = Logs.LogComparison.Build(
                    current.Mods, previous, current.FirstError, index.ModFolders, aliases);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The comparison is a bonus: without it the report is still useful.
        }

        report.CompareDuration = watch.Elapsed - report.IndexDuration - report.ReadDuration;

        report.Findings.AddRange(analyzed.Findings);
        report.Notes.AddRange(analyzed.Notes);
        report.TotalFindings = analyzed.TotalFindings;
        report.ModsIndexed = index.ModCount;
        report.TypesIndexed = index.TypeCount;
        report.PatchTargetsIndexed = index.PatchTargetCount;
        report.Duration = watch.Elapsed;

        return report;
    }

    private static int CountDlls(string modsPath)
    {
        try
        {
            return Directory.Exists(modsPath)
                ? Directory.EnumerateFiles(modsPath, "*.dll", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Writes the operation log entry. Called on the caller's thread, never from a worker.</summary>
    public void RecordLogAnalysis(Logs.LogDiagnosisReport report, int files) =>
        _log.Add("log", Localization.SF("log.logged", report.Findings.Count, files),
            report.IndexWarnings.FirstOrDefault());

    /// <summary>Convenience wrapper for the command line, where there is no UI thread to protect.</summary>
    public Logs.LogDiagnosisReport AnalyzeLogs()
    {
        var report = AnalyzeLogsCore(Settings.GamePath, Settings.ModsPath);
        RecordLogAnalysis(report, report.Sources.Count);
        return report;
    }

    public List<OperationLogEntry> GetLog(int limit = 200) => _log.List(limit);

    public void ClearCatalog()
    {
        _catalog.Clear();
        _log.Add("catalog", Localization.S("svc.catalogCleared"), string.Empty);
    }

    public void ClearLog() => _log.Clear();

    // ---------------------------------------------------------------- Nexus Mods

    /// <summary>True when OAuth tokens are stored (the user signed in at least once).</summary>
    public bool HasNexusAccount => _nexusOAuth.IsSignedIn;

    /// <summary>Nexus username recorded at the last successful validation, for display.</summary>
    public string NexusAccountName => _nexusOAuth.AccountName ?? string.Empty;

    /// <summary>Effective OAuth client id: saved override, then environment, then build default.</summary>
    public string NexusOAuthClientId => _nexusOAuth.ClientId;

    /// <summary>True once a real client id is available, so the sign-in button can work.</summary>
    public bool HasNexusOAuthClientId => _nexusOAuth.IsConfigured;

    /// <summary>
    /// Stores the client id issued by Nexus so sign-in works without rebuilding the app. The id is
    /// public, not a secret.
    /// </summary>
    public void SetNexusOAuthClientId(string? clientId)
    {
        Settings.NexusOAuthClientId = (clientId ?? string.Empty).Trim();
        _settingsStore.Save(Settings);
        _log.Add("nexus", string.IsNullOrWhiteSpace(Settings.NexusOAuthClientId)
            ? Localization.S("svc.nexusClientIdCleared")
            : Localization.S("svc.nexusClientIdSaved"), string.Empty);
    }

    /// <summary>
    /// Runs the interactive OAuth 2.0 + PKCE sign-in. <paramref name="openUrl"/> opens the Nexus
    /// authorization page in the default browser. No personal API key is ever requested.
    /// </summary>
    public async Task<NexusOAuthResult> SignInToNexusAsync(
        Action<string> openUrl, CancellationToken ct = default)
    {
        var result = await _nexusOAuth.SignInAsync(openUrl, ct);

        _log.Add("nexus",
            result.Success
                ? Localization.S("svc.nexusSignedIn")
                : result.Cancelled
                    ? Localization.S("svc.nexusSignInCancelled")
                    : Localization.SF("svc.nexusSignInFailed", result.Error ?? string.Empty),
            string.Empty);

        return result;
    }

    /// <summary>Clears stored tokens. The user can still browse Nexus links, just not check updates.</summary>
    public void SignOutOfNexus()
    {
        _nexusOAuth.SignOut();
        _log.Add("nexus", Localization.S("svc.nexusSignedOut"), string.Empty);
    }

    /// <summary>Confirms the signed-in account works and refreshes the cached display name.</summary>
    public Task<NexusApiResult<NexusUserInfo>> ValidateNexusAccountAsync(CancellationToken ct = default) =>
        _nexus.ValidateAccountAsync(ct);

    /// <summary>
    /// Checks installed mods against Nexus. Called ONLY from the explicit UI button.
    /// Never downloads anything and never touches the game's Mods folder.
    /// </summary>
    public Task<NexusCheckReport> CheckNexusUpdatesAsync(
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        _nexus.CheckAsync(GetInstalled(), progress, ct);

    public Dictionary<string, NexusLink> GetNexusLinks() => _nexus.GetLinks();

    public void SetNexusLink(string folderName, int modId, string? gameDomain = null) =>
        _nexus.SetLink(folderName, modId, gameDomain);

    public void RemoveNexusLink(string folderName) => _nexus.RemoveLink(folderName);

    public List<NexusModUpdate> GetCachedNexusResults() => _nexus.GetCachedResults();

    public void ClearNexusResults() => _nexus.ClearCachedResults();
}
