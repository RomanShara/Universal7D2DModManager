using System.Text;
using System.Xml;
using System.Xml.Linq;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Mods;

/// <summary>
/// Parses 7 Days to Die ModInfo.xml in both historical layouts:
/// legacy &lt;xml&gt;&lt;ModInfo&gt;&lt;Name&gt;text&lt;/Name&gt; and modern &lt;ModInfo&gt;&lt;Name value="..."/&gt;.
/// </summary>
public static class ModInfoParser
{
    private static readonly string[] DependencyContainers =
        ["dependencies", "requires", "requirements", "moddependencies", "modrequirements", "dependson"];

    private static readonly string[] DependencyItems =
        ["dependency", "mod", "require", "requiredmod", "moddependency", "dependson"];

    public static ModMetadata Parse(byte[] bytes)
    {
        var text = DecodeText(bytes);
        return Parse(text);
    }

    public static ModMetadata Parse(string xmlText)
    {
        if (string.IsNullOrWhiteSpace(xmlText))
            throw new FormatException(Localization.S("mod.modInfoEmpty"));

        var document = LoadDocument(xmlText);
        var modInfo = FindModInfoElement(document)
                      ?? throw new FormatException(Localization.S("mod.modInfoNoElement"));

        var metadata = new ModMetadata();
        var valueAttributeHits = 0;
        var textHits = 0;

        foreach (var element in modInfo.Elements())
        {
            var key = element.Name.LocalName;
            var hasValueAttribute = element.Attribute("value") is not null;
            if (hasValueAttribute) valueAttributeHits++;
            else if (!string.IsNullOrWhiteSpace(element.Value)) textHits++;

            var value = ReadValue(element) ?? string.Empty;

            switch (key.ToLowerInvariant())
            {
                case "name":
                    metadata.Name = value;
                    break;
                case "displayname":
                    metadata.DisplayName = value;
                    break;
                case "description":
                    metadata.Description = value;
                    break;
                case "author":
                    metadata.Author = value;
                    break;
                case "version":
                    metadata.Version = value;
                    break;
                case "website":
                    metadata.Website = value;
                    break;
                case "skipwithanticheat":
                    metadata.SkipWithAntiCheat = ParseBool(value);
                    break;
                case "gameversion":
                case "compatibleversion":
                case "targetversion":
                case "gameversionmin":
                case "mingameversion":
                case "supportedversion":
                    if (!string.IsNullOrWhiteSpace(value))
                        metadata.DeclaredGameVersion ??= value;
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(value) && metadata.Extra.Count < 40)
                        metadata.Extra[key] = value;
                    break;
            }
        }

        ParseDependencies(modInfo, metadata);

        metadata.Format = valueAttributeHits > 0 && textHits > 0 ? ModInfoFormat.Mixed
            : valueAttributeHits > 0 ? ModInfoFormat.ValueAttribute
            : ModInfoFormat.LegacyText;

        if (string.IsNullOrWhiteSpace(metadata.Name) && string.IsNullOrWhiteSpace(metadata.DisplayName))
            throw new FormatException(Localization.S("mod.modInfoNoName"));

        if (string.IsNullOrWhiteSpace(metadata.DisplayName))
            metadata.DisplayName = metadata.Name;

        return metadata;
    }

    public static bool TryParse(byte[] bytes, out ModMetadata metadata, out string? error)
    {
        try
        {
            metadata = Parse(bytes);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            metadata = new ModMetadata();
            error = ex.Message;
            return false;
        }
    }

    private static void ParseDependencies(XElement modInfo, ModMetadata metadata)
    {
        foreach (var container in modInfo.Elements())
        {
            var containerName = container.Name.LocalName.ToLowerInvariant();
            if (!DependencyContainers.Contains(containerName)) continue;

            foreach (var item in container.Elements())
            {
                var itemName = item.Name.LocalName.ToLowerInvariant();
                if (!DependencyItems.Contains(itemName)) continue;

                var name = ReadAttribute(item, "name", "mod", "id", "modname", "target") ?? ReadValue(item);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var version = ReadAttribute(item, "version", "minversion", "versionmin", "requiresversion");
                var optional = ParseBool(ReadAttribute(item, "optional") ?? string.Empty);
                if (!optional)
                {
                    var required = ReadAttribute(item, "required");
                    if (required is not null && !ParseBool(required)) optional = true;
                }

                metadata.Dependencies.Add(new ModDependency
                {
                    Name = name.Trim(),
                    Version = version,
                    Optional = optional,
                    Source = "ModInfo.xml"
                });
            }
        }
    }

    private static XDocument LoadDocument(string xmlText)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = false
        };

        using var reader = XmlReader.Create(new StringReader(xmlText), settings);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// Locates the element that holds mod fields. Three layouts exist in the wild:
    ///   &lt;ModInfo&gt;&lt;Name value="..."/&gt;&lt;/ModInfo&gt;      (modern)
    ///   &lt;xml&gt;&lt;ModInfo&gt;&lt;Name&gt;text&lt;/Name&gt;&lt;/ModInfo&gt;&lt;/xml&gt; (legacy)
    ///   &lt;xml&gt;&lt;Name value="..."/&gt;&lt;/xml&gt;                 (7 Days to Die V1.0+, no ModInfo element at all)
    /// </summary>
    private static XElement? FindModInfoElement(XDocument document)
    {
        var root = document.Root;
        if (root is null) return null;

        if (root.Name.LocalName.Equals("ModInfo", StringComparison.OrdinalIgnoreCase)) return root;
        if (HasNameChild(root)) return root;

        var modInfo = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("ModInfo", StringComparison.OrdinalIgnoreCase));
        if (modInfo is not null) return modInfo;

        // Last resort: any element that directly contains <Name>.
        return root.Descendants().FirstOrDefault(HasNameChild);
    }

    private static bool HasNameChild(XElement element) =>
        element.Elements().Any(e => e.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase));

    private static string? ReadValue(XElement element)
    {
        var attribute = element.Attribute("value");
        if (attribute is not null) return attribute.Value.Trim();

        var text = element.Value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ReadAttribute(XElement element, params string[] names)
    {
        foreach (var attribute in element.Attributes())
        {
            if (names.Any(n => n.Equals(attribute.Name.LocalName, StringComparison.OrdinalIgnoreCase)))
            {
                var value = attribute.Value.Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    private static bool ParseBool(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }
}
