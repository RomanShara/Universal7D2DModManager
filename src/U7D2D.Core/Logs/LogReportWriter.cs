using System.Text;
using U7D2D.Core.I18n;

namespace U7D2D.Core.Logs;

/// <summary>Plain text report, the same one the window shows and the one a user can attach to a bug report.</summary>
public static class LogReportWriter
{
    public static string Build(LogDiagnosisReport report)
    {
        var text = new StringBuilder();

        text.AppendLine(Localization.S("log.reportTitle"));
        text.AppendLine($"{Localization.S("log.reportCreated")}: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine();

        if (report.Error is not null)
        {
            text.AppendLine(report.Error);
            return text.ToString();
        }

        text.AppendLine($"{Localization.S("log.reportSources")}: {string.Join(", ", report.Sources)}");
        text.AppendLine($"{Localization.S("log.reportIndexed")}: {report.ModsIndexed} / {report.TypesIndexed} / {report.PatchTargetsIndexed}");
        text.AppendLine($"{Localization.S("log.reportLines")}: {report.LinesRead}");
        text.AppendLine($"{Localization.S("log.reportDuration")}: {report.Duration.TotalSeconds:0.0}s");
        text.AppendLine($"{Localization.S("log.reportStages")}: {report.IndexDuration.TotalSeconds:0.0}s / " +
                        $"{report.ReadDuration.TotalSeconds:0.0}s / {report.CompareDuration.TotalSeconds:0.0}s");
        text.AppendLine();

        text.AppendLine(
            $"{Localization.S("log.reportSummary")}: " +
            $"{Localization.S("log.confHigh")} {report.HighCount}, " +
            $"{Localization.S("log.confMedium")} {report.MediumCount}, " +
            $"{Localization.S("log.confNone")} {report.UnattributedCount}, " +
            $"{Localization.S("log.confNotice")} {report.NoticeCount}");

        if (report.Truncated)
            text.AppendLine(
                Localization.SF("log.reportTruncated", report.Findings.Count, report.TotalFindings));

        if (report.Notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Localization.S("log.notesTitle"));
            foreach (var note in report.Notes) text.AppendLine($"  * {note.Text}");
        }

        if (report.Skipped.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Localization.S("log.reportSkipped"));
            foreach (var skipped in report.Skipped) text.AppendLine($"  {skipped}");
        }

        if (report.IndexWarnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Localization.S("log.indexWarningsTitle"));
            foreach (var warning in report.IndexWarnings) text.AppendLine($"  {warning}");
        }

        if (report.Comparison is { } comparison)
        {
            text.AppendLine();
            text.AppendLine(Localization.S("log.cmpTitle"));

            // Three sources, never merged: the log says what loaded, the folder says what is on disk.
            text.AppendLine($"  {Localization.SF("log.cmpCurrentLog", comparison.LoadedCurrent.Count)}");

            foreach (var mention in comparison.LoadedCurrent)
            {
                var seen = mention.FirstSeen?.ToString("dd.MM, HH:mm") ?? "-";
                text.AppendLine($"    {mention.Name}  ({Localization.S("log.cmpSeen")}: {seen})");
            }

            if (comparison.HasPreviousSession)
                text.AppendLine($"  {Localization.SF("log.cmpPreviousLog", comparison.LoadedPrevious.Count)}");
            else
                text.AppendLine($"  {Localization.S("log.cmpNoPrevious")}");

            text.AppendLine($"  {Localization.SF("log.cmpInstalledNow", comparison.InstalledNow.Count)}");
            text.AppendLine($"    {Localization.S("log.cmpInstalledNote")}");
            text.AppendLine($"    {Localization.S("log.cmpCountsNote")}");

            if (comparison.FirstError is { } error)
                text.AppendLine($"  {Localization.S("log.cmpFirstError")}: {error:dd.MM, HH:mm}");

            if (comparison.NewInCurrent.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"  {Localization.S("log.cmpNewSince")}");

                foreach (var mention in comparison.NewInCurrent)
                {
                    var seen = mention.FirstSeen?.ToString("dd.MM, HH:mm") ?? "-";
                    text.AppendLine($"    + {mention.Name}  ({Localization.S("log.cmpSeen")}: {seen})");

                    var minutes = LogComparison.MinutesBefore(mention, comparison.FirstError);
                    if (minutes is not null)
                        text.AppendLine($"      {Localization.SF("log.cmpMinutes", minutes)}");
                }

                text.AppendLine($"  {Localization.S("log.cmpCoincidence")}");
            }

            if (comparison.MissingInCurrent.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"  {Localization.S("log.cmpGoneSince")}: {string.Join(", ", comparison.MissingInCurrent)}");
            }

            if (comparison.InstalledNotLoaded.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"  {Localization.S("log.cmpInstalledNotLoaded")}: {string.Join(", ", comparison.InstalledNotLoaded)}");
                text.AppendLine($"    {Localization.S("log.cmpInstalledNotLoadedHint")}");
            }

            if (comparison.LoadedNotInstalled.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"  {Localization.S("log.cmpLoadedNotInstalled")}: {string.Join(", ", comparison.LoadedNotInstalled)}");
                text.AppendLine($"    {Localization.S("log.cmpLoadedNotInstalledHint")}");
            }
        }

        if (report.Findings.Count == 0)
        {
            text.AppendLine();
            text.AppendLine(Localization.S("log.reportEmpty"));
            return text.ToString();
        }

        var number = 1;
        foreach (var finding in report.Findings)
        {
            text.AppendLine();
            text.AppendLine(new string('-', 70));
            text.AppendLine($"{number++}. {finding.DisplayTitle}  " +
                            $"[{(finding.IsNotice ? Localization.S("log.confNotice") : Confidence(finding.Confidence))}]");
            text.AppendLine($"{Localization.S("log.fieldTime")}: {finding.Time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");

            if (finding.Occurrences > 1 && finding.FirstSeen is { } first &&
                finding.LastSeen != first)
                text.AppendLine($"{Localization.S("log.fieldFirstLast")}: " +
                                $"{first:HH:mm:ss} / {finding.LastSeen?.ToString("HH:mm:ss") ?? "-"}");
            text.AppendLine($"{Localization.S("log.fieldCount")}: {finding.Occurrences}");

            if (finding.DistinctValues > 0)
                text.AppendLine($"{Localization.S("log.fieldDistinct")}: " +
                                $"{finding.DistinctValues} / {finding.Occurrences}");
            text.AppendLine($"{Localization.S("log.fieldMessage")}: {Trim(finding.Message, 300)}");

            if (finding.Details.Count > 0)
            {
                var more = finding.Details.Count < finding.DistinctValues ? ", ..." : string.Empty;
                text.AppendLine($"{Localization.S("log.fieldDetails")}: " +
                                $"{string.Join(", ", finding.Details)}{more}");
            }

            if (finding.Suspects.Count > 0)
            {
                text.AppendLine(Localization.S("log.fieldSuspects") + ":");

                foreach (var suspect in finding.Suspects.OrderByDescending(s => s.Confidence))
                {
                    text.AppendLine($"  - {suspect.ModName} ({suspect.ModFolder})  [{Confidence(suspect.Confidence)}]");
                    text.AppendLine($"    {suspect.Reason.Text}");

                    if (suspect.Types.Count > 0)
                        text.AppendLine($"    {Localization.S("log.fieldTypes")}: {string.Join(", ", suspect.Types)}");
                }
            }
            else if (!finding.IsNotice)
            {
                text.AppendLine($"{Localization.S("log.fieldSuspects")}: {Localization.S("log.none")}");
            }

            if (finding.Frames.Count > 0)
            {
                text.AppendLine(Localization.S("log.fieldStack") + ":");
                foreach (var frame in finding.Frames.Where(f => !string.IsNullOrWhiteSpace(f)))
                    text.AppendLine($"    at {frame}");
            }

            if (finding.Suggestions.Count > 0)
            {
                text.AppendLine(Localization.S("log.fieldSuggestions") + ":");
                foreach (var suggestion in finding.Suggestions) text.AppendLine($"  * {suggestion.Text}");
            }
        }

        return text.ToString();
    }

    private static string Confidence(LogConfidence confidence) => confidence switch
    {
        LogConfidence.High => Localization.S("log.confHigh"),
        LogConfidence.Medium => Localization.S("log.confMedium"),
        LogConfidence.Low => Localization.S("log.confLow"),
        _ => Localization.S("log.confNone")
    };

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
