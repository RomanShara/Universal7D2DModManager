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

    /// <summary>Possible roots when the archive structure is ambiguous. Empty for a normal archive.</summary>
    public List<ModRootCandidate> RootCandidates { get; set; } = new();

    /// <summary>True when the scanner cannot pick the install folder without guessing.</summary>
    public bool NeedsRootChoice { get; set; }

    /// <summary>Why the choice is needed: nested ModInfo.xml, a load-order prefix above, or both.</summary>
    public string? AmbiguityReason { get; set; }

    /// <summary>True when the archive is a container with shared files and several child mods.</summary>
    public bool IsComposite { get; set; }

    /// <summary>Child mods of a composite archive. Empty for a normal package.</summary>
    public List<ModPackage> Components { get; set; } = new();

    /// <summary>Files directly inside the container, shown in the library card.</summary>
    public List<string> SharedFiles { get; set; } = new();

    /// <summary>Folder name for the generated mod that carries the shared files.</summary>
    public string? SharedFolder { get; set; }

    /// <summary>Container-relative roots that belong to child mods and must not be copied as shared files.</summary>
    public List<string> ExcludeRoots { get; set; } = new();

    /// <summary>Install writes a generated ModInfo.xml for this package.</summary>
    public bool GeneratesModInfo { get; set; }
    public string? Warning { get; set; }

    /// <summary>Stable fingerprint of the archive + subtree, used to detect package changes.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public string DisplayTitle => Info.EffectiveName;
    public string VersionLabel => Info.VersionOrUnknown;

    public override string ToString() => $"{DisplayTitle} ({VersionLabel}) @ {ArchiveFileName}";
}

/// <summary>One way to install an archive: the folder name and the subtree it comes from.</summary>
public sealed class ModRootCandidate
{
    /// <summary>Name the folder gets inside Mods. It decides the load order.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Directory inside the archive whose contents are installed.</summary>
    public string ContentRoot { get; set; } = string.Empty;

    /// <summary>Archive path shown in the choice dialog, for example "0-Core/Release/CoreMod".</summary>
    public string DisplayPath { get; set; } = string.Empty;

    /// <summary>Names directly under the content root, shown in the choice dialog.</summary>
    public List<string> TopLevelEntries { get; set; } = new();

    /// <summary>Number of files under the content root.</summary>
    public int FileCount { get; set; }

    /// <summary>The scanner would pick this one without asking.</summary>
    public bool IsDefault { get; set; }

    public override string ToString() => $"{Name} <- {DisplayPath}";
}
