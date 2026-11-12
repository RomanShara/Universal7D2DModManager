namespace U7D2D.Core.Models;

public enum CompatibilityVerdict
{
    Unknown = 0,
    Incompatible = 1,
    LikelyIncompatible = 2,
    Risky = 3,
    LikelyCompatible = 4,
    Compatible = 5
}

public enum FindingSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>Single evidence-backed observation produced by the compatibility analysis.</summary>
public sealed class AnalysisFinding
{
    public FindingSeverity Severity { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;

    /// <summary>0..1 — how sure the analyzer is about this specific finding.</summary>
    public double Confidence { get; set; }
}

public sealed class CompatibilityReport
{
    public string ModName { get; set; } = string.Empty;
    public string ModVersion { get; set; } = string.Empty;
    public string? GameVersion { get; set; }
    public CompatibilityVerdict Verdict { get; set; } = CompatibilityVerdict.Unknown;

    /// <summary>0..1 — confidence of the overall verdict.</summary>
    public double Confidence { get; set; }

    public string Headline { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<AnalysisFinding> Findings { get; set; } = new();
    public List<string> SignalsUsed { get; set; } = new();
    public List<string> NotVerified { get; set; } = new();
    public DateTime AnalyzedUtc { get; set; } = DateTime.UtcNow;

    public bool HasCritical => Findings.Any(f => f.Severity == FindingSeverity.Critical);
    public int HighCount => Findings.Count(f => f.Severity == FindingSeverity.High);
}
