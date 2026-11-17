using U7D2D.Core.I18n;

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

    // Kept as translations rather than finished strings: the report stays on screen, and the
    // language can be switched while it is being read.
    public Loc? CategoryLoc { get; set; }
    public Loc? TitleLoc { get; set; }
    public Loc? DetailLoc { get; set; }
    public Loc? EvidenceLoc { get; set; }

    public string Category { get => CategoryLoc?.Text ?? string.Empty; set => CategoryLoc = Loc.Literal(value); }
    public string Title { get => TitleLoc?.Text ?? string.Empty; set => TitleLoc = Loc.Literal(value); }
    public string Detail { get => DetailLoc?.Text ?? string.Empty; set => DetailLoc = Loc.Literal(value); }
    public string Evidence { get => EvidenceLoc?.Text ?? string.Empty; set => EvidenceLoc = Loc.Literal(value); }

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

    public Loc? HeadlineLoc { get; set; }
    public Loc? SummaryLoc { get; set; }

    public string Headline { get => HeadlineLoc?.Text ?? string.Empty; set => HeadlineLoc = Loc.Literal(value); }
    public string Summary { get => SummaryLoc?.Text ?? string.Empty; set => SummaryLoc = Loc.Literal(value); }

    public List<AnalysisFinding> Findings { get; set; } = new();
    public List<Loc> SignalsUsed { get; set; } = new();
    public List<Loc> NotVerified { get; set; } = new();
    public DateTime AnalyzedUtc { get; set; } = DateTime.UtcNow;

    public bool HasCritical => Findings.Any(f => f.Severity == FindingSeverity.Critical);
    public int HighCount => Findings.Count(f => f.Severity == FindingSeverity.High);
}
