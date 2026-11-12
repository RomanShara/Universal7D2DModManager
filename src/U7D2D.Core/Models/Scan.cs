namespace U7D2D.Core.Models;

public enum ScanPhase
{
    Preparing = 0,
    Enumerating = 1,
    OpeningArchive = 2,
    DetectingMods = 3,
    Completed = 4,
    Failed = 5
}

public sealed class ScanProgressInfo
{
    public ScanPhase Phase { get; set; }
    public int Current { get; set; }
    public int Total { get; set; }
    public string Item { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Current / Total, 0, 1);
}

public sealed class ArchiveScanResult
{
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>Configured root folder that owns this archive.</summary>
    public string SourceRoot { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string Format { get; set; } = "unknown";
    public int EntryCount { get; set; }
    public long ArchiveSize { get; set; }
    public DateTime ArchiveModifiedUtc { get; set; }
    public List<ModPackage> Mods { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public bool FromCache { get; set; }

    /// <summary>Version of the scanning/parsing logic that produced this result. Older cache entries are discarded.</summary>
    public int ScannerVersion { get; set; }
}

public sealed class ScanSummary
{
    public List<ArchiveScanResult> Archives { get; set; } = new();
    public List<ModPackage> AllMods { get; set; } = new();
    public int ArchivesScanned => Archives.Count;
    public int ArchivesFailed => Archives.Count(a => !a.Success);
    public int ModsFound => AllMods.Count;
    public TimeSpan Duration { get; set; }
    public List<string> SkippedFolders { get; set; } = new();
}
