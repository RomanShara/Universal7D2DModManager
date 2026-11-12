namespace U7D2D.Core.Models;

public sealed class InstallOptions
{
    /// <summary>Explicitly allow replacing an existing mod folder (never happens silently).</summary>
    public bool AllowReplace { get; set; }

    public bool CreateBackup { get; set; } = true;
    public string? FolderNameOverride { get; set; }
}

public sealed class InstallOutcome
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string ModName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public bool ReplacedExisting { get; set; }
    public bool BackupCreated { get; set; }
    public string? BackupPath { get; set; }
    public int FilesWritten { get; set; }
    public long BytesWritten { get; set; }
    public List<string> Warnings { get; } = new();
    public TimeSpan Duration { get; set; }
}

public sealed class UninstallOutcome
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string ModName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public bool BackupCreated { get; set; }
    public string? BackupPath { get; set; }
    public List<string> Warnings { get; } = new();
}

public sealed class RestoreOutcome
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public int FilesWritten { get; set; }
    public bool SafetyBackupCreated { get; set; }
    public string? SafetyBackupPath { get; set; }
    public List<string> Warnings { get; } = new();
}

public sealed class InstallProgress
{
    public string Stage { get; set; } = string.Empty;
    public int Processed { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
}
