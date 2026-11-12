namespace U7D2D.Core.Models;

/// <summary>A mod folder that currently exists inside the game's Mods directory.</summary>
public sealed class InstalledMod
{
    public string FolderName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public ModMetadata Info { get; set; } = new();
    public ModContentProfile Content { get; set; } = new();
    public int FileCount { get; set; }
    public long SizeBytes { get; set; }
    public bool HasModInfo { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Archive this mod was installed from, if the manager performed the install.</summary>
    public string? SourceArchive { get; set; }

    public DateTime? InstalledUtc { get; set; }
    public List<string> Warnings { get; set; } = new();

    public string DisplayTitle => Info.EffectiveName;
    public string VersionLabel => Info.VersionOrUnknown;
}
