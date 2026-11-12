using System.Xml.Linq;
using System.Xml.XPath;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Analysis;

public enum PatchStatus
{
    /// <summary>The patch found its target and changed something.</summary>
    Applied = 0,

    /// <summary>The xpath matched nothing: the patch does nothing and the mod's change is lost.</summary>
    TargetMissing = 1,

    /// <summary>The attribute the patch addresses is not present on the target node.</summary>
    AttributeMissing = 2,

    /// <summary>The node existed in the game but an earlier mod replaced or removed it.</summary>
    RemovedByEarlier = 4,

    /// <summary>The parent node exists but the addressed child does not: usually a version mismatch.</summary>
    PathChanged = 5,

    /// <summary>The target is absent entirely: usually an optional companion mod that is not installed.</summary>
    OptionalTarget = 6,

    /// <summary>Operation the simulator does not replay (csv, conditional, unknown).</summary>
    Unsupported = 3
}

/// <summary>Result of replaying one mod patch against the merged config.</summary>
public sealed class PatchOutcome
{
    public string File { get; init; } = string.Empty;
    public string Mod { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string XPath { get; init; } = string.Empty;
    public PatchStatus Status { get; set; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public int Matched { get; init; }
    public string? Note { get; init; }
}

public sealed class ConfigSimulation
{
    public string File { get; init; } = string.Empty;

    /// <summary>False when there is no vanilla file to replay against.</summary>
    public bool Replayed { get; init; }

    public List<PatchOutcome> Outcomes { get; } = new();

    public IEnumerable<PatchOutcome> Failures =>
        Outcomes.Where(o => o.Status is PatchStatus.TargetMissing or PatchStatus.AttributeMissing or PatchStatus.RemovedByEarlier);
}

/// <summary>
/// Replays mod patches on a copy of the game's own config, in load order, the way the game does it.
/// This answers the question a list of xpaths cannot: whether the patch actually finds its target.
/// Operations that cannot be replayed faithfully are reported as unsupported instead of guessed.
/// </summary>
public static class ConfigPatchSimulator
{
    public static ConfigSimulation Run(
        string file,
        XDocument? document,
        IEnumerable<(string Mod, ConfigPatch Patch)> patches)
    {
        if (document is null)
            return new ConfigSimulation { File = file, Replayed = false };

        var simulation = new ConfigSimulation { File = file, Replayed = true };

        foreach (var (mod, patch) in patches)
            simulation.Outcomes.Add(Apply(document, patch, mod, file));

        return simulation;
    }

    private static PatchOutcome Apply(XDocument document, ConfigPatch patch, string mod, string file)
    {
        if (patch.IsConditional)
            return new PatchOutcome
            {
                File = file, Mod = mod, Operation = patch.RawOperation, XPath = patch.XPath,
                Status = PatchStatus.Unsupported, Note = Localization.S("sim.conditional")
            };

        PatchOutcome Result(PatchStatus status, string? before = null, string? after = null, int matched = 0, string? note = null) =>
            new()
            {
                File = file,
                Mod = mod,
                Operation = patch.RawOperation,
                XPath = patch.XPath,
                Status = status,
                Before = before,
                After = after,
                Matched = matched,
                Note = note
            };

        try
        {
            switch (patch.Operation)
            {
                case PatchOperation.Set:
                case PatchOperation.SetAttribute:
                {
                    if (patch.Operation == PatchOperation.SetAttribute && patch.Attribute is null)
                        return Result(PatchStatus.Unsupported, note: Localization.S("sim.setAttributeNoName"));

                    var nodes = document.XPathSelectElements(patch.TargetPath).ToList();
                    if (nodes.Count == 0) return Result(PatchStatus.TargetMissing);

                    string? before = null;

                    foreach (var node in nodes)
                    {
                        if (patch.Attribute is null)
                        {
                            before ??= node.Value.Trim();
                            node.ReplaceNodes(new XText(patch.Value ?? string.Empty));
                        }
                        else
                        {
                            var attribute = node.Attribute(patch.Attribute);
                            before ??= attribute?.Value;
                            node.SetAttributeValue(patch.Attribute, patch.Value ?? string.Empty);
                        }
                    }

                    return Result(PatchStatus.Applied, before, patch.Value, nodes.Count);
                }

                case PatchOperation.Remove:
                {
                    var nodes = document.XPathSelectElements(patch.TargetPath).ToList();
                    if (nodes.Count == 0) return Result(PatchStatus.TargetMissing);

                    var before = nodes[0].Value.Trim();
                    foreach (var node in nodes) node.Remove();
                    return Result(PatchStatus.Applied, before, null, nodes.Count);
                }

                case PatchOperation.RemoveAttribute:
                {
                    if (patch.Attribute is null) return Result(PatchStatus.Unsupported, note: Localization.S("sim.removeAttributeNoName"));

                    var nodes = document.XPathSelectElements(patch.TargetPath).ToList();
                    if (nodes.Count == 0) return Result(PatchStatus.TargetMissing);

                    var withAttribute = nodes.Where(n => n.Attribute(patch.Attribute) is not null).ToList();
                    if (withAttribute.Count == 0) return Result(PatchStatus.AttributeMissing, matched: nodes.Count);

                    var before = withAttribute[0].Attribute(patch.Attribute)!.Value;
                    foreach (var node in withAttribute) node.Attribute(patch.Attribute)!.Remove();
                    return Result(PatchStatus.Applied, before, null, withAttribute.Count);
                }

                case PatchOperation.Append:
                {
                    var fragment = ParseFragment(patch.InnerXml);
                    if (fragment is null) return Result(PatchStatus.Unsupported, note: Localization.S("sim.noSourceXml"));

                    var parents = document.XPathSelectElements(patch.TargetPath).ToList();
                    if (parents.Count == 0) return Result(PatchStatus.TargetMissing);

                    foreach (var parent in parents)
                        parent.Add(fragment.Select(n => new XElement(n)));

                    return Result(PatchStatus.Applied, null, null, parents.Count);
                }

                case PatchOperation.InsertAfter:
                case PatchOperation.InsertBefore:
                {
                    var fragment = ParseFragment(patch.InnerXml);
                    if (fragment is null) return Result(PatchStatus.Unsupported, note: Localization.S("sim.noSourceXml"));

                    var nodes = document.XPathSelectElements(patch.TargetPath).ToList();
                    if (nodes.Count == 0) return Result(PatchStatus.TargetMissing);

                    foreach (var node in nodes)
                    {
                        var clones = fragment.Select(n => new XElement(n)).ToList();
                        if (patch.Operation == PatchOperation.InsertAfter)
                            node.AddAfterSelf(clones);
                        else
                            node.AddBeforeSelf(clones);
                    }

                    return Result(PatchStatus.Applied, null, null, nodes.Count);
                }

                default:
                    return Result(PatchStatus.Unsupported, note: Localization.S("sim.unsupportedOperation"));
            }
        }
        catch (Exception ex) when (ex is System.Xml.XPath.XPathException or System.Xml.XmlException or InvalidOperationException)
        {
            return Result(PatchStatus.Unsupported, note: ex.Message);
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
}
