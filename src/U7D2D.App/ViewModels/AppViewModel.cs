using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using U7D2D.App.Services;
using U7D2D.Core;
using U7D2D.Core.Analysis;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;
using U7D2D.Core.Nexus;

namespace U7D2D.App.ViewModels;

public partial class AppViewModel : ObservableObject
{
    private readonly ModManagerService _manager;
    private readonly IFolderPicker _picker;
    private readonly IShellService _shell;
    private readonly ToastService _toasts;
    private readonly DialogService _dialogs;

    public AppViewModel(
        ModManagerService manager,
        IFolderPicker picker,
        IShellService shell,
        ToastService toasts,
        DialogService dialogs)
    {
        _manager = manager;
        _picker = picker;
        _shell = shell;
        _toasts = toasts;
        _dialogs = dialogs;

        Settings = manager.ReloadSettings();
        SetupRequired = !Settings.SetupCompleted;
        NexusApiKeyInput = manager.GetNexusApiKey();

        // Applied here, before the first render, so the shell and the pages start in the same language.
        ApplyStoredLanguage();

        // Every view model instance forwards the shared language change, so all components re-render
        // no matter which instance they were given.
        // Every view model instance forwards the shared language change, so all components re-render
        // no matter which instance they were given.
        Localization.Changed += OnLanguageChanged;
    }

    public ObservableCollection<ModCardViewModel> AvailableMods { get; } = new();
    public ObservableCollection<ModGroupViewModel> LibraryGroups { get; } = new();

    private readonly HashSet<string> _cardKeys = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<ModCardViewModel> FilteredMods { get; } = new();
    public ObservableCollection<InstalledCardViewModel> InstalledMods { get; } = new();
    public ObservableCollection<BackupRecord> Backups { get; } = new();

    public ObservableCollection<ConfigModEntry> ConfigMods { get; } = new();
    public ObservableCollection<ModConfigFile> ConfigFiles { get; } = new();
    public ObservableCollection<ConfigFieldViewModel> ConfigFields { get; } = new();
    public ObservableCollection<OperationLogEntry> LogEntries { get; } = new();

    [ObservableProperty] private AppSettings _settings;
    [ObservableProperty] private GameInstallInfo? _game;
    [ObservableProperty] private bool _setupRequired;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _libraryFilter = "all";
    [ObservableProperty] private string _sortMode = "name";
    [ObservableProperty] private ScanSummary? _scan;
    [ObservableProperty] private CompatibilityReport? _report;
    [ObservableProperty] private ConflictReport? _conflicts;
    [ObservableProperty] private ModPackage? _analysisTarget;
    [ObservableProperty] private bool _isInitialized;
    [ObservableProperty] private ConfigModEntry? _configMod;
    [ObservableProperty] private ModConfigFile? _configFile;
    [ObservableProperty] private bool _isConfigLoading;
    [ObservableProperty] private string? _configNotice;

    public bool HasConfigChanges => ConfigFields.Any(f => f.IsDirty);

    public int SelectedCount => AvailableMods.Count(m => m.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public int InstalledCount => InstalledMods.Count;
    public int AvailableCount => AvailableMods.Count;
    public int ArchivesCount => Scan?.ArchivesScanned ?? 0;
    public int FailedArchives => Scan?.ArchivesFailed ?? 0;
    public int BackupsCount => Backups.Count;
    public int ConflictsCount => Conflicts?.Conflicts.Count ?? 0;
    public int UpdatesCount => AvailableMods.Count(m => m.UpdateAvailable);

    public string GameVersionText => Game?.VersionString ?? T("vm.versionUndefined");
    public bool GameReady => Game?.IsValid == true;
    public bool GameDeepAnalysis => !string.IsNullOrEmpty(Game?.ManagedAssemblyPath);
    public string ModsPathText => string.IsNullOrWhiteSpace(Settings.ModsPath) ? T("vm.pathNotSet") : Settings.ModsPath;
    public bool IsEmptyLibrary => AvailableMods.Count == 0;
    public bool CanScan => !IsScanning && Settings.ValidArchivePaths.Any();

    public async Task InitializeAsync()
    {
        if (IsInitialized) return;
        IsInitialized = true;

        await Task.Run(() =>
        {
            Game = _manager.InspectGame();
            Settings = _manager.ReloadSettings();
        });
        NexusApiKeyInput = _manager.GetNexusApiKey();

        await RefreshInstalledAsync();
        await RefreshBackupsAsync();
        await RefreshLogAsync();
        await LoadCachedCatalogAsync();
        NotifyStats();

        // Components render before initialization finishes, so one explicit notification here makes
        // sure every screen shows the language that is actually active.
        Localization.NotifyChanged();
    }

    /// <summary>Populates the library from the previous scan so the app is useful right after start.</summary>
    private async Task LoadCachedCatalogAsync()
    {
        if (AvailableMods.Count > 0) return;

        var cached = await Task.Run(() => _manager.GetCachedScanSummary());
        if (cached.AllMods.Count == 0) return;

        Scan = cached;
        ResetCards();
        foreach (var package in cached.AllMods) TryAddCard(new ModCardViewModel(package));

        ApplyInstallState();
        RefreshFilter();
    }

    public void NotifyStats()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(ArchivesCount));
        OnPropertyChanged(nameof(FailedArchives));
        OnPropertyChanged(nameof(BackupsCount));
        OnPropertyChanged(nameof(ConflictsCount));
        OnPropertyChanged(nameof(UpdatesCount));
        OnPropertyChanged(nameof(GameVersionText));
        OnPropertyChanged(nameof(GameReady));
        OnPropertyChanged(nameof(GameDeepAnalysis));
        OnPropertyChanged(nameof(ModsPathText));
        OnPropertyChanged(nameof(IsEmptyLibrary));
        OnPropertyChanged(nameof(CanScan));
    }

    // ---------------------------------------------------------------- paths

    public async Task PickPathAsync(string kind)
    {
        var initial = kind switch
        {
            "game" => Settings.GamePath,
            "mods" => Settings.ModsPath,
            "backups" => Settings.BackupsPath,
            _ => string.Empty
        };

        var title = kind switch
        {
            "game" => T("vm.pickGameFolder"),
            "mods" => T("vm.pickModsFolder"),
            "backups" => T("vm.pickBackupsFolder"),
            _ => T("vm.pickFolder")
        };

        var selected = _picker.PickFolder(title, initial);
        if (selected is null) return;

        switch (kind)
        {
            case "game":
                Settings.GamePath = selected;
                var modsCandidate = Path.Combine(selected, "Mods");
                if (string.IsNullOrWhiteSpace(Settings.ModsPath) && Directory.Exists(modsCandidate))
                    Settings.ModsPath = modsCandidate;
                Game = await Task.Run(() => _manager.InspectGame());
                break;
            case "mods":
                Settings.ModsPath = selected;
                break;
            case "backups":
                Settings.BackupsPath = selected;
                break;
        }

        SaveSettings();
        NotifyStats();
        await RefreshInstalledAsync();
    }

    public async Task AddArchivePathAsync()
    {
        if (Settings.ArchivePaths.Count >= AppSettings.MaxArchivePaths)
        {
            _toasts.Warning(T("vm.folderLimit"), F("vm.folderLimitHint", AppSettings.MaxArchivePaths));
            return;
        }

        var selected = _picker.PickFolder(T("vm.pickArchiveFolder"), Settings.ArchivePaths.LastOrDefault());
        if (selected is null) return;

        if (Settings.ArchivePaths.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            _toasts.Warning(T("vm.folderExists"), selected);
            return;
        }

        Settings.ArchivePaths.Add(selected);
        SaveSettings();
        _toasts.Success(T("vm.folderAdded"), selected);
        await Task.CompletedTask;
    }

    public void RemoveArchivePath(string path)
    {
        Settings.ArchivePaths.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        NotifyStats();
    }

    public void UpdateArchivePath(int index, string value)
    {
        if (index < 0 || index >= Settings.ArchivePaths.Count) return;
        Settings.ArchivePaths[index] = value;
    }

    public void SaveSettings()
    {
        Settings.GamePath = Settings.GamePath?.Trim() ?? string.Empty;
        Settings.ModsPath = Settings.ModsPath?.Trim() ?? string.Empty;
        Settings.BackupsPath = Settings.BackupsPath?.Trim() ?? string.Empty;
        // The key lives in AppSettings only in DPAPI-encrypted form.
        Settings.NexusApiKey = NexusKeyProtection.Protect(NexusApiKeyInput?.Trim() ?? string.Empty);
        _manager.SaveSettings(Settings);

        // Re-inspect the installation immediately: otherwise a newly typed game path keeps showing
        // the stale "Assembly-CSharp not found" state until the app is restarted.
        Game = _manager.InspectGame();

        OnPropertyChanged(nameof(Settings));
        NotifyStats();
    }

    public async Task CompleteSetupAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.GamePath) || !Directory.Exists(Settings.GamePath))
        {
            _toasts.Error(T("vm.gameFolderMissing"), T("vm.gameFolderMissingHint"));
            return;
        }

        if (string.IsNullOrWhiteSpace(Settings.ModsPath))
        {
            _toasts.Error(T("vm.modsFolderMissing"), T("vm.modsFolderMissingHint"));
            return;
        }

        if (!Directory.Exists(Settings.ModsPath))
        {
            var ok = await _dialogs.ConfirmAsync(T("vm.createModsTitle"),
                F("vm.createModsText", Settings.ModsPath), T("vm.createButton"));
            if (!ok) return;

            try { Directory.CreateDirectory(Settings.ModsPath); }
            catch (Exception ex)
            {
                _toasts.Error(T("vm.folderCreateFailed"), ex.Message);
                return;
            }
        }

        Settings.SetupCompleted = true;
        SetupRequired = false;
        SaveSettings();
        await RefreshInstalledAsync();
        _toasts.Success(T("vm.setupDone"), T("vm.setupDoneHint"));
    }

    public void ReopenSetup()
    {
        SetupRequired = true;
        OnPropertyChanged(nameof(SetupRequired));
    }

    // ---------------------------------------------------------------- scanning

    public async Task ScanAsync()
    {
        if (IsScanning) return;

        if (!Settings.ValidArchivePaths.Any())
        {
            _toasts.Warning(T("vm.noArchives"), T("vm.addFolderFirst"));
            return;
        }

        IsScanning = true;
        ProgressPercent = 0;
        StatusText = T("vm.searchingArchives");
        NotifyStats();

        try
        {
            var progress = new Progress<ScanProgressInfo>(p =>
            {
                StatusText = string.IsNullOrWhiteSpace(p.Message) ? p.Phase.ToString() : p.Message;
                ProgressPercent = p.Fraction * 100;
            });

            var summary = await Task.Run(() => _manager.ScanArchivesAsync(progress));
            Scan = summary;

            ResetCards();
            foreach (var package in summary.AllMods)
                TryAddCard(new ModCardViewModel(package));

            ApplyInstallState();
            RefreshFilter();
            NotifyStats();
            await RefreshLogAsync();

            if (summary.ArchivesFailed > 0)
                _toasts.Warning(T("vm.scanDoneErrors"),
                    F("vm.scanDoneErrorsHint", summary.ModsFound, summary.ArchivesFailed));
            else
                _toasts.Success(T("vm.scanDone"),
                    F("vm.scanDoneHint", summary.ArchivesScanned, summary.ModsFound, summary.Duration.TotalSeconds.ToString("0.0")));
        }
        catch (Exception ex)
        {
            _toasts.Error(T("vm.scanFailed"), ex.Message);
        }
        finally
        {
            IsScanning = false;
            ProgressPercent = 0;
            StatusText = string.Empty;
            NotifyStats();
        }
    }

    private void ResetCards()
    {
        foreach (var card in AvailableMods) card.PropertyChanged -= OnCardChanged;
        AvailableMods.Clear();
        _cardKeys.Clear();
        LibraryGroups.Clear();
    }

    /// <summary>Adds a card once per archive + subtree, so nested roots or cached duplicates cannot double-list a mod.</summary>
    private void TryAddCard(ModCardViewModel card)
    {
        var key = $"{card.Package.ArchivePath}|{card.Package.RootInArchive}";
        if (!_cardKeys.Add(key)) return;
        AddCard(card);
    }

    private void AddCard(ModCardViewModel card)
    {
        card.PropertyChanged += OnCardChanged;
        AvailableMods.Add(card);
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModCardViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));

            if (sender is ModCardViewModel card)
                LibraryGroups.FirstOrDefault(g => g.Mods.Contains(card))?.RefreshSelection();
        }
    }

    public void RefreshFilter()
    {
        IEnumerable<ModCardViewModel> query = AvailableMods;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(m => m.SearchIndex.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        query = LibraryFilter switch
        {
            "installed" => query.Where(m => m.IsInstalled),
            "updates" => query.Where(m => m.UpdateAvailable),
            "new" => query.Where(m => !m.IsInstalled),
            "code" => query.Where(m => m.HasCode),
            _ => query
        };

        query = SortMode switch
        {
            "size" => query.OrderByDescending(m => m.Package.ArchiveSize),
            "date" => query.OrderByDescending(m => m.Package.ArchiveModifiedUtc),
            "files" => query.OrderByDescending(m => m.FileCount),
            _ => query.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
        };

        var ordered = query.ToList();

        FilteredMods.Clear();
        foreach (var card in ordered) FilteredMods.Add(card);

        LibraryGroups.Clear();
        var groups = ordered
            .GroupBy(m => m.Package.SourceRoot ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
            LibraryGroups.Add(new ModGroupViewModel(group.Key, group.ToList()));
    }

    partial void OnSearchTextChanged(string value) => RefreshFilter();
    partial void OnLibraryFilterChanged(string value) => RefreshFilter();
    partial void OnSortModeChanged(string value) => RefreshFilter();

    public void SetLibraryFilter(string filter)
    {
        LibraryFilter = filter;
    }

    public void SelectAllVisible(bool selected)
    {
        foreach (var card in FilteredMods) card.IsSelected = selected;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    public void ClearSelection()
    {
        foreach (var card in AvailableMods) card.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    // ---------------------------------------------------------------- install / uninstall

    public async Task InstallSelectedAsync()
    {
        var cards = AvailableMods.Where(c => c.IsSelected).ToList();
        if (cards.Count == 0)
        {
            _toasts.Warning(T("vm.nothingSelected"), T("vm.checkCards"));
            return;
        }

        await InstallManyAsync(cards);
    }

    public Task InstallOneAsync(ModCardViewModel card) => InstallManyAsync(new List<ModCardViewModel> { card });

    private async Task InstallManyAsync(List<ModCardViewModel> cards)
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(Settings.ModsPath))
        {
            _toasts.Error(T("vm.modsPathMissing"), T("vm.modsPathMissingHint"));
            return;
        }

        // The same mod may exist in several archives (zip + 7z + tar.gz). Install one copy only.
        var unique = new List<ModCardViewModel>();
        var duplicates = new List<string>();

        foreach (var card in cards)
        {
            if (unique.Any(u => EffectiveFolderName(u.Package)
                    .Equals(EffectiveFolderName(card.Package), StringComparison.OrdinalIgnoreCase)))
            {
                duplicates.Add(card.Title);
                card.IsSelected = false;
                continue;
            }
            unique.Add(card);
        }

        if (duplicates.Count > 0)
            _toasts.Warning(T("vm.duplicatesSkipped"),
                F("vm.duplicatesHint", string.Join(", ", duplicates.Distinct().Take(4))));

        cards = unique;
        if (cards.Count == 0) return;

        var existing = cards
            .Select(c => EffectiveFolderName(c.Package))
            .Where(folder => Directory.Exists(Path.Combine(Settings.ModsPath, folder)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allowReplace = false;
        if (existing.Count > 0)
        {
            var preview = string.Join(", ", existing.Take(5));
            if (existing.Count > 5) preview += F("vm.andMore", existing.Count - 5);
            allowReplace = await _dialogs.ConfirmAsync(
                T("vm.replaceTitle"),
                F("vm.replaceText", preview),
                T("vm.replaceButton"), T("dlg.cancel"), destructive: true);

            if (!allowReplace) return;
        }

        IsBusy = true;
        var outcomes = new List<InstallOutcome>();

        try
        {
            foreach (var card in cards)
            {
                card.IsInstalling = true;
                card.InstallProgress = 0;
                StatusText = F("vm.installingStatus", card.Title);

                var targetFolder = EffectiveFolderName(card.Package);
                var options = new InstallOptions
                {
                    AllowReplace = allowReplace && existing.Contains(targetFolder),
                    CreateBackup = Settings.BackupBeforeChange,
                    // Update goes into the installed folder even if the new archive has
                    // a different root directory or filename.
                    FolderNameOverride = targetFolder
                };

                var progress = new Progress<InstallProgress>(p =>
                {
                    card.InstallProgress = p.Processed;
                    StatusText = $"{card.Title}: {p.Stage}";
                });

                var outcome = await Task.Run(() => _manager.Install(card.Package, options, progress));
                outcomes.Add(outcome);

                card.IsInstalling = false;
                if (outcome.Success) card.IsSelected = false;
            }
        }
        finally
        {
            IsBusy = false;
            StatusText = string.Empty;
        }

        var success = outcomes.Count(o => o.Success);
        var failed = outcomes.Count - success;

        if (success > 0)
            _toasts.Success(T("vm.installDone"), F("vm.installDoneHint", success));
        if (failed > 0)
            _toasts.Error(T("vm.installPartial"),
                string.Join(" · ", outcomes.Where(o => !o.Success).Take(3).Select(o => $"{o.ModName}: {o.Error}")));

        await RefreshInstalledAsync();
        await RefreshLogAsync();
        NotifyStats();
    }

    public async Task UninstallAsync(InstalledCardViewModel card)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            T("vm.uninstallTitle"),
            F("vm.uninstallText", card.Title,
                Settings.BackupBeforeChange ? T("vm.uninstallBackupYes") : T("vm.uninstallBackupNo")),
            T("vm.uninstallButton"), T("dlg.cancel"), destructive: true);

        if (!confirmed) return;

        card.IsBusy = true;
        StatusText = F("vm.removingStatus", card.Title);

        try
        {
            var outcome = await Task.Run(() => _manager.Uninstall(card.Model, Settings.BackupBeforeChange));
            if (outcome.Success)
            {
                _toasts.Success(T("vm.modRemoved"), card.Title + (outcome.BackupCreated ? T("vm.modRemovedBackup") : string.Empty));
            }
            else
            {
                _toasts.Error(T("vm.removeFailed"), outcome.Error ?? T("vm.unknownError"));
            }
        }
        finally
        {
            card.IsBusy = false;
            StatusText = string.Empty;
        }

        await RefreshInstalledAsync();
        await RefreshBackupsAsync();
        await RefreshLogAsync();
        await LoadCachedCatalogAsync();
        NotifyStats();
    }

    public async Task BackupModAsync(InstalledCardViewModel card)
    {
        card.IsBusy = true;
        try
        {
            var record = await Task.Run(() => _manager.CreateBackup(card.Model));
            _toasts.Success(T("vm.backupCreated"), Path.GetFileName(record.ArchivePath));
        }
        catch (Exception ex)
        {
            _toasts.Error(T("vm.backupFailed"), ex.Message);
        }
        finally
        {
            card.IsBusy = false;
        }

        await RefreshBackupsAsync();
        NotifyStats();
    }

    public async Task UpdateInstalledAsync(InstalledCardViewModel card)
    {
        var package = FindBestPackageFor(card);

        if (package is null)
        {
            _toasts.Warning(T("vm.archiveNotFound"), T("vm.archiveNotFoundHint"));
            return;
        }

        await InstallManyAsync(new List<ModCardViewModel> { package });
    }

    // ---------------------------------------------------------------- backups

    public async Task RefreshBackupsAsync()
    {
        var backups = await Task.Run(() => _manager.GetBackups());
        Backups.Clear();
        foreach (var backup in backups) Backups.Add(backup);
        NotifyStats();
    }

    public async Task RestoreAsync(BackupRecord record)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            T("vm.restoreTitle"),
            F("vm.restoreText", record.ModName, record.ModVersion,
                record.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")),
            T("vm.restoreButton"), T("dlg.cancel"), destructive: true);

        if (!confirmed) return;

        IsBusy = true;
        StatusText = T("vm.restoringStatus");
        try
        {
            var outcome = await Task.Run(() => _manager.Restore(record));
            if (outcome.Success)
                _toasts.Success(T("vm.restored"), $"{record.ModName}: {outcome.FilesWritten}");
            else
                _toasts.Error(T("vm.restoreFailed"), outcome.Error ?? T("vm.unknownError"));
        }
        finally
        {
            IsBusy = false;
            StatusText = string.Empty;
        }

        await RefreshInstalledAsync();
        await RefreshBackupsAsync();
        await RefreshLogAsync();
    }

    public async Task DeleteBackupAsync(BackupRecord record)
    {
        var confirmed = await _dialogs.ConfirmAsync(T("vm.deleteBackupTitle"),
            F("vm.deleteBackupText", Path.GetFileName(record.ArchivePath)),
            T("vm.deleteButton"), T("dlg.cancel"), destructive: true);

        if (!confirmed) return;

        var deleted = await Task.Run(() => _manager.DeleteBackup(record));
        if (deleted) _toasts.Success(T("vm.backupRemoved"), Path.GetFileName(record.ArchivePath));
        else _toasts.Error(T("vm.backupRemoveFailed"), T("vm.fileBusy"));

        await RefreshBackupsAsync();
        NotifyStats();
    }

    // ---------------------------------------------------------------- analysis

    public async Task AnalyzeAsync(ModPackage package)
    {
        if (IsAnalyzing) return;

        IsAnalyzing = true;
        AnalysisTarget = package;
        Report = null;
        StatusText = F("vm.analyzingStatus", package.DisplayTitle);

        try
        {
            Report = await Task.Run(() => _manager.Analyze(package));
            await RefreshLogAsync();
        }
        catch (Exception ex)
        {
            _toasts.Error(T("vm.analysisFailed"), ex.Message);
        }
        finally
        {
            IsAnalyzing = false;
            StatusText = string.Empty;
        }
    }

    public Task AnalyzeCardAsync(ModCardViewModel card) => AnalyzeAsync(card.Package);

    public async Task AnalyzeConflictsAsync()
    {
        if (IsAnalyzing) return;

        IsAnalyzing = true;
        StatusText = T("vm.searchingConflicts");
        try
        {
            Conflicts = await Task.Run(() => _manager.AnalyzeConflicts());
            NotifyStats();
            if (Conflicts.Conflicts.Count == 0)
                _toasts.Success(T("vm.noConflicts"), T("vm.noConflictsHint"));
            else
                _toasts.Warning(T("vm.foundOverlaps"), F("vm.foundOverlapsHint", Conflicts.Conflicts.Count));
        }
        finally
        {
            IsAnalyzing = false;
            StatusText = string.Empty;
        }
    }

    // ---------------------------------------------------------------- language

    private readonly Localization _localization = new();


    public Localization L => _localization;

    public Language Language => _localization.Current;

    public string T(string key) => _localization.T(key);

    public string F(string key, params object[] args) => _localization.F(key, args);

    public void SetLanguage(Language language)
    {
        if (_localization.Current == language) return;

        _localization.Set(language);
        Settings.Language = Localization.ToCode(language);
        SaveSettings();
        OnPropertyChanged(nameof(Language));
    }

    private void OnLanguageChanged() => OnPropertyChanged(nameof(Language));

    private void ApplyStoredLanguage()
    {
        // English unless the user picked Ukrainian and it was saved. A deterministic start avoids
        // screens rendering in different languages before the first repaint.
        var stored = string.IsNullOrWhiteSpace(Settings.Language) ? "en" : Settings.Language;

        _localization.Set(Localization.FromCode(stored));
    }

    // ---------------------------------------------------------------- conflict resolution

    private readonly ConflictResolutionService _resolutions = new();
    private readonly GameLauncher _launcher = new();

    public IReadOnlyList<ConflictResolution> Suggestions(ConflictEntry conflict) => _resolutions.Suggest(conflict);

    /// <summary>Writes the chosen option as a separate patch mod. Never touches the conflicting mods.</summary>
    public async Task CreateMergedPatchAsync(ConflictEntry conflict, ConflictResolution choice)
    {
        var modsPath = Settings.ModsPath;
        if (string.IsNullOrWhiteSpace(modsPath)) modsPath = Path.Combine(Settings.GamePath, "Mods");

        var result = await Task.Run(() => _resolutions.CreateMergedPatch(conflict, choice, modsPath));

        if (result.Success)
            _toasts.Success(T("vm.patchCreated"), result.FolderPath ?? string.Empty);
        else
            _toasts.Error(T("vm.patchFailed"), result.Error ?? T("vm.unknownError"));
    }

    public void LaunchGame(bool eacEnabled)
    {
        var result = _launcher.Launch(Settings.GamePath, eacEnabled);

        if (result.Started)
            _toasts.Success(T("vm.gameStarting"), eacEnabled ? T("vm.gameStartingEac") : T("vm.gameStartingNoEac"));
        else
            _toasts.Error(T("vm.gameLaunchFailed"), result.Error ?? T("vm.unknownError"));
    }

    public bool HasEac => _launcher.HasEacExecutable(Settings.GamePath);

    public async Task AnalyzePackageConflictsAsync(ModCardViewModel card)
    {
        IsAnalyzing = true;
        try
        {
            Conflicts = await Task.Run(() => _manager.AnalyzePackageConflicts(card.Package));
            NotifyStats();
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    // ---------------------------------------------------------------- misc

    public async Task RefreshInstalledAsync()
    {
        var mods = await Task.Run(() => _manager.GetInstalled());
        InstalledMods.Clear();
        foreach (var mod in mods) InstalledMods.Add(new InstalledCardViewModel(mod));
        ApplyInstallState();
        LoadNexusState();
        NotifyStats();
    }

    /// <summary>
    /// Matches an archive to an already installed mod. Both the name from ModInfo.xml and
    /// the folder name are considered: a fresh Nexus download often has a different inner
    /// folder (or no inner folder at all), so relying on one of them alone loses the match.
    /// </summary>
    private IEnumerable<InstalledMod> InstalledModels => InstalledMods.Select(c => c.Model);

    private string EffectiveFolderName(ModPackage package) =>
        ModPackageSelector.EffectiveFolderName(package, InstalledModels);

    /// <summary>
    /// Several archives of the same mod often sit in the library (old + freshly downloaded).
    /// Pick the highest version, and on a tie the most recently modified archive, so
    /// «Update» never reinstalls the older file.
    /// </summary>
    private ModCardViewModel? FindBestPackageFor(InstalledCardViewModel installed)
    {
        var best = ModPackageSelector.PickNewest(
            AvailableMods.Select(m => m.Package), installed.Model);
        return best is null
            ? null
            : AvailableMods.FirstOrDefault(m => ReferenceEquals(m.Package, best));
    }

    private void ApplyInstallState()
    {
        foreach (var card in AvailableMods)
        {
            var installed = InstalledMods.FirstOrDefault(i =>
                ModPackageSelector.Matches(i.Model, card.Package));

            card.IsInstalled = installed is not null;
            card.InstalledVersion = installed?.Version;
            card.IsOlderVersion = false;
            card.VersionNote = null;

            if (installed is null)
            {
                card.UpdateAvailable = false;
                continue;
            }

            // Only a genuinely newer version is an update. A different string used to be
            // enough, so an old archive was offered as a "downgrade upgrade".
            card.UpdateAvailable = ModPackageSelector.IsUpdateAvailable(card.Package, installed.Model);
            card.IsOlderVersion = ModPackageSelector.IsDowngrade(card.Package, installed.Model);

            if (card.IsOlderVersion)
                card.VersionNote = T("vm.noteArchiveOlder");
            else if (!card.UpdateAvailable &&
                     ModPackageSelector.Relation(card.Package, installed.Model) == VersionRelation.Inconclusive)
                card.VersionNote = T("vm.noteIncomparable");
        }

        foreach (var installed in InstalledMods)
        {
            var package = FindBestPackageFor(installed);
            var packageVersion = package?.Package.Info.Version;

            installed.HasUpdate = package is not null && packageVersion is not null &&
                                  ModPackageSelector.IsUpdateAvailable(package.Package, installed.Model);
            installed.AvailableVersion = installed.HasUpdate ? packageVersion : null;
            installed.VersionNote = package is not null &&
                                    ModPackageSelector.IsDowngrade(package.Package, installed.Model)
                ? T("vm.noteLibraryOlder")
                : null;
        }

        NotifyStats();
    }

    public async Task RefreshLogAsync()
    {
        var entries = await Task.Run(() => _manager.GetLog(80));
        LogEntries.Clear();
        foreach (var entry in entries) LogEntries.Add(entry);
    }

    public void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _shell.OpenFolder(path);
    }

    public void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _shell.OpenPath(path);
    }

    public async Task ClearCatalogAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(T("vm.cacheConfirmTitle"),
            T("vm.cacheConfirmText"),
            T("vm.cacheConfirmButton"));

        if (!confirmed) return;

        await Task.Run(() => _manager.ClearCatalog());
        _toasts.Info(T("vm.cacheCleared"), T("vm.cacheClearedHint"));
    }

    public async Task ClearLogAsync()
    {
        await Task.Run(() => _manager.ClearLog());
        await RefreshLogAsync();
        _toasts.Info(T("vm.logCleared"));
    }

    /// <summary>Writes a diagnostics file and opens the folder, so a bug report has something to attach.</summary>
    public async Task SaveDiagnosticsAsync()
    {
        try
        {
            var file = await Task.Run(() => Diagnostics.Save(
                _manager.Paths,
                Settings,
                Game,
                InstalledMods.Select(m => $"{m.Title} {m.Version}".Trim()),
                LogEntries,
                ArchivesCount,
                FailedArchives));

            OpenFolder(Path.GetDirectoryName(file) ?? _manager.Paths.LogDirectory);
            _toasts.Success(T("diag.saved"), Path.GetFileName(file));
        }
        catch (Exception e)
        {
            _toasts.Error(T("diag.failed"), e.Message);
        }
    }

    public async Task SaveSettingsWithToastAsync()
    {
        SaveSettings();
        await RefreshInstalledAsync();
        _toasts.Success(T("vm.settingsSaved"));
    }

    // ---------------------------------------------------------------- mod configs

    public async Task LoadConfigsAsync()
    {
        IsConfigLoading = true;
        try
        {
            await RefreshInstalledAsync();

            var mods = InstalledMods.ToList();
            var entries = await Task.Run(() => mods
                .Select(mod => (Mod: mod, Files: _manager.GetModConfigs(mod.Model)))
                .Where(x => x.Files.Count > 0)
                .Select(x => new ConfigModEntry(x.Mod, x.Files.Count))
                .ToList());

            ConfigMods.Clear();
            foreach (var entry in entries) ConfigMods.Add(entry);

            if (ConfigMod is null || !ConfigMods.Contains(ConfigMod))
                ConfigMod = ConfigMods.FirstOrDefault();

            if (ConfigMod is not null)
                await SelectConfigModAsync(ConfigMod);
            else
            {
                ConfigFiles.Clear();
                ConfigFields.Clear();
                ConfigNotice = null;
            }
        }
        finally
        {
            IsConfigLoading = false;
        }
    }

    public async Task SelectConfigModAsync(ConfigModEntry entry)
    {
        ConfigMod = entry;
        ConfigNotice = null;

        var files = await Task.Run(() => _manager.GetModConfigs(entry.Mod.Model));
        ConfigFiles.Clear();
        foreach (var file in files) ConfigFiles.Add(file);

        if (ConfigFile is null || !ConfigFiles.Contains(ConfigFile))
            ConfigFile = ConfigFiles.FirstOrDefault();

        await LoadConfigFileAsync();
    }

    public async Task SelectConfigFileAsync(ModConfigFile file)
    {
        ConfigFile = file;
        await LoadConfigFileAsync();
    }

    public async Task LoadConfigFileAsync()
    {
        foreach (var field in ConfigFields) field.PropertyChanged -= OnConfigFieldChanged;
        ConfigFields.Clear();
        ConfigNotice = null;

        if (ConfigFile is null) return;

        IsConfigLoading = true;
        try
        {
            var document = await Task.Run(() => _manager.ReadModConfig(ConfigFile));

            foreach (var field in document.Fields)
            {
                var viewModel = new ConfigFieldViewModel(field);
                viewModel.PropertyChanged += OnConfigFieldChanged;
                ConfigFields.Add(viewModel);
            }

            ConfigNotice = document.Error ?? document.Notice;
            OnPropertyChanged(nameof(HasConfigChanges));
        }
        finally
        {
            IsConfigLoading = false;
        }
    }

    private void OnConfigFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigFieldViewModel.IsDirty))
            OnPropertyChanged(nameof(HasConfigChanges));
    }

    public async Task SaveConfigAsync()
    {
        if (ConfigFile is null || ConfigMod is null) return;

        var dirty = ConfigFields.Where(f => f.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            _toasts.Info(T("vm.noChanges"), T("vm.changeOne"));
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            T("vm.saveConfigTitle"),
            F("vm.saveConfigText", ConfigFile.DisplayName, ConfigMod.Title, dirty.Count),
            T("vm.saveButton"));

        if (!confirmed) return;

        var changes = dirty.Select(f => new ConfigFieldChange
        {
            Key = f.Key,
            Value = f.EditValue,
            Kind = f.Kind
        }).ToList();

        var result = await Task.Run(() => _manager.SaveModConfig(ConfigFile, ConfigMod.Mod.Model, changes));

        if (result.Success)
        {
            _toasts.Success(T("vm.configSaved"),
                F("vm.configSavedHint", result.Changed) + (result.BackupPath is null ? string.Empty : T("vm.backupSuffix")));
            await LoadConfigFileAsync();
        }
        else
        {
            _toasts.Error(T("vm.configSaveFailed"), result.Error ?? T("vm.unknownError"));
        }

        await RefreshLogAsync();
    }

    public async Task ResetConfigAsync()
    {
        foreach (var field in ConfigFields) field.Revert();
        await LoadConfigFileAsync();
    }

    public string AppDataRoot => _manager.AppDataRoot;
    public string DatabasePath => _manager.DatabasePath;
}
