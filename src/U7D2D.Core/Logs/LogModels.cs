using U7D2D.Core.I18n;

namespace U7D2D.Core.Logs;

public enum LogSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>How sure the manager is about the mod it names. Low confidence must never read as an accusation.</summary>
public enum LogConfidence
{
    None,
    Low,
    Medium,
    High
}

/// <summary>One frame of a stack trace, as Unity prints it.</summary>
public sealed class StackFrame
{
    public int Index { get; init; }

    /// <summary>Namespace and type, without the method, for example GameManager or Gravebound.Vault.UI.VaultUiBootstrap.</summary>
    public string TypeName { get; init; } = string.Empty;

    public string MemberName { get; init; } = string.Empty;

    /// <summary>Assembly hash Unity prints instead of a name. Almost always useless, kept for completeness.</summary>
    public string? AssemblyHint { get; init; }

    public override string ToString() =>
        string.IsNullOrEmpty(MemberName) ? TypeName : $"{TypeName}.{MemberName}";
}

/// <summary>A single diagnostic event taken from a game log.</summary>
public sealed class LogEntry
{
    public LogSeverity Severity { get; init; }
    public DateTime? Time { get; set; }
    public string Tag { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ExceptionType { get; init; }
    public List<StackFrame> Frames { get; } = new();
    public int LineNumber { get; init; }

    public string Signature
    {
        get
        {
            var frames = string.Join("|", Frames.Take(3).Select(f => f.ToString()));
            return $"{ExceptionType ?? Tag}|{Message}|{frames}";
        }
    }
}

/// <summary>A mod the manager considers related to a finding.</summary>
public sealed class ModBlame
{
    public string ModFolder { get; init; } = string.Empty;
    public string ModName { get; init; } = string.Empty;
    public LogConfidence Confidence { get; init; }
    public Loc Reason { get; init; } = new("log.none");
    public List<string> Types { get; } = new();
}

/// <summary>One reported problem, already deduplicated.</summary>
public sealed class LogFinding
{
    /// <summary>Raw title, used when the text is not translatable, for example an exception name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Translated title. Wins over <see cref="Title"/> when set.</summary>
    public Loc? TitleLoc { get; set; }

    public string DisplayTitle => TitleLoc?.Text ?? Title;
    public LogSeverity Severity { get; set; }
    public DateTime? Time { get; set; }

    /// <summary>When the merged lines first and last occurred. A repeat is one scenario, not N faults.</summary>
    public DateTime? FirstSeen { get; set; }
    public DateTime? LastSeen { get; set; }

    public int Occurrences { get; set; } = 1;
    public string? ExceptionType { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>Top of the stack, for the report. Truncated on purpose.</summary>
    public List<string> Frames { get; } = new();

    public List<ModBlame> Suspects { get; } = new();

    /// <summary>True for "Harmony could not find type" entries, which need different advice.</summary>
    public bool MissingType { get; set; }

    /// <summary>
    /// A self-healing notice, not a failure. The game said it fell back to a default, so there is
    /// nothing to blame a mod for. Notices are shown last and never carry a culprit.
    /// </summary>
    public bool IsNotice { get; set; }

    /// <summary>Values that differed between the merged lines, for example the setting names.</summary>
    public List<string> Details { get; } = new();

    /// <summary>
    /// Exact number of distinct values, including those past the display limit in Details.
    /// Lets a card say "48 distinct patches" instead of implying 48 copies of one XPath.
    /// </summary>
    public int DistinctValues { get; set; }

    /// <summary>What the manager can say about a fix. Empty when nothing honest can be offered.</summary>
    public List<Loc> Suggestions { get; } = new();

    public LogConfidence Confidence =>
        Suspects.Count == 0 ? LogConfidence.None : Suspects.Max(s => s.Confidence);

    public ModBlame? Primary => Suspects
        .OrderByDescending(s => s.Confidence)
        .FirstOrDefault();
}

/// <summary>Result of one log analysis run.</summary>
public sealed class LogDiagnosisReport
{
    public List<string> Sources { get; } = new();
    public List<LogFinding> Findings { get; } = new();

    /// <summary>
    /// Findings the analysis produced before the report cap was applied. <see cref="Findings"/> may
    /// hold fewer, so the report can state how many cards are not shown instead of looking complete.
    /// </summary>
    public int TotalFindings { get; set; }

    public bool Truncated => Findings.Count < TotalFindings;

    /// <summary>
    /// Advice that applies to the whole report. Rendered once above the findings instead of being
    /// repeated on every card that happens to have no culprit.
    /// </summary>
    public List<Loc> Notes { get; } = new();
    public List<string> Skipped { get; } = new();

    /// <summary>Assemblies where malformed Harmony metadata was skipped; the report is not complete for them.</summary>
    public List<string> IndexWarnings { get; } = new();
    public int LinesRead { get; set; }
    public int EntriesParsed { get; set; }
    public int ModsIndexed { get; set; }
    public int TypesIndexed { get; set; }
    public int PatchTargetsIndexed { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>Where the time went. Useful when the first run is slow and the cache is cold.</summary>
    public TimeSpan IndexDuration { get; set; }
    public TimeSpan ReadDuration { get; set; }
    public TimeSpan CompareDuration { get; set; }
    public string? Error { get; set; }

    /// <summary>What changed between the current session and the previous one.</summary>
    public LogComparison? Comparison { get; set; }

    public int HighCount => Findings.Count(f => f.Confidence == LogConfidence.High);
    public int MediumCount => Findings.Count(f => f.Confidence == LogConfidence.Medium);
    public int UnattributedCount => Findings.Count(f => f.Confidence == LogConfidence.None);
    public int NoticeCount => Findings.Count(f => f.IsNotice);
}

/// <summary>What the analysis is doing right now, for the progress line.</summary>
public sealed class LogProgress
{
    public string Stage { get; init; } = string.Empty;
    public int Done { get; init; }
    public int Total { get; init; }

    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}
