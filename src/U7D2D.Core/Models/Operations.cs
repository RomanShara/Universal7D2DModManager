namespace U7D2D.Core.Models;

public sealed class InstallOptions
{
    /// <summary>Explicitly allow replacing an existing mod folder (never happens silently).</summary>
    public bool AllowReplace { get; set; }

    public bool CreateBackup { get; set; } = true;
    public string? FolderNameOverride { get; set; }

    /// <summary>Directory inside the archive to install when the user picked a nested root.</summary>
    public string? ContentRootOverride { get; set; }
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

    /// <summary>True when a failed install put the previous version back in place.</summary>
    public bool RolledBack { get; set; }

    /// <summary>Where the previous version was kept when it could not be restored automatically.</summary>
    public string? KeptOldPath { get; set; }

    /// <summary>Where the new files were kept when the swap failed and the old version was moved aside.</summary>
    public string? KeptNewPath { get; set; }

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

    /// <summary>True when a failed restore put the previous folder back in place.</summary>
    public bool RolledBack { get; set; }

    /// <summary>True when the folder was rebuilt from the safety backup after a failed restore.</summary>
    public bool RecoveredFromSafety { get; set; }

    public List<string> Warnings { get; } = new();
}

public sealed class InstallProgress
{
    public string Stage { get; set; } = string.Empty;
    public int Processed { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
}
