using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Analysis;

/// <summary>Structural facts about a single XML config file shipped by a mod.</summary>
public sealed class ConfigFileAnalysis
{
    private static readonly Regex WindowNameRegex = new(@"@name\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex AttributeStepRegex = new(@"^(.*)/@([A-Za-z_][A-Za-z0-9_.-]*)$", RegexOptions.Compiled);
    private static readonly Regex SpaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Tags whose entries are identified by name and must stay unique. A repeated "drop" or
    /// "ingredient" is normal in 7 Days to Die, but a repeated item, block or window is a real clash,
    /// so only these collections take part in duplicate-node detection.
    /// </summary>
    private static readonly HashSet<string> UniqueByName = new(StringComparer.OrdinalIgnoreCase)
    {
        "item", "block", "buff", "recipe", "entity_class", "perk", "progression",
        "quest", "vehicle", "window", "window_group", "simplebutton", "property"
    };

    public string RelativePath { get; init; } = string.Empty;
    public bool Parsed { get; init; }
    public string? Error { get; init; }
    public bool UsesXPath { get; init; }
    public int XPathOperations { get; init; }
    public List<string> XPathTargets { get; init; } = new();
    public List<string> WindowNames { get; init; } = new();
    public bool FullReplacement { get; init; }

    /// <summary>Raw content of a wholesale replacement file, kept to compare it with the game file.</summary>
    public string? RawXml { get; init; }
    public string? RootElement { get; init; }
    public int ElementCount { get; init; }

    /// <summary>Every patch in the file reduced to node, operation and value.</summary>
    public List<ConfigPatch> Patches { get; init; } = new();

    /// <summary>
    /// Turns one patch element into a comparable record. The node identity is the xpath without
    /// the trailing attribute step, so "/items/item[@name='A']/property[@name='B']/@value"
    /// and a setattribute on the same element both land on the same node.
    /// </summary>
    private static void CollectPatches(XElement element, string xpath, List<ConfigPatch> patches)
    {
        var rawOperation = element.Name.LocalName;
        var operation = MapOperation(rawOperation);

        var targetPath = xpath;
        string? attribute = null;

        if (operation == PatchOperation.SetAttribute)
        {
            attribute = element.Attribute("name")?.Value;
        }
        else
        {
            var match = AttributeStepRegex.Match(xpath);
            if (match.Success)
            {
                targetPath = match.Groups[1].Value;
                attribute = match.Groups[2].Value;
            }
        }

        var value = operation is PatchOperation.Set or PatchOperation.SetAttribute
            ? element.Value.Trim()
            : null;

        var added = new List<string>();
        if (operation is PatchOperation.Append or PatchOperation.InsertAfter or PatchOperation.InsertBefore)
        {
            foreach (var child in element.Elements())
            {
                // Only nodes with a name can be identified. Nameless elements are appended by many
                // mods independently and must not be treated as duplicates.
                var identity = NodeIdentity(targetPath, child);
                if (identity is not null) added.Add(identity);
            }
        }

        patches.Add(new ConfigPatch
        {
            Operation = operation,
            RawOperation = rawOperation,
            XPath = xpath,
            TargetPath = targetPath,
            Attribute = attribute,
            Value = value,
            AddedNodes = added,
            InnerXml = operation is PatchOperation.Append or PatchOperation.InsertAfter or PatchOperation.InsertBefore
                ? BuildInnerXml(element)
                : null,
            IsConditional = element.Ancestors().Any(a => a.Name.LocalName.Equals("conditional", StringComparison.OrdinalIgnoreCase))
        });
    }

    /// <summary>Keeps the added nodes so the patch can be replayed, with a size guard.</summary>
    private static string? BuildInnerXml(XElement element)
    {
        var xml = string.Concat(element.Nodes().Select(n => n.ToString()));
        return xml.Length is > 0 and <= 4_000_000 ? xml : null;
    }

    private static string? NodeIdentity(string parent, XElement node)
    {
        var tag = node.Name.LocalName;
        if (!UniqueByName.Contains(tag)) return null;

        var name = node.Attribute("name")?.Value ?? node.Attribute("id")?.Value;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return $"{parent}/{tag}[@name='{name}']";
    }

    private static PatchOperation MapOperation(string raw) => raw.ToLowerInvariant() switch
    {
        "append" => PatchOperation.Append,
        "set" => PatchOperation.Set,
        "setattribute" => PatchOperation.SetAttribute,
        "remove" => PatchOperation.Remove,
        "removeattribute" => PatchOperation.RemoveAttribute,
        "insertafter" => PatchOperation.InsertAfter,
        "insertbefore" => PatchOperation.InsertBefore,
        "replace" => PatchOperation.Replace,
        _ => PatchOperation.Unknown
    };

    private static string NormalizeXPath(string raw) => SpaceRegex.Replace(raw.Trim(), " ").Replace('"', '\'');

    public static ConfigFileAnalysis Parse(string relativePath, string xml)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true
            };

            XDocument document;
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
                document = XDocument.Load(reader);

            var root = document.Root;
            if (root is null)
                return new ConfigFileAnalysis { RelativePath = relativePath, Parsed = false, Error = Localization.S("cf.emptyXml") };

            var xpathOperations = 0;
            var targets = new List<string>();
            var windows = new List<string>();
            var patches = new List<ConfigPatch>();

            foreach (var element in document.Descendants())
            {
                var xpath = element.Attribute("xpath")?.Value;
                if (!string.IsNullOrWhiteSpace(xpath))
                {
                    xpathOperations++;
                    foreach (Match match in WindowNameRegex.Matches(xpath))
                    {
                        var name = match.Groups[1].Value;
                        if (!targets.Contains(name, StringComparer.OrdinalIgnoreCase)) targets.Add(name);
                    }

                    CollectPatches(element, NormalizeXPath(xpath), patches);
                }

                // A <window name="X"/> inside a <window_group> is a reference to a window defined
                // elsewhere, not a definition. Everything else that names a window defines it,
                // including windows appended through <append xpath="/windows">.
                if (element.Name.LocalName.Equals("window", StringComparison.OrdinalIgnoreCase) &&
                    element.Parent?.Name.LocalName.Equals("window_group", StringComparison.OrdinalIgnoreCase) != true)
                {
                    var name = element.Attribute("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name) && !windows.Contains(name, StringComparer.OrdinalIgnoreCase))
                        windows.Add(name);
                }
            }

            return new ConfigFileAnalysis
            {
                RelativePath = relativePath,
                Parsed = true,
                UsesXPath = xpathOperations > 0,
                XPathOperations = xpathOperations,
                XPathTargets = targets,
                WindowNames = windows,
                // A <configs> file is a patch container: with no patch elements it simply does nothing.
                // Only a file whose root is the target document itself replaces the file.
                FullReplacement = !root.Name.LocalName.Equals("configs", StringComparison.OrdinalIgnoreCase) &&
                                  xpathOperations == 0 && root.HasElements,
                RawXml = !root.Name.LocalName.Equals("configs", StringComparison.OrdinalIgnoreCase) &&
                         xpathOperations == 0 && root.HasElements && xml.Length <= 4_000_000
                    ? xml
                    : null,
                RootElement = root.Name.LocalName,
                ElementCount = document.Descendants().Count(),
                Patches = patches
            };
        }
        catch (Exception ex)
        {
            return new ConfigFileAnalysis
            {
                RelativePath = relativePath,
                Parsed = false,
                Error = ex.Message
            };
        }
    }
}
