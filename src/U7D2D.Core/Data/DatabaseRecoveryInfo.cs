namespace U7D2D.Core.Data;

/// <summary>What happened when the manager had to replace its database.</summary>
public sealed class DatabaseRecoveryInfo
{
    /// <summary>Where the damaged file was moved. Kept on disk so support can inspect it.</summary>
    public string QuarantinePath { get; init; } = string.Empty;

    /// <summary>Error reported by SQLite when the file was opened.</summary>
    public string Reason { get; init; } = string.Empty;

    public DateTime RecoveredUtc { get; init; } = DateTime.UtcNow;
}
