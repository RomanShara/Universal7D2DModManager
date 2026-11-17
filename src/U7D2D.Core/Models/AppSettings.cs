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

    /// <summary>
    /// Launch the game with Easy Anti-Cheat. On by default: turning it off changes what multiplayer
    /// the player can join, so the choice has to survive a restart.
    /// </summary>
    public bool LaunchWithEac { get; set; } = true;
    public bool ScanRecursively { get; set; } = true;
    public int MaxScanDepth { get; set; } = 12;
    public bool ConfirmBeforeDelete { get; set; } = true;

    /// <summary>Interface language code: "en" or "ua".</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// OAuth access token, DPAPI-protected. Short-lived; refreshed automatically. The manager never
    /// stores or asks for a personal API key - the Nexus AUP does not allow it.
    /// </summary>
    public string NexusAccessToken { get; set; } = string.Empty;

    /// <summary>OAuth refresh token, DPAPI-protected. Used to obtain a new access token silently.</summary>
    public string NexusRefreshToken { get; set; } = string.Empty;

    /// <summary>UTC instant when the access token expires, so a refresh can happen before a call.</summary>
    public DateTime? NexusTokenExpiresUtc { get; set; }

    /// <summary>Nexus username for display. Not sensitive and not used for authorization.</summary>
    public string NexusAccountName { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client id issued by Nexus after registration. The id is public (not a secret); storing
    /// it here lets the user enable sign-in by pasting it, without rebuilding the app.
    /// </summary>
    public string NexusOAuthClientId { get; set; } = string.Empty;

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
        LaunchWithEac = LaunchWithEac,
        ScanRecursively = ScanRecursively,
        MaxScanDepth = MaxScanDepth,
        ConfirmBeforeDelete = ConfirmBeforeDelete,
        Language = Language,
        NexusAccessToken = NexusAccessToken,
        NexusRefreshToken = NexusRefreshToken,
        NexusTokenExpiresUtc = NexusTokenExpiresUtc,
        NexusAccountName = NexusAccountName,
        NexusOAuthClientId = NexusOAuthClientId,
        NexusGameDomain = NexusGameDomain
    };

    public IEnumerable<string> ValidArchivePaths =>
        ArchivePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
}
