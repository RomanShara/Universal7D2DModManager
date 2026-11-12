namespace U7D2D.Core.Models;

/// <summary>Static description of what is physically inside a mod folder/archive subtree.</summary>
public sealed class ModContentProfile
{
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<string> ConfigFiles { get; set; } = new();
    public List<string> DllFiles { get; set; } = new();
    public List<string> XmlFiles { get; set; } = new();
    public List<string> TopLevelFolders { get; set; } = new();
    public bool HasHarmonyFolder { get; set; }
    public bool HasLocalization { get; set; }
    public bool HasAssets { get; set; }
    public bool HasXui { get; set; }
    public bool HasDlls => DllFiles.Count > 0;
    public bool HasConfig => ConfigFiles.Count > 0;
}

/// <summary>A mod discovered inside an archive (one archive may contain several mods).</summary>
public sealed class ModPackage
{
    public string Id { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>Configured archive root folder this package came from (used to group the library).</summary>
    public string SourceRoot { get; set; } = string.Empty;
    public string ArchiveFileName { get; set; } = string.Empty;

    /// <summary>Directory inside the archive that contains ModInfo.xml. Empty string means archive root.</summary>
    public string RootInArchive { get; set; } = string.Empty;

    /// <summary>Folder name that will be created inside the game Mods directory.</summary>
    public string FolderName { get; set; } = string.Empty;

    public ModMetadata Info { get; set; } = new();
    public ModContentProfile Content { get; set; } = new();
    public long ArchiveSize { get; set; }
    public DateTime ArchiveModifiedUtc { get; set; }
    public bool HasModInfo { get; set; }
    public string? Warning { get; set; }

    /// <summary>Stable fingerprint of the archive + subtree, used to detect package changes.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public string DisplayTitle => Info.EffectiveName;
    public string VersionLabel => Info.VersionOrUnknown;

    public override string ToString() => $"{DisplayTitle} ({VersionLabel}) @ {ArchiveFileName}";
}
