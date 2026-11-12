namespace U7D2D.Core.Models;

/// <summary>Persisted application configuration.</summary>
public sealed class AppSettings
{
    public const int MaxArchivePaths = 10;

    public string GamePath { get; set; } = string.Empty;
    public string ModsPath { get; set; } = string.Empty;
    public string BackupsPath { get; set; } = string.Empty;
    public List<string> ArchivePaths { get; set; } = new();

    public bool SetupCompleted { get; set; }

    /// <summary>Manual override for the detected game version (used when auto-detection fails).</summary>
    public string? GameVersionOverride { get; set; }

    public bool BackupBeforeChange { get; set; } = true;
    public bool ScanRecursively { get; set; } = true;
    public int MaxScanDepth { get; set; } = 12;
    public bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>Interface language code: "en" or "ua".</summary>
    public string Language { get; set; } = "en";

    /// <summary>Personal Nexus API key, DPAPI-protected. Never written in plain text.</summary>
    public string NexusApiKey { get; set; } = string.Empty;

    /// <summary>Nexus game domain used for lookups (7 Days To Die = "7daystodie").</summary>
    public string NexusGameDomain { get; set; } = "7daystodie";

    public AppSettings Clone() => new()
    {
        GamePath = GamePath,
        ModsPath = ModsPath,
        BackupsPath = BackupsPath,
        ArchivePaths = new List<string>(ArchivePaths),
        SetupCompleted = SetupCompleted,
        GameVersionOverride = GameVersionOverride,
        BackupBeforeChange = BackupBeforeChange,
        ScanRecursively = ScanRecursively,
        MaxScanDepth = MaxScanDepth,
        ConfirmBeforeDelete = ConfirmBeforeDelete,
        NexusApiKey = NexusApiKey,
        NexusGameDomain = NexusGameDomain
    };

    public IEnumerable<string> ValidArchivePaths =>
        ArchivePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
}
