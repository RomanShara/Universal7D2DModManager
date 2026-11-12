using System.Xml.Linq;
using System.Xml.XPath;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Analysis;

/// <summary>
/// Detects real overlaps between mods: identical config files, identical XPath targets,
/// the same XUi windows, duplicate DLL names and multiple Harmony copies.
/// </summary>
public sealed class ConflictAnalyzer
{
    private const int MaxFilesPerMod = 120;
    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int MaxConflicts = 300;

    private sealed class ModSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public string TechnicalName { get; init; } = string.Empty;
        public string Folder { get; init; } = string.Empty;

        /// <summary>Position in load order, used to tell which value survives.</summary>
        public int Order { get; set; }

        /// <summary>Mods this one declares as dependencies; used to recognise related mods.</summary>
        public List<string> Dependencies { get; init; } = new();

        /// <summary>Assemblies this mod's DLLs reference; links it to the mod that ships them.</summary>
        public HashSet<string> AssemblyReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ConfigFileAnalysis> Configs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DllNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> WindowNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> XPathTargets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string File, ConfigPatch Patch)> Patches { get; } = new();
        public bool ShipsHarmony { get; set; }
        public bool FullReplacementUsed { get; set; }
    }

    public ConflictReport AnalyzeInstalled(IReadOnlyList<InstalledMod> mods, VanillaConfigResolver? resolver = null)
    {
        var report = new ConflictReport();
        var snapshots = BuildLoadOrder(mods.Select(CreateSnapshot).ToList());
        report.LoadOrder.AddRange(snapshots.Select(s => s.Name));

        if (snapshots.Count == 0)
        {
            report.Notes.Add(Localization.S("an3.noMods"));
            return report;
        }

        // One mod cannot conflict with itself, but its patches still have to be checked against the game.
        if (snapshots.Count < 2)
            report.Notes.Add(Localization.S("an3.singleMod"));

        // Duplicate mod names installed into different folders. Compared by the technical <Name>,
        // because different mods legitimately share a display name.
        foreach (var group in snapshots
                     .Where(s => !string.IsNullOrWhiteSpace(s.TechnicalName))
                     .GroupBy(s => s.TechnicalName, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var folders = group.Select(g => g.Folder).ToList();
            var titles = group.Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            Add(report, new ConflictEntry
            {
                Kind = ConflictKind.DuplicateModName,
                Severity = FindingSeverity.Critical,
                ModA = folders[0],
                ModB = folders[1],
                Subject = group.Key,
                Detail = Localization.SF("an3.duplicateModName",
                    group.Key,
                    titles.Count == 1 ? $" ({titles[0]})" : string.Empty,
                    string.Join(", ", folders)),
                Confidence = 0.95
            });
        }

        // Same config file touched by several mods.
        var configOwners = new Dictionary<string, List<(ModSnapshot Mod, ConfigFileAnalysis File)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var (path, analysis) in snapshot.Configs)
            {
                if (!configOwners.TryGetValue(path, out var owners))
                    configOwners[path] = owners = new List<(ModSnapshot, ConfigFileAnalysis)>();
                owners.Add((snapshot, analysis));
            }
        }

        // A wholesale replacement is not automatically a loss: what matters is whether the replacing
        // file already contains the other mods' changes. That is decided by comparing content, below.
        var replacementConflicts = AnalyzeFullReplacements(configOwners, resolver, report);

        var nodeConflicts = AnalyzeNodeConflicts(snapshots, resolver, report);
        AnalyzePatchApplication(snapshots, resolver, report);

        // Files several mods patch without touching the same node: explicitly not a conflict.
        var sharedButDisjoint = configOwners
            .Where(kv => kv.Value.Count > 1 && !kv.Value.Any(o => o.File.FullReplacement) &&
                         !nodeConflicts.Contains(kv.Key) && !replacementConflicts.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        report.CoexistenceCount = sharedButDisjoint.Count;

        if (sharedButDisjoint.Count > 0)
        {
            report.Notes.Add(Localization.SF("an3.sharedDisjoint",
                sharedButDisjoint.Count,
                string.Join(", ", sharedButDisjoint.Take(5)),
                sharedButDisjoint.Count > 5 ? " …" : string.Empty));
        }

        // Same XUi window definitions.
        var windowOwners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var window in snapshot.WindowNames)
            {
                if (!windowOwners.TryGetValue(window, out var list))
                    windowOwners[window] = list = new List<string>();
                list.Add(snapshot.Name);
            }
        }

        foreach (var (window, owners) in windowOwners.Where(kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            Add(report, new ConflictEntry
            {
                Kind = ConflictKind.SameXuiWindow,
                Severity = FindingSeverity.Medium,
                ModA = owners[0],
                ModB = owners[1],
                Subject = window,
                Detail = Localization.SF("an3.sameWindow", window),
                Confidence = 0.8
            });
        }

        // Duplicate DLL file names.
        var dllOwners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var dll in snapshot.DllNames)
            {
                if (!dllOwners.TryGetValue(dll, out var list))
                    dllOwners[dll] = list = new List<string>();
                list.Add(snapshot.Name);
            }
        }

        foreach (var (dll, owners) in dllOwners.Where(kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            Add(report, new ConflictEntry
            {
                Kind = ConflictKind.DuplicateDll,
                Severity = FindingSeverity.Medium,
                ModA = owners[0],
                ModB = owners[1],
                Subject = dll,
                Detail = Localization.S("an3.duplicateDll"),
                Confidence = 0.85
            });
        }

        var harmonyMods = snapshots.Where(s => s.ShipsHarmony).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (harmonyMods.Count > 1)
        {
            Add(report, new ConflictEntry
            {
                Kind = ConflictKind.DuplicateHarmony,
                Severity = FindingSeverity.High,
                ModA = harmonyMods[0],
                ModB = harmonyMods[1],
                Subject = "0Harmony.dll",
                Detail = Localization.SF("an3.multipleHarmony", string.Join(", ", harmonyMods)),
                Confidence = 0.9
            });
        }

        MarkRelatedMods(snapshots, report);

        // Most harmful first, so the list reads as a priority order.
        report.Conflicts.Sort((a, b) =>
        {
            var byRelation = b.Relation.CompareTo(a.Relation);
            return byRelation != 0 ? byRelation : b.Severity.CompareTo(a.Severity);
        });

        if (report.Conflicts.Count == 0)
            report.Notes.Add(Localization.S("an3.noOverlaps"));

        report.Notes = report.Notes.Distinct().ToList();

        report.Notes.Add(Localization.S("an3.categories"));
        return report;
    }

    /// <summary>
    /// Handles overlaps between mods that depend on each other. When the dependent mod loads after its
    /// dependency, its definition simply overrides the one it builds on — that is the intended layering
    /// and not a conflict. The problem is the opposite order: the dependent mod loads first and its
    /// changes are then wiped by the mod it needs.
    /// </summary>
    private static void MarkRelatedMods(List<ModSnapshot> snapshots, ConflictReport report)
    {
        var byName = new Dictionary<string, ModSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            byName.TryAdd(snapshot.Name, snapshot);
            byName.TryAdd(snapshot.TechnicalName, snapshot);
        }

        var byAssembly = BuildAssemblyOwners(snapshots);
        var intended = new List<ConflictEntry>();

        foreach (var conflict in report.Conflicts)
        {
            if (string.IsNullOrEmpty(conflict.ModB) || conflict.ModA == conflict.ModB) continue;
            if (!byName.TryGetValue(conflict.ModA, out var first)) continue;
            if (!byName.TryGetValue(conflict.ModB, out var second)) continue;

            var firstDepends = DependsOn(first, second, byAssembly);
            var secondDepends = DependsOn(second, first, byAssembly);
            if (!firstDepends && !secondDepends) continue;

            var dependent = firstDepends ? first : second;
            var dependency = firstDepends ? second : first;

            conflict.Related = true;

            if (dependent.Order > dependency.Order)
            {
                // Loads later, so its version wins: the overlap is by design.
                intended.Add(conflict);
                continue;
            }

            conflict.Severity = FindingSeverity.High;
            conflict.Detail += Localization.SF("an3.dependentEarlier", dependent.Name, dependency.Name);
        }

        foreach (var conflict in intended)
        {
            report.Conflicts.Remove(conflict);
            report.Notes.Add(Localization.SF("an3.intendedOverlap", conflict.Subject, conflict.ModA, conflict.ModB));
        }
    }

    /// <summary>
    /// A mod depends on another when it says so in ModInfo, or when one of its DLLs references an
    /// assembly that only the other mod ships. The second signal is what links mods that declare
    /// their dependency only on the download page.
    /// </summary>
    private static bool DependsOn(ModSnapshot mod, ModSnapshot other, Dictionary<string, ModSnapshot> byAssembly)
    {
        if (mod.Dependencies.Any(d =>
                d.Equals(other.TechnicalName, StringComparison.OrdinalIgnoreCase) ||
                d.Equals(other.Name, StringComparison.OrdinalIgnoreCase)))
            return true;

        foreach (var reference in mod.AssemblyReferences)
            if (byAssembly.TryGetValue(reference, out var owner) && ReferenceEquals(owner, other))
                return true;

        return false;
    }

    private static Dictionary<string, ModSnapshot> BuildAssemblyOwners(List<ModSnapshot> snapshots)
    {
        var owners = new Dictionary<string, ModSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in snapshots)
            foreach (var dll in snapshot.DllNames)
                owners.TryAdd(Path.GetFileNameWithoutExtension(dll), snapshot);

        return owners;
    }

    private static void ReadReferences(ModSnapshot snapshot, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 32L * 1024 * 1024) return;

            foreach (var reference in AssemblyInspector.ReadAssemblyReferences(File.ReadAllBytes(path)))
                snapshot.AssemblyReferences.Add(reference);
        }
        catch (Exception)
        {
            // A DLL that cannot be read contributes nothing.
        }
    }

    /// <summary>
    /// Orders mods the way the game reads them: by folder name inside Mods, without case sensitivity.
    /// 7 Days to Die applies the patches in that order, so the last writer wins. The resulting order is
    /// reported so the winner named in a conflict can be checked against it.
    /// </summary>
    private static List<ModSnapshot> BuildLoadOrder(List<ModSnapshot> snapshots)
    {
        var ordered = snapshots
            .OrderBy(s => s.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        return ordered;
    }

    /// <summary>
    /// Replays every patch on a copy of the game config in load order and reports the ones that
    /// find no target. This is the only check that can tell "the mod changes something" apart from
    /// "the mod's change silently does nothing".
    /// </summary>
    private static void AnalyzePatchApplication(
        List<ModSnapshot> snapshots,
        VanillaConfigResolver? resolver,
        ConflictReport report)
    {
        if (resolver is null || !resolver.IsAvailable)
        {
            report.Notes.Add(Localization.S("an3.noVanillaConfig"));
            return;
        }

        var byFile = new Dictionary<string, List<(string Mod, ConfigPatch Patch)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var (file, patch) in snapshot.Patches)
            {
                if (!byFile.TryGetValue(file, out var list)) byFile[file] = list = new();
                list.Add((snapshot.Name, patch));
            }
        }

        var skippedFiles = 0;
        var unsupported = 0;

        foreach (var (file, patches) in byFile.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var document = resolver.CreateWorkingCopy(file);
            var pristine = resolver.CreateWorkingCopy(file);
            if (document is null || pristine is null)
            {
                skippedFiles++;
                continue;
            }

            var simulation = ConfigPatchSimulator.Run(file, document, patches);
            unsupported += simulation.Outcomes.Count(o => o.Status == PatchStatus.Unsupported);

            for (var i = 0; i < simulation.Outcomes.Count; i++)
            {
                var outcome = simulation.Outcomes[i];
                if (outcome.Status != PatchStatus.TargetMissing) continue;

                // "Remove if present" is a normal idiom: the mod adds the node later in the same file.
                if (AddsLater(patches, i)) { outcome.Status = PatchStatus.Unsupported; continue; }

                var target = patches[i].Patch.TargetPath;

                // The node existed and an earlier mod removed or replaced it: order-dependent.
                if (ExistsIn(pristine, target)) { outcome.Status = PatchStatus.RemovedByEarlier; continue; }

                // Walk up to the nearest ancestor that exists in the game. If that ancestor is a named
                // node, its contents changed between versions; if not, the whole named node is absent.
                outcome.Status = PatchStatus.OptionalTarget;
                var ancestor = target;

                for (var depth = 0; depth < 10; depth++)
                {
                    ancestor = ParentPath(ancestor);
                    if (ancestor is null) break;
                    if (!ExistsIn(pristine, ancestor)) continue;

                    outcome.Status = ancestor.Contains("[@name", StringComparison.OrdinalIgnoreCase)
                        ? PatchStatus.PathChanged
                        : PatchStatus.OptionalTarget;
                    break;
                }
            }

            var failures = simulation.Outcomes
                .Where(o => o.Status is not (PatchStatus.Applied or PatchStatus.Unsupported))
                .ToList();
            if (failures.Count == 0) continue;

            foreach (var group in failures.GroupBy(f => f.Mod, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var byStatus in group.GroupBy(f => f.Status))
                {
                    var items = byStatus.ToList();
                    var (reason, note) = DescribePatchFailure(byStatus.Key);

                    // A remove that finds nothing does no harm; a lost write is the mod's effect gone.
                    var writes = items.Where(f => !IsRemoval(f.Operation)).ToList();
                    var removals = items.Where(f => IsRemoval(f.Operation)).ToList();

                    EmitPatchFinding(report, file, group.Key, writes, WriteSeverity(byStatus.Key), reason, note);
                    EmitPatchFinding(report, file, group.Key, removals, FindingSeverity.Low,
                        reason + Localization.S("an2.removalSuffix"), Localization.S("an2.nothingToRemove"));
                }
            }
        }

        if (skippedFiles > 0)
            report.Notes.Add(Localization.SF("an3.skippedFiles", skippedFiles));

        if (unsupported > 0)
            report.Notes.Add(Localization.SF("an3.unsupportedPatches", unsupported));
    }

    /// <summary>
    /// True when the same mod adds the very node the patch failed to find later in the same file.
    /// "Remove if present, then add" is a normal idiom and must not be reported as a broken patch.
    /// </summary>
    /// <summary>
    /// Judges a wholesale file replacement by content instead of by the operation type: an identical
    /// copy changes nothing, a copy that already contains another mod's change keeps it, and only a
    /// copy that dropped that change is a real conflict.
    /// </summary>
    private static HashSet<string> AnalyzeFullReplacements(
        Dictionary<string, List<(ModSnapshot Mod, ConfigFileAnalysis File)>> configOwners,
        VanillaConfigResolver? resolver,
        ConflictReport report)
    {
        var conflicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, owners) in configOwners.Where(kv => kv.Value.Count > 1))
        {
            var replacers = owners.Where(o => o.File.FullReplacement).OrderBy(o => o.Mod.Order).ToList();
            if (replacers.Count == 0) continue;

            if (replacers.Count > 1)
            {
                var names = string.Join(", ", replacers.Select(r => r.Mod.Name));
                report.Notes.Add(Localization.SF("an3.multiReplace", path, names));
            }

            var last = replacers[^1];
            var vanilla = resolver?.CreateWorkingCopy(path);

            XDocument? replaced = null;
            try
            {
                if (last.File.RawXml is not null) replaced = XDocument.Parse(last.File.RawXml);
            }
            catch (System.Xml.XmlException)
            {
                replaced = null;
            }

            if (replaced is not null && vanilla is not null && SameDocument(replaced, vanilla))
            {
                report.Notes.Add(Localization.SF("an3.identicalReplacement", last.Mod.Name, path));
                continue;
            }

            var lost = new List<(ModSnapshot Mod, ConfigPatch Patch)>();
            var kept = new List<string>();

            foreach (var (other, otherFile) in owners)
            {
                if (otherFile.FullReplacement) continue;

                // Patches applied after the replacement land on top of it and survive.
                if (other.Order > last.Mod.Order) continue;

                foreach (var patch in otherFile.Patches)
                {
                    if (patch.IsConditional) continue;

                    if (replaced is null) lost.Add((other, patch));
                    else if (ContainsEffect(replaced, patch)) kept.Add(other.Name);
                    else lost.Add((other, patch));
                }
            }

            if (lost.Count > 0)
            {
                conflicted.Add(path);

                var lostMods = lost.Select(l => l.Mod.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                Add(report, new ConflictEntry
                {
                    Kind = ConflictKind.SameConfigFile,
                    Severity = replaced is null ? FindingSeverity.Medium : FindingSeverity.High,
                    ModA = last.Mod.Name,
                    ModB = lostMods[0],
                    Subject = path,
                    Detail = replaced is null
                        ? Localization.SF("an2.replaceUnparsed", $"«{last.Mod.Name}»", path)
                        : Localization.SF("an2.replaceLost", $"«{last.Mod.Name}»", path, string.Join(", ", lostMods), lost.Count),
                    Confidence = replaced is null ? 0.5 : 0.9,
                    Participants = lost
                        .GroupBy(l => l.Mod.Name, StringComparer.OrdinalIgnoreCase)
                        .SelectMany(g => g.Select(l => new ConflictParticipant
                        {
                            Mod = l.Mod.Name,
                            File = path,
                            Operation = l.Patch.RawOperation,
                            XPath = l.Patch.XPath,
                            NewValue = l.Patch.Value,
                            Note = replaced is null
                                ? Localization.S("an3.noteNeedsCheck")
                                : Localization.S("an3.noteMissingInReplacer")
                        }))
                        .ToList()
                });
            }
            else if (kept.Count > 0)
            {
                report.Notes.Add(Localization.SF("an3.replacementKeeps",
                    path,
                    last.Mod.Name,
                    string.Join(", ", kept.Distinct(StringComparer.OrdinalIgnoreCase))));
            }
        }

        return conflicted;
    }

    private static bool ContainsEffect(XDocument document, ConfigPatch patch)
    {
        try
        {
            switch (patch.Operation)
            {
                case PatchOperation.Set:
                case PatchOperation.SetAttribute:
                {
                    var nodes = document.XPathSelectElements(patch.TargetPath).ToList();
                    if (nodes.Count == 0) return false;

                    return nodes.Any(node =>
                    {
                        var value = patch.Attribute is null ? node.Value.Trim() : node.Attribute(patch.Attribute)?.Value;
                        return string.Equals(value, patch.Value, StringComparison.Ordinal);
                    });
                }

                case PatchOperation.Remove:
                case PatchOperation.RemoveAttribute:
                {
                    var nodes = document.XPathSelectElements(patch.TargetPath).ToList();
                    if (nodes.Count == 0) return true;
                    if (patch.Attribute is null) return false;
                    return nodes.All(n => n.Attribute(patch.Attribute) is null);
                }

                case PatchOperation.Append:
                case PatchOperation.InsertAfter:
                case PatchOperation.InsertBefore:
                {
                    if (patch.AddedNodes.Count == 0) return false;

                    var fragment = ParseFragment(patch.InnerXml);

                    // Without the original XML only the name can be compared.
                    if (fragment is null)
                        return patch.AddedNodes.All(identity => NodeExists(document, identity));

                    foreach (var element in fragment)
                    {
                        var name = element.Attribute("name")?.Value ?? element.Attribute("id")?.Value;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var xpath = $"{patch.TargetPath}/{element.Name.LocalName}[@name='{name}']";
                        var found = document.XPathSelectElements(xpath).ToList();

                        if (found.Count == 0) return false;
                        if (!found.Any(node => SameElement(node, element))) return false;
                    }

                    return true;
                }

                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<XElement>? ParseFragment(string? innerXml)
    {
        if (string.IsNullOrWhiteSpace(innerXml)) return null;

        try
        {
            var wrapper = XElement.Parse($"<u7d2d-fragment>{innerXml}</u7d2d-fragment>");
            var elements = wrapper.Elements().ToList();
            return elements.Count > 0 ? elements : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Compares two elements by tag, attributes and text, ignoring formatting.</summary>
    private static bool SameElement(XElement left, XElement right) =>
        string.Equals(CanonicalElement(left), CanonicalElement(right), StringComparison.Ordinal);

    private static string CanonicalElement(XElement element) =>
        string.Concat(element.DescendantsAndSelf().Select(node =>
            node.Name.LocalName + "|" +
            string.Join(",", node.Attributes()
                .OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .Select(attribute => attribute.Name.LocalName + "=" + attribute.Value)) + "|" +
            (node.HasElements ? string.Empty : node.Value.Trim()) + "\n"));

    private static bool NodeExists(XDocument document, string identity)
    {
        try
        {
            return document.XPathSelectElements(identity).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Compares two config documents ignoring formatting, attribute order and comments.</summary>
    private static bool SameDocument(XDocument a, XDocument b) =>
        string.Equals(CanonicalElement(a.Root!), CanonicalElement(b.Root!), StringComparison.Ordinal);

    private static bool AddsLater(List<(string Mod, ConfigPatch Patch)> patches, int index)
    {
        var (mod, patch) = patches[index];

        for (var i = index + 1; i < patches.Count; i++)
        {
            var (otherMod, other) = patches[i];
            if (!otherMod.Equals(mod, StringComparison.OrdinalIgnoreCase)) continue;

            if (other.AddedNodes.Any(n => n.Equals(patch.TargetPath, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static bool IsRemoval(string operation) =>
        operation.Contains("remove", StringComparison.OrdinalIgnoreCase);

    private static FindingSeverity WriteSeverity(PatchStatus status) => status switch
    {
        PatchStatus.PathChanged => FindingSeverity.High,
        PatchStatus.AttributeMissing => FindingSeverity.High,
        PatchStatus.RemovedByEarlier => FindingSeverity.Medium,
        _ => FindingSeverity.Low
    };

    private static (string Reason, string Note) DescribePatchFailure(PatchStatus status) => status switch
    {
        PatchStatus.PathChanged => (Localization.S("an2.reasonPathChanged"), Localization.S("an2.notePathChanged")),
        PatchStatus.AttributeMissing => (Localization.S("an2.reasonAttr"), Localization.S("an2.noteAttr")),
        PatchStatus.RemovedByEarlier => (Localization.S("an2.reasonRemoved"), Localization.S("an2.noteRemoved")),
        _ => (Localization.S("an2.reasonOptional"), Localization.S("an2.noteOptional"))
    };

    private static void EmitPatchFinding(
        ConflictReport report,
        string file,
        string mod,
        List<PatchOutcome> items,
        FindingSeverity severity,
        string reason,
        string note)
    {
        if (items.Count == 0) return;

        Add(report, new ConflictEntry
        {
            Kind = ConflictKind.PatchTargetMissing,
            Severity = severity,
            ModA = mod,
            ModB = string.Empty,
            Subject = file,
            Detail = Localization.SF("an2.patchFinding", $"«{mod}»", reason, items.Count),
            Confidence = severity >= FindingSeverity.High ? 0.85 : 0.7,
            Participants = items.Select(f => new ConflictParticipant
            {
                Mod = f.Mod,
                File = file,
                Operation = f.Operation,
                XPath = f.XPath,
                Note = note
            }).ToList()
        });
    }

    /// <summary>Strips the last xpath step, ignoring slashes inside predicates.</summary>
    private static string? ParentPath(string path)
    {
        var depth = 0;

        for (var i = path.Length - 1; i >= 0; i--)
        {
            var c = path[i];
            if (c == ']') depth++;
            else if (c == '[') depth--;
            else if (c == '/' && depth == 0) return i > 0 ? path[..i] : null;
        }

        return null;
    }

    private static bool ExistsIn(XDocument document, string xpath)
    {
        try
        {
            return document.XPathSelectElements(xpath).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Compares what each mod does to the same node. Two mods editing different nodes of one file
    /// are not a conflict; two mods writing different values to one attribute are. Returns the set
    /// of files that produced node-level conflicts so file-level noise can be suppressed.
    /// </summary>
    private static HashSet<string> AnalyzeNodeConflicts(
        List<ModSnapshot> snapshots,
        VanillaConfigResolver? resolver,
        ConflictReport report)
    {
        var touchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: two mods writing to the same node attribute.
        var byTarget = new Dictionary<string, List<(ModSnapshot Mod, string File, ConfigPatch Patch)>>(StringComparer.OrdinalIgnoreCase);
        // Pass 2: one mod removing a node another mod edits, at element level so that removing
        // "/x/property[@name='D']" still meets a write to "/x/property[@name='D']/@value".
        var byElement = new Dictionary<string, List<(ModSnapshot Mod, string File, ConfigPatch Patch)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in snapshots)
        {
            foreach (var (file, patch) in snapshot.Patches)
            {
                if (!patch.WritesExistingNode && !patch.RemovesNode) continue;

                var key = $"{file}|{patch.TargetPath}|{patch.Attribute ?? string.Empty}";
                if (!byTarget.TryGetValue(key, out var list)) byTarget[key] = list = new();
                list.Add((snapshot, file, patch));

                var elementKey = $"{file}|{patch.TargetPath}";
                if (!byElement.TryGetValue(elementKey, out var elementList)) byElement[elementKey] = elementList = new();
                elementList.Add((snapshot, file, patch));
            }
        }

        foreach (var (_, entries) in byElement)
        {
            var removes = entries.Where(e => e.Patch.RemovesNode).ToList();
            var writes = entries.Where(e => e.Patch.WritesExistingNode).ToList();
            if (removes.Count == 0 || writes.Count == 0) continue;

            var removeMods = removes.Select(e => e.Mod.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (writes.All(w => removeMods.Contains(w.Mod.Name))) continue;

            var write = writes.First(w => !removeMods.Contains(w.Mod.Name));
            touchedFiles.Add(entries[0].File);

            Add(report, new ConflictEntry
            {
                Kind = ConflictKind.RemoveVsModify,
                Severity = FindingSeverity.High,
                ModA = removes[0].Mod.Name,
                ModB = write.Mod.Name,
                Subject = entries[0].Patch.TargetPath,
                Detail = write.Patch.Attribute is null
                    ? Localization.SF("an2.removeVsModify", removes[0].Mod.Name, write.Mod.Name)
                    : Localization.SF("an2.removeVsModifyAttr", removes[0].Mod.Name, write.Mod.Name, write.Patch.Attribute),
                Confidence = 0.85,
                Participants = BuildParticipants(entries, resolver)
            });
        }

        foreach (var (_, entries) in byElement)
        {
            var writes = entries.Where(e => e.Patch.WritesExistingNode).ToList();
            var writeMods = writes.Select(w => w.Mod.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (writeMods.Count < 2) continue;

            // A same-attribute collision with different values is a contradiction and is reported below;
            // editing different attributes of one element is an overlap where both changes survive.
            var hasValueConflict = writes
                .GroupBy(w => w.Patch.Attribute ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Select(w => w.Mod.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2 &&
                          g.Select(w => w.Patch.Value ?? string.Empty).Distinct(StringComparer.Ordinal).Count() > 1);
            if (hasValueConflict) continue;

            var attributes = writes.Select(w => w.Patch.Attribute).Where(a => a is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (attributes.Count < 2) continue;

            var file = entries[0].File;
            touchedFiles.Add(file);

            Add(report, new ConflictEntry
            {
                Kind = ConflictKind.SameNodeOverlap,
                Severity = FindingSeverity.Low,
                ModA = writeMods[0],
                ModB = writeMods[1],
                Subject = entries[0].Patch.TargetPath,
                Detail = Localization.SF("an2.overlap", string.Join(", ", attributes.Select(a => "@" + a))),
                Confidence = 0.9,
                Participants = BuildParticipants(entries, resolver)
            });
        }

        foreach (var (_, entries) in byTarget)
        {
            var mods = entries.Select(e => e.Mod.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (mods.Count < 2) continue;

            var writes = entries.Where(e => e.Patch.WritesExistingNode).ToList();
            if (writes.Count < 2) continue;

            var file = entries[0].File;
            var subject = DescribeTarget(entries[0].Patch.TargetPath, entries[0].Patch.Attribute);
            touchedFiles.Add(file);

            var values = writes.Select(w => w.Patch.Value ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count > 1)
            {
                var winner = writes.OrderByDescending(w => w.Mod.Order).First();
                var finalValue = winner.Patch.Value ?? string.Empty;

                Add(report, new ConflictEntry
                {
                    Kind = ConflictKind.ConflictingValue,
                    Severity = FindingSeverity.Medium,
                    ModA = writes[0].Mod.Name,
                    ModB = writes[1].Mod.Name,
                    Subject = subject,
                    Detail = Localization.SF("an2.conflictValue",
                        string.Join(" / ", values.Select(v => $"«{v}»")),
                        $"«{finalValue}»",
                        $"«{winner.Mod.Name}»"),
                    Confidence = 0.95,
                    Winner = winner.Mod.Name,
                    FinalValue = finalValue,
                    Participants = BuildParticipants(entries, resolver)
                });
            }
            else
            {
                Add(report, new ConflictEntry
                {
                    Kind = ConflictKind.RedundantWrite,
                    Severity = FindingSeverity.Low,
                    ModA = writes[0].Mod.Name,
                    ModB = writes[1].Mod.Name,
                    Subject = subject,
                    Detail = Localization.SF("an2.redundant", $"«{values[0]}»"),
                    Confidence = 0.9,
                    Participants = BuildParticipants(entries, resolver)
                });
            }
        }

        // Nodes added by append/insert with the same identity: the game ends up with two nodes of one name.
        var byAddedNode = new Dictionary<string, List<(ModSnapshot Mod, string File, ConfigPatch Patch)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var (file, patch) in snapshot.Patches)
            {
                foreach (var node in patch.AddedNodes)
                {
                    var key = $"{file}|{node}";
                    if (!byAddedNode.TryGetValue(key, out var list)) byAddedNode[key] = list = new();
                    list.Add((snapshot, file, patch));
                }
            }
        }

        foreach (var (key, entries) in byAddedNode)
        {
            var mods = entries.Select(e => e.Mod.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (mods.Count < 2) continue;

            var node = key[(key.IndexOf('|') + 1)..];
            touchedFiles.Add(entries[0].File);

            Add(report, new ConflictEntry
            {
                Kind = ConflictKind.DuplicateNode,
                Severity = FindingSeverity.High,
                ModA = mods[0],
                ModB = mods[1],
                Subject = node,
                Detail = Localization.SF("an2.dupNode", string.Join(", ", mods)),
                Confidence = 0.9,
                Participants = BuildParticipants(entries, resolver)
            });
        }

        return touchedFiles;
    }

    private static List<ConflictParticipant> BuildParticipants(
        List<(ModSnapshot Mod, string File, ConfigPatch Patch)> entries,
        VanillaConfigResolver? resolver)
    {
        var participants = new List<ConflictParticipant>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (mod, file, patch) in entries.OrderBy(e => e.Mod.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add($"{mod.Name}|{patch.XPath}|{patch.Value}")) continue;

            // An append has no "before" value and no single "after" value: it adds new nodes.
            var newValue = patch.AddsNode
                ? (patch.AddedNodes.Count > 0 ? string.Join(", ", patch.AddedNodes) : null)
                : patch.Value;

            var original = patch.AddsNode || patch.RemovesNode
                ? null
                : resolver?.Resolve(file, patch.TargetPath, patch.Attribute);

            participants.Add(new ConflictParticipant
            {
                Mod = mod.Name,
                File = file,
                Operation = patch.RawOperation,
                XPath = patch.XPath,
                NewValue = newValue,
                OriginalValue = original
            });
        }

        return participants;
    }

    private static string DescribeTarget(string target, string? attribute) =>
        attribute is null ? target : $"{target}/@{attribute}";

    public ConflictReport AnalyzePackage(ModPackage package, IReadOnlyList<InstalledMod> installed)
    {
        var report = new ConflictReport();
        if (installed.Count == 0)
        {
            report.Notes.Add(Localization.S("an3.noInstalledMods"));
            return report;
        }

        var packageConfigs = new HashSet<string>(package.Content.ConfigFiles, StringComparer.OrdinalIgnoreCase);
        var packageDlls = new HashSet<string>(package.Content.DllFiles.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in installed)
        {
            var snapshot = CreateSnapshot(mod);

            foreach (var path in packageConfigs)
            {
                if (!snapshot.Configs.TryGetValue(path, out var existing)) continue;

                Add(report, new ConflictEntry
                {
                    Kind = ConflictKind.SameConfigFile,
                    Severity = existing.FullReplacement ? FindingSeverity.High : FindingSeverity.Medium,
                    ModA = package.DisplayTitle,
                    ModB = mod.DisplayTitle,
                    Subject = path,
                    Detail = existing.FullReplacement
                        ? Localization.S("an3.installedFullReplacement")
                        : Localization.S("an3.bothModifyFile"),
                    Confidence = 0.7
                });
            }

            foreach (var dll in packageDlls)
            {
                if (!snapshot.DllNames.Contains(dll)) continue;

                Add(report, new ConflictEntry
                {
                    Kind = ConflictKind.DuplicateDll,
                    Severity = FindingSeverity.Medium,
                    ModA = package.DisplayTitle,
                    ModB = mod.DisplayTitle,
                    Subject = dll,
                    Detail = Localization.S("an3.duplicateDllName"),
                    Confidence = 0.75
                });
            }

            if (package.FolderName.Equals(mod.FolderName, StringComparison.OrdinalIgnoreCase))
            {
                Add(report, new ConflictEntry
                {
                    Kind = ConflictKind.DuplicateModFolder,
                    Severity = FindingSeverity.Critical,
                    ModA = package.DisplayTitle,
                    ModB = mod.DisplayTitle,
                    Subject = mod.FolderName,
                    Detail = Localization.S("an3.folderBusy"),
                    Confidence = 0.95
                });
            }

            if (package.Info.TechnicalName.Equals(mod.Info.TechnicalName, StringComparison.OrdinalIgnoreCase))
            {
                Add(report, new ConflictEntry
                {
                    Kind = ConflictKind.DuplicateModName,
                    Severity = FindingSeverity.Critical,
                    ModA = package.DisplayTitle,
                    ModB = mod.DisplayTitle,
                    Subject = package.Info.TechnicalName,
                    Detail = Localization.S("an3.technicalNameInstalled"),
                    Confidence = 0.9
                });
            }
        }

        report.Notes.Add(Localization.S("an3.structureOnly"));
        return report;
    }

    private static void Add(ConflictReport report, ConflictEntry entry)
    {
        if (report.Conflicts.Count >= MaxConflicts) return;

        // The same node can be reached from two files (XUi/windows.xml and XUi_InGame/windows.xml);
        // one row is enough, the file is shown per participant.
        var duplicate = report.Conflicts.Any(c =>
            c.Kind == entry.Kind && c.Subject == entry.Subject && c.ModA == entry.ModA &&
            c.ModB == entry.ModB && c.Severity == entry.Severity);
        if (duplicate) return;

        report.Conflicts.Add(entry);
    }

    private static ModSnapshot CreateSnapshot(InstalledMod mod)
    {
        var snapshot = new ModSnapshot
        {
            Name = mod.DisplayTitle,
            TechnicalName = mod.Info.TechnicalName,
            Folder = mod.FolderName,
            Dependencies = mod.Info.Dependencies.Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
        };

        foreach (var dll in mod.Content.DllFiles)
        {
            var name = Path.GetFileName(dll);
            snapshot.DllNames.Add(name);
            if (name.Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase)) snapshot.ShipsHarmony = true;

            var full = Path.IsPathRooted(dll) ? dll : Path.Combine(mod.FullPath, dll);
            ReadReferences(snapshot, full);
        }

        if (mod.Content.HasHarmonyFolder && Directory.Exists(Path.Combine(mod.FullPath, "Harmony")))
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(mod.FullPath, "Harmony"), "*.dll", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                snapshot.DllNames.Add(name);
                if (name.Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase)) snapshot.ShipsHarmony = true;
            }
        }

        var configRoot = Path.Combine(mod.FullPath, "Config");
        if (!Directory.Exists(configRoot)) return snapshot;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(configRoot, "*.xml", SearchOption.AllDirectories).Take(MaxFilesPerMod).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return snapshot;
        }

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes) continue;

                var relative = Path.GetRelativePath(mod.FullPath, file).Replace('\\', '/');
                var analysis = ConfigFileAnalysis.Parse(relative, File.ReadAllText(file));
                if (!analysis.Parsed) continue;

                snapshot.Configs[relative] = analysis;
                if (analysis.FullReplacement) snapshot.FullReplacementUsed = true;

                foreach (var patch in analysis.Patches)
                    snapshot.Patches.Add((relative, patch));

                foreach (var window in analysis.WindowNames) snapshot.WindowNames.Add(window);

                foreach (var target in analysis.XPathTargets)
                {
                    if (!snapshot.XPathTargets.TryGetValue(target, out var set))
                        snapshot.XPathTargets[target] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(relative);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Unreadable config files are skipped; the compatibility report covers parse errors.
            }
        }

        return snapshot;
    }
}
