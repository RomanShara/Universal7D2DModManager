using System.Text.RegularExpressions;
using U7D2D.Core.I18n;

namespace U7D2D.Core.Logs;

/// <summary>
/// Turns parsed log entries into a diagnosis: which mod, how sure, and what can be done.
///
/// The whole stack is used on purpose. A frame owned by a mod is not automatically the culprit:
/// if the exception was thrown in game code and the mod only appears deeper in the call chain,
/// the mod called into the game and the fault may be on either side. That case is reported as a
/// medium-confidence chain, never as a verdict.
/// </summary>
public sealed partial class LogAnalyzer
{
    private const int MaxFramesInReport = 12;
    private const int MaxFindings = 120;

    [GeneratedRegex(@"Could not find type named\s+(?<type>[\w.`+<>\[\],]+)", RegexOptions.CultureInvariant)]
    private static partial Regex MissingTypeMessage();

    // A different failure from a missing type: the type exists, but the method signature does not.
    [GeneratedRegex(@"Could not find method for type\s+(?<type>[\w.`+<>\[\],]+)\s+and name\s+(?<method>[\w.`+<>\[\],]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex MissingMethodMessage();

    [GeneratedRegex(@"Controller\s*'(?<type>[^',]+),\s*(?<mod>[^']+)'\s*not found",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ControllerMissing();

    [GeneratedRegex(@"Type:(?<type>[^,]+),\s*(?<mod>\S+)\s+was missing when we looked it up via Type\.GetType\(\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TypeLookupMissing();

    [GeneratedRegex(@"ActionTarget implicitly converted to Vector3, but failed",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PropsConversion();

    [GeneratedRegex(@"XML patch for ""(?<file>[^""]+)"" from mod ""(?<mod>[^""]+)"" did not apply",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex XmlPatchFromMod();

    [GeneratedRegex(@"XML\.Patch\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex XmlPatchUnknown();

    [GeneratedRegex(@"did not apply:\s*<(?<op>\w+)[^>]*?(?:xpath\s*=\s*""(?<xpath>[^""]*)""|name\s*=\s*""(?<name>[^""]*)"")",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex XmlPatchTarget();

    [GeneratedRegex(@"@name=(['""])(?<name>[^'""]+)\1", RegexOptions.CultureInvariant)]
    private static partial Regex XmlTargetName();

    // Only a field that explicitly names a mod counts as a link. A bare word inside an XPath or a
    // message is not evidence and must never produce a high-confidence suspect.
    [GeneratedRegex(@"\b(?:mod|folder)\s*[:=]\s*'?(?<name>[A-Za-z0-9_.\-]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitModName();

    [GeneratedRegex(@"\b(?<mod>[A-Za-z0-9_.\-]+\.xml)\b", RegexOptions.CultureInvariant)]
    private static partial Regex XmlFileName();

    // "No action class 'NPCNoVoiceTag, 1-XNPCVoiceControl found!" names the mod after the comma.
    [GeneratedRegex(@"No action class '[^,]+,\s*(?<mod>[A-Za-z0-9_.\-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ActionClassName();

    // Gears keeps saved values for other mods. When a mod update removes a setting, Gears prints
    // one warning per old key and uses the default. That is a notice, not a broken mod.
    [GeneratedRegex(@"\[SavedModSettingsFromXml\]\s*Mod\s+(?<mod>[^\s:]+)\s*:\s*Setting\s+(?<setting>[A-Za-z0-9_]+)\s+not found.*using default value",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SavedSettingsMessage();

    [GeneratedRegex(@"\bSetting\s+(?<setting>[A-Za-z0-9_]+)\s+not found",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SettingNotFound();

    [GeneratedRegex(@"-?\d+", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();

    private static readonly string[] Noise =
    {
        "No device binding source",
        "Tick took exceptionally long",
        "is missing the UnityEngine.Scripting.Preserve attribute",
        "no model for",
        "Platform config file has no valid entry",
        "Full Discord integration only available"
    };

    public LogDiagnosisReport Analyze(IEnumerable<LogEntry> entries, ModTypeIndex index)
    {
        var report = new LogDiagnosisReport
        {
            ModsIndexed = index.ModCount,
            TypesIndexed = index.TypeCount
        };

        var grouped = new Dictionary<string, LogFinding>(StringComparer.Ordinal);
        var seenValues = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            report.EntriesParsed++;

            if (!IsRelevant(entry)) continue;

            var key = GroupKey(entry);
            if (grouped.TryGetValue(key.Key, out var existing))
            {
                existing.Occurrences++;

                if (entry.Time is { } later &&
                    (existing.LastSeen is null || later > existing.LastSeen))
                    existing.LastSeen = later;

                if (key.Detail is not null)
                {
                    var values = seenValues[key.Key];
                    if (values.Add(key.Detail))
                    {
                        existing.DistinctValues = values.Count;
                        if (existing.Details.Count < 20) existing.Details.Add(key.Detail);
                    }
                }

                continue;
            }

            var finding = Classify(entry, index);
            if (finding is null) continue;

            if (key.Detail is not null)
            {
                var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key.Detail };
                seenValues[key.Key] = values;
                finding.DistinctValues = values.Count;

                if (!finding.Details.Any(d => string.Equals(d, key.Detail, StringComparison.OrdinalIgnoreCase)))
                    finding.Details.Add(key.Detail);
            }

            finding.FirstSeen = entry.Time;
            finding.LastSeen = entry.Time;
            grouped[key.Key] = finding;
        }

        report.TotalFindings = grouped.Count;

        report.Findings.AddRange(grouped.Values
            .OrderByDescending(f => f.ExceptionType is not null)
            // A self-healing notice is never more important than a real error.
            .ThenBy(f => f.IsNotice)
            .ThenByDescending(f => f.Confidence)
            .ThenByDescending(f => f.Severity)
            .ThenByDescending(f => f.Occurrences)
            .Take(MaxFindings));

        // One place for the reasoning that applies to every card. Previously it was appended to
        // each unattributed finding, so the same paragraph appeared twenty times in a report.
        report.Notes.Add(new Loc("log.notesNoCulprit"));
        report.Notes.Add(new Loc("log.notesRepeats"));

        // The game reports a missing class through three independent mechanisms. When any of them
        // appears, one shared note explains that the same class name can show up under several with
        // different counts - they are lookup paths of one problem, not duplicated errors.
        if (report.Findings.Any(f => f.TitleLoc?.Key is "log.titleControllerMissing"
                or "log.titleTypeLookupMissing" or "log.titleActionClass"))
            report.Notes.Add(new Loc("log.notesLookupPaths"));

        return report;
    }

    /// <summary>Cheap pre-filter, also used while parsing so noise never reaches memory.</summary>
    public static bool IsRelevant(LogEntry entry)
    {
        if (entry.Frames.Count > 0) return true;
        if (entry.Severity == LogSeverity.Error) return true;

        // Informational lines are not problems. "Loading localization from mod: CasesMod" is the game
        // telling you what it loaded, and putting it in the report buries the real errors.
        if (entry.Severity != LogSeverity.Warning) return false;

        // A warning is kept only when it sounds like a failure, not when it merely mentions a mod.
        var text = entry.Message;
        return text.Contains("Harmony", StringComparison.OrdinalIgnoreCase)
               || text.Contains("XML", StringComparison.OrdinalIgnoreCase)
               || text.Contains("failed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Could not", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cannot", StringComparison.OrdinalIgnoreCase)
               || text.Contains("missing", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not found", StringComparison.OrdinalIgnoreCase)
               || text.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    private LogFinding? Classify(LogEntry entry, ModTypeIndex index)
    {
        var text = $"{entry.Tag} {entry.Message}";
        if (Noise.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase))) return null;

        var finding = new LogFinding
        {
            Severity = entry.Severity,
            Time = entry.Time,
            ExceptionType = entry.ExceptionType,
            Message = string.IsNullOrWhiteSpace(entry.Message) ? entry.Tag : entry.Message,
            Title = entry.ExceptionType ?? (entry.Tag.Length > 0 ? entry.Tag : entry.Message)
        };

        foreach (var frame in entry.Frames
                     .Where(f => !string.IsNullOrWhiteSpace(f.ToString()))
                     .Take(MaxFramesInReport))
            finding.Frames.Add(frame.ToString());

        var saved = SavedSettingsMessage().Match(entry.Message);
        if (saved.Success)
        {
            // The message is printed by the settings framework (the tag), while the settings belong
            // to another mod. Naming the framework as the culprit would be wrong twice over.
            var mod = saved.Groups["mod"].Value;
            var host = FindFolder(mod, index) ?? mod;

            finding.IsNotice = true;
            finding.TitleLoc = new Loc("log.titleSavedSettings", mod);
            finding.Details.Add(saved.Groups["setting"].Value);
            finding.Suggestions.Add(new Loc("log.hintSavedSettings", mod, entry.Tag, host));

            return finding;
        }

        var controller = ControllerMissing().Match(entry.Message);
        if (controller.Success)
        {
            ApplyMissingClass(finding, controller.Groups["mod"].Value.Trim(),
                "log.titleControllerMissing", "log.hintControllerMissing",
                "log.hintControllerMissingAbsent", index);
            return finding;
        }

        var lookup = TypeLookupMissing().Match(entry.Message);
        if (lookup.Success)
        {
            ApplyMissingClass(finding, lookup.Groups["mod"].Value.Trim(),
                "log.titleTypeLookupMissing", "log.hintTypeLookupMissing",
                "log.hintTypeLookupMissingAbsent", index);
            return finding;
        }

        if (PropsConversion().IsMatch(entry.Message))
        {
            finding.TitleLoc = new Loc("log.titlePropsConversion");
            finding.Suggestions.Add(new Loc("log.hintPropsConversion"));
            return finding;
        }

        // "No action class 'NPCNoVoiceTag, 1-XNPCVoiceControl found!" is a third missing-class
        // mechanism (action-class registration). The matching EXC line carries a stack, but the
        // message alone is what identifies it, so handle it here - before the frame dispatch - and
        // let the stack be copied onto the card afterwards.
        var actionClass = ActionClassName().Match(entry.Message);
        if (actionClass.Success)
        {
            var named = actionClass.Groups["mod"].Value;
            var host = InstalledFolder(named, index);

            finding.TitleLoc = new Loc("log.titleActionClass", named);
            finding.Suggestions.Add(host is not null
                ? new Loc("log.hintMissingActionClass", named, host)
                : new Loc("log.hintModNotInstalled", named));

            // The log named an assembly, but the class inside it is what is missing. That is
            // information about context, not a culprit, so Suspects stays empty.
            finding.Suggestions.Add(new Loc("log.hintActionClassPath"));

            return finding;
        }

        var xmlMod = XmlPatchFromMod().Match(entry.Message);
        if (xmlMod.Success)
        {
            var mod = xmlMod.Groups["mod"].Value;
            finding.TitleLoc = new Loc("log.titleXmlPatchMod", mod);
            finding.Suggestions.Add(new Loc("log.hintXmlPatchMod", mod));
            return finding;
        }

        if (XmlPatchUnknown().IsMatch(entry.Message))
        {
            finding.TitleLoc = new Loc("log.titleXmlPatchUnknown");
            finding.Suggestions.Add(new Loc("log.hintXmlPatchUnknown"));
            return finding;
        }

        if (entry.Frames.Count > 0)
            AttributeByStack(entry, finding, index);
        else
            AttributeByMessage(entry, finding, index);

        // The log frequently names the mod by its folder: "Loading localization from mod: CasesMod",
        // "Trying to load from folder: '00000-Gears'". That is stronger than any guess from a stack,
        // so it fills in when nothing else matched.
        // Only the message is searched. A tag like [Gears] names the component that printed the
        // line, not necessarily the mod responsible for it.
        if (finding.Suspects.Count == 0)
            AttributeByName(entry.Message, finding, index);

        AddSuggestions(finding);
        return finding;
    }

    /// <summary>
    /// Walks the stack from the throwing frame downwards. The first frame owned by a mod decides the
    /// confidence, the rest of the stack decides whether that mod is really the source.
    /// </summary>
    private static void AttributeByStack(LogEntry entry, LogFinding finding, ModTypeIndex index)
    {
        var owners = new Dictionary<string, (TypeOwner Owner, int Frame, LogConfidence Confidence, Loc Reason)>(
            StringComparer.OrdinalIgnoreCase);

        StackFrame? firstGameFrame = null;
        var patchedOnly = new Dictionary<string, TypeOwner>(StringComparer.OrdinalIgnoreCase);

        foreach (var frame in entry.Frames)
        {
            var defining = index.Resolve(frame.TypeName);

            if (defining.Count > 0)
            {
                // A type defined by several mods cannot be pinned to one of them.
                var ambiguous = defining.Select(o => o.ModFolder).Distinct().Count() > 1;

                foreach (var owner in defining)
                {
                    var confidence = frame.Index == 0
                        ? (ambiguous ? LogConfidence.Medium : LogConfidence.High)
                        : LogConfidence.Medium;

                    var reason = frame.Index == 0
                        ? new Loc("log.reasonThrownIn", frame.ToString())
                        : new Loc("log.reasonInChain", frame.ToString(), frame.Index);

                    if (ambiguous)
                        reason = new Loc("log.reasonAmbiguous", frame.TypeName, defining.Count);

                    if (!owners.TryGetValue(owner.ModFolder, out var current) ||
                        confidence > current.Confidence ||
                        (confidence == current.Confidence && frame.Index < current.Frame))
                    {
                        owners[owner.ModFolder] = (owner, frame.Index, confidence, reason);
                    }
                }

                continue;
            }

            firstGameFrame ??= frame;

            // No mod defines this type, but a mod may patch it. The patch code itself is not in
            // the trace, so this can only ever be a suspicion.
            foreach (var patcher in index.ResolvePatched(frame.TypeName))
                patchedOnly.TryAdd(patcher.ModFolder, patcher);
        }

        foreach (var (_, value) in owners)
        {
            var blame = new ModBlame
            {
                ModFolder = value.Owner.ModFolder,
                ModName = value.Owner.ModName,
                Confidence = value.Confidence,
                Reason = value.Reason
            };

            blame.Types.Add(entry.Frames[value.Frame].TypeName);
            finding.Suspects.Add(blame);
        }

        // Only when no mod code appears anywhere in the stack do patched methods become suspects.
        if (finding.Suspects.Count == 0)
        {
            foreach (var patcher in patchedOnly.Values)
            {
                var blame = new ModBlame
                {
                    ModFolder = patcher.ModFolder,
                    ModName = patcher.ModName,
                    Confidence = LogConfidence.Low,
                    Reason = new Loc("log.reasonPatched", firstGameFrame?.ToString() ?? string.Empty)
                };

                if (firstGameFrame is not null) blame.Types.Add(firstGameFrame.TypeName);
                finding.Suspects.Add(blame);
            }
        }

        if (finding.Suspects.Count > 1)
        {
            finding.Suggestions.Add(new Loc("log.hintSeveralMods"));
        }
    }

    /// <summary>Findings without a stack: Harmony complaints, XML patch errors, version warnings.</summary>
    private static void AttributeByMessage(LogEntry entry, LogFinding finding, ModTypeIndex index)
    {
        var text = $"{entry.Tag} {entry.Message}";

        var missingMethod = MissingMethodMessage().Match(text);
        if (missingMethod.Success)
        {
            var ownerType = missingMethod.Groups["type"].Value;
            var method = missingMethod.Groups["method"].Value;

            finding.TitleLoc = new Loc("log.titleMissingMethod", ShortName(ownerType), method);
            finding.MissingType = true;
            finding.Suggestions.Add(new Loc("log.hintMissingMethod", ownerType, method));

            return;
        }

        var missing = MissingTypeMessage().Match(text);
        if (missing.Success)
        {
            var typeName = missing.Groups["type"].Value;
            var ns = typeName.Contains('.') ? typeName[..typeName.LastIndexOf('.')] : typeName;

            finding.TitleLoc = new Loc("log.titleMissingType", ShortName(typeName));
            finding.MissingType = true;

            // A name in an attribute is not proof of a broken mod. No suspect is added here: the
            // report explains what a missing type can mean and leaves "no culprit" in place.
            finding.Suggestions.Add(new Loc("log.hintMissingType", typeName, ns));

            return;
        }

        var xml = XmlFileName().Match(text);
        var patchProblem = text.Contains("Patch operations failed", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("XML patch", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("patch failed", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("Could not find", StringComparison.OrdinalIgnoreCase);

        if (xml.Success && patchProblem)
        {
            finding.TitleLoc = new Loc("log.titleXml");
            finding.Suggestions.Add(new Loc("log.hintXml", xml.Groups["mod"].Value));
            return;
        }

        if (text.Contains("Harmony", StringComparison.OrdinalIgnoreCase))
        {
            finding.TitleLoc = new Loc("log.titleHarmony");
            finding.Suggestions.Add(new Loc("log.hintHarmony"));
        }
    }

    /// <summary>
    /// Only an explicit "mod: X" / "folder: 'X'" field is accepted as a link to a mod. Matching
    /// any occurrence of a folder name inside arbitrary text produced confident but fabricated
    /// suspects, for example when a mod name happened to appear inside an XPath.
    /// </summary>
    private static void AttributeByName(string message, LogFinding finding, ModTypeIndex index)
    {
        var match = ExplicitModName().Match(message);
        if (!match.Success) return;

        var name = match.Groups["name"].Value;
        var folder = InstalledFolder(name, index);
        if (folder is null) return;

        AddSuspect(finding, new TypeOwner { ModFolder = folder, ModName = folder },
            LogConfidence.High, new Loc("log.reasonNamedInLog", name));
    }

    /// <summary>
    /// Folder names carry load-order prefixes that the log does not repeat: "00000-Gears" is logged
    /// as "Gears", "Zzzzzzzzzzzzzzzz_CasesMod" as "CasesMod". Both the full name and the stripped one
    /// are tried, and the stripped one has to stay long enough to be distinctive.
    /// </summary>
    private static IEnumerable<string> NameCandidates(string folder)
    {
        yield return folder;

        var noDigits = folder.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-', '_', ' ', '.');
        if (noDigits.Length >= 4 && noDigits.Length != folder.Length) yield return noDigits;

        var noPadding = folder.TrimStart('z', 'Z', 'x', 'X', '0', '-', '_', ' ', '.');
        if (noPadding.Length >= 4 && noPadding.Length != folder.Length) yield return noPadding;

        if (folder.Contains('_'))
        {
            var spaced = folder.Replace('_', ' ');
            if (spaced.Length >= 4) yield return spaced;
        }
    }

    /// <summary>
    /// A missing XUi controller or class is not a missing mod. The assembly can be installed and
    /// still fail to register the class, so the report names the class and where the DLL lives,
    /// without adding a suspect.
    /// </summary>
    private static void ApplyMissingClass(
        LogFinding finding, string assembly, string titleKey, string hintKey,
        string absentHintKey, ModTypeIndex index)
    {
        var host = InstalledFolder(assembly, index);

        finding.TitleLoc = new Loc(titleKey, assembly);
        finding.Suggestions.Add(host is not null
            ? new Loc(hintKey, assembly, host)
            : new Loc(absentHintKey, assembly));

        // Two different lookups can report the same class name. Say so once per card, with the
        // wording that fits that card, so the two counts do not look like a duplicated error.
        finding.Suggestions.Add(new Loc(titleKey == "log.titleControllerMissing"
            ? "log.hintControllerPath"
            : "log.hintTypeLookupPath"));
    }

    /// <summary>Where a name from the log actually lives: folder, DLL, or declared mod name.</summary>
    private static string? InstalledFolder(string name, ModTypeIndex index)
    {
        var folder = FindFolder(name, index);
        if (folder is not null) return folder;
        if (index.AssemblyFolders.TryGetValue(name, out var host)) return host;
        if (index.ModInfoAliases.TryGetValue(name, out var declared)) return declared;
        return null;
    }

    /// <summary>
    /// Nearly identical warnings differ only by a setting name, a chunk key or world coordinates.
    /// Grouping on the raw message produced dozens of cards for one problem, so those values are
    /// folded into a placeholder and kept in Details instead.
    /// </summary>
    private static (string Key, string? Detail) GroupKey(LogEntry entry)
    {
        var message = entry.Message;
        string? detail = null;

        var controller = ControllerMissing().Match(message);
        var lookup = TypeLookupMissing().Match(message);
        var setting = SettingNotFound().Match(message);
        var xmlMod = XmlPatchFromMod().Match(message);

        if (xmlMod.Success)
        {
            // "x48" may be 48 attempts at one XPath or 48 different patches in the same file.
            // Keeping file + target as the value makes that visible.
            // "x48" may be 48 attempts at one XPath or 48 different patches in the same file.
            // Keeping file + target as the value makes that visible. Patches come in two shapes:
            //   <append xpath="/items/item[@name='gun']">   -> node is the @name inside the XPath
            //   <passive_effect name="LootProb" ...>         -> node is a direct name attribute
            var target = XmlPatchTarget().Match(message);
            string shortTarget;

            if (target.Groups["name"].Success && target.Groups["name"].Value.Length > 0)
            {
                shortTarget = target.Groups["name"].Value;
            }
            else if (target.Groups["xpath"].Success && target.Groups["xpath"].Value.Length > 0)
            {
                var xpath = target.Groups["xpath"].Value;
                var named = XmlTargetName().Match(xpath);
                shortTarget = named.Success
                    ? named.Groups["name"].Value
                    : xpath.Length > 60 ? xpath[..60] + "..." : xpath;
            }
            else
            {
                shortTarget = target.Success ? target.Groups["op"].Value : "unknown target";
            }

            detail = $"{xmlMod.Groups["file"].Value} [{shortTarget}]";
            message = $"XML patch from mod '{xmlMod.Groups["mod"].Value}' did not apply";
        }
        else if (XmlPatchUnknown().IsMatch(message))
        {
            message = "XML.Patch unknown patch type";
        }
        else if (controller.Success)
        {
            // Every missing controller from the same assembly is one problem. Class names are kept
            // in Details instead of producing a card with the same advice for each class.
            detail = controller.Groups["type"].Value.Trim();
            // The "--- hierarchy: ..." tail is different for every window and is pure context.
            message = $"Controller '{controller.Groups["mod"].Value.Trim()}' not found";
        }
        else if (lookup.Success)
        {
            detail = lookup.Groups["type"].Value.Trim();
            message = $"Type:{lookup.Groups["mod"].Value.Trim()} was missing";
        }
        else if (setting.Success)
        {
            detail = setting.Groups["setting"].Value;
            message = SettingNotFound().Replace(message, "Setting * not found");
        }

        message = LongNumber().Replace(message, "#");

        var frames = string.Join("|", entry.Frames.Take(3).Select(f => f.ToString()));
        return ($"{entry.ExceptionType ?? entry.Tag}|{message}|{frames}", detail);
    }

    /// <summary>Resolves a mod name from the log to a folder, tolerating load-order prefixes.</summary>
    private static string? FindFolder(string name, ModTypeIndex index)
    {
        var wanted = Loose(name);

        foreach (var folder in index.ModFolders)
            if (Loose(folder) == wanted || NameCandidates(folder).Any(c => Loose(c) == wanted))
                return folder;

        // ModInfo.xml declares the name the game prints in the log. It often has nothing in common
        // with the folder: "Dir2104_TargetAnalyzer" lives in "TargetAnalyzer".
        foreach (var (alias, folder) in index.ModInfoAliases)
            if (Loose(alias) == wanted) return folder;

        return null;
    }

    private static string Loose(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));

        return builder.ToString();
    }

    private static void AddSuspect(LogFinding finding, TypeOwner owner, LogConfidence confidence, Loc reason)
    {
        if (finding.Suspects.Any(s => string.Equals(s.ModFolder, owner.ModFolder, StringComparison.OrdinalIgnoreCase)))
            return;

        finding.Suspects.Add(new ModBlame
        {
            ModFolder = owner.ModFolder,
            ModName = owner.ModName,
            Confidence = confidence,
            Reason = reason
        });
    }

    private static void AddSuggestions(LogFinding finding)
    {
        // General reasoning lives in LogDiagnosisReport.Notes and is shown once for the whole
        // report. Only advice specific to this finding belongs on a card.
        var primary = finding.Primary;
        if (primary is null) return;

        if (primary.Confidence == LogConfidence.High && finding.Frames.Count > 0)
            finding.Suggestions.Add(new Loc("log.hintReportToAuthor", primary.ModName));

        if (finding.Confidence <= LogConfidence.Low)
            finding.Suggestions.Add(new Loc("log.hintLowConfidence"));
    }

    private static string ShortName(string typeName)
    {
        var plus = typeName.LastIndexOf('+');
        var name = plus >= 0 ? typeName[(plus + 1)..] : typeName;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }
}
