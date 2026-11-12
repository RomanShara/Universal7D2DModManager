using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using U7D2D.Core.Models;

namespace U7D2D.App.ViewModels;

public partial class ModCardViewModel : ObservableObject
{
    public ModCardViewModel(ModPackage package)
    {
        Package = package;
        SearchIndex = string.Join(' ',
            package.Info.EffectiveName,
            package.Info.Author,
            package.Info.Version,
            package.ArchiveFileName,
            package.FolderName,
            package.Info.Description);
    }

    public ModPackage Package { get; }
    public string SearchIndex { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _isOlderVersion;
    [ObservableProperty] private string? _versionNote;
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private int _installProgress;

    public string Title => Package.DisplayTitle;
    public string Version => Package.VersionLabel;
    public string Author => string.IsNullOrWhiteSpace(Package.Info.Author) ? Localization.S("card.unknownAuthor") : Package.Info.Author;
    public string ArchiveName => Package.ArchiveFileName;
    public string SizeLabel => FormatSize(Package.ArchiveSize);
    public int FileCount => Package.Content.FileCount;
    public bool HasCode => Package.Content.HasDlls;
    public bool HasHarmony => Package.Content.HasHarmonyFolder || Package.Content.DllFiles.Any(d => d.Contains("Harmony", StringComparison.OrdinalIgnoreCase));
    public int ConfigCount => Package.Content.ConfigFiles.Count;
    public bool MultipleInArchive => !string.IsNullOrEmpty(Package.RootInArchive) && Package.RootInArchive.Contains('/');
    public string SourceLabel => string.IsNullOrEmpty(Package.RootInArchive) ? Localization.S("card.archiveRoot") : Package.RootInArchive;

    public string StatusText => IsInstalling ? Localization.S("card.installing")
        : UpdateAvailable ? Localization.S("card.updateAvailable")
        : IsOlderVersion ? Localization.S("card.archiveOlder")
        : VersionNote is not null ? Localization.S("card.otherVersion")
        : IsInstalled ? Localization.S("card.installed")
        : Localization.S("card.notInstalled");

    public string StatusKind => IsInstalling ? "busy"
        : UpdateAvailable ? "update"
        : IsOlderVersion || VersionNote is not null ? "risky"
        : IsInstalled ? "installed"
        : "new";

    public string Description => string.IsNullOrWhiteSpace(Package.Info.Description)
        ? Localization.S("card.noDescription")
        : Package.Info.Description;

    private static string FormatSize(long bytes)
    {
        string[] units =
        [
            Localization.S("card.unitB"),
            Localization.S("card.unitKb"),
            Localization.S("card.unitMb"),
            Localization.S("card.unitGb")
        ];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusKind));
    }

    partial void OnUpdateAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusKind));
    }

    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusKind));
    }
}

/// <summary>Library section: all mods that came from one configured archive folder.</summary>
public partial class ModGroupViewModel : ObservableObject
{
    public ModGroupViewModel(string root, IReadOnlyList<ModCardViewModel> mods)
    {
        Root = root ?? string.Empty;
        Mods = mods;
    }

    public string Root { get; }
    public IReadOnlyList<ModCardViewModel> Mods { get; }

    public string Title
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Root)) return Localization.S("card.noSource");
            var trimmed = Root.TrimEnd('\\', '/');
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? Root : name;
        }
    }

    public string SourcePath => string.IsNullOrWhiteSpace(Root) ? Localization.S("card.pathNotSet") : Root;
    public int Count => Mods.Count;
    public int SelectedCount => Mods.Count(m => m.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public bool AllSelected => Mods.Count > 0 && Mods.All(m => m.IsSelected);

    [ObservableProperty] private bool _isExpanded = true;

    public void SelectAll(bool value)
    {
        foreach (var mod in Mods) mod.IsSelected = value;
        RefreshSelection();
    }

    public void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AllSelected));
    }
}

public partial class InstalledCardViewModel : ObservableObject
{
    public InstalledCardViewModel(InstalledMod model)
    {
        Model = model;
        SearchIndex = string.Join(' ', model.DisplayTitle, model.Info.Author, model.VersionLabel, model.FolderName);
    }

    public InstalledMod Model { get; }
    public string SearchIndex { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private string? _availableVersion;
    [ObservableProperty] private string? _versionNote;

    /// <summary>Last Nexus verdict. Null means "not checked yet" or "binding changed".</summary>
    [ObservableProperty] private NexusModUpdate? _nexus;
    [ObservableProperty] private string _nexusIdLabel = Localization.S("card.nexusNotLinked");

    public bool HasNexusUpdate => Nexus?.IsUpdate == true;
    public string? NexusRemoteVersion => Nexus?.RemoteVersion;
    public string? NexusMessage => Nexus?.Message;
    public string? NexusFilesUrl => Nexus?.FilesUrl;
    public string? NexusChangelogUrl => Nexus?.ChangelogUrl;
    public string? NexusPageUrl => Nexus?.PageUrl;
    public bool NexusNeedsLink => Nexus?.Status == NexusCheckStatus.NotLinked;

    public string Title => Model.DisplayTitle;
    public string Version => Model.VersionLabel;
    public string Folder => Model.FolderName;
    public string Author => string.IsNullOrWhiteSpace(Model.Info.Author) ? Localization.S("card.unknownAuthor") : Model.Info.Author;
    public int FileCount => Model.FileCount;
    public string SizeLabel => FormatSize(Model.SizeBytes);
    public bool HasCode => Model.Content.HasDlls;
    public bool HasModInfo => Model.HasModInfo;
    public int ConfigCount => Model.Content.ConfigFiles.Count;
    public string InstalledLabel => Model.InstalledUtc is null
        ? Localization.S("card.installedManually")
        : Localization.SF("card.installedAt", Model.InstalledUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
    public string? SourceArchive => Model.SourceArchive;
    public string WarningsText => string.Join(" · ", Model.Warnings);

    private static string FormatSize(long bytes)
    {
        string[] units =
        [
            Localization.S("card.unitB"),
            Localization.S("card.unitKb"),
            Localization.S("card.unitMb"),
            Localization.S("card.unitGb")
        ];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
