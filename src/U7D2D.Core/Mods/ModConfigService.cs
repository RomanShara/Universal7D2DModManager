using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using U7D2D.Core.I18n;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Mods;

/// <summary>
/// Reads and edits user-facing configuration files shipped inside a mod
/// (XML attributes/text nodes and JSON properties), with a backup before every write.
/// ModInfo.xml and schema descriptors such as Quartz ModSettings.xml are never offered for editing.
/// </summary>
public sealed class ModConfigService
{
    private const long MaxFileBytes = 4L * 1024 * 1024;
    private const int MaxFields = 600;

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModInfo.xml",
        "ModSettings.xml" // descriptor of available settings, not the user's values
    };

    private readonly AppPaths _paths;

    public ModConfigService(AppPaths paths) => _paths = paths;

    public IReadOnlyList<ModConfigFile> ListConfigs(InstalledMod mod)
    {
        var result = new List<ModConfigFile>();
        if (string.IsNullOrWhiteSpace(mod.FullPath) || !Directory.Exists(mod.FullPath)) return result;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(mod.FullPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var path in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (ExcludedFileNames.Contains(name)) continue;
            if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("CHANGELOG", StringComparison.OrdinalIgnoreCase)) continue;

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".xml" or ".json")) continue;

            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes) continue;

            if (extension == ".xml" && !IsEditableXml(path)) continue;
            if (extension == ".json" && !IsEditableJson(path)) continue;

            result.Add(new ModConfigFile
            {
                RelativePath = Path.GetRelativePath(mod.FullPath, path).Replace('\\', '/'),
                FullPath = path,
                Format = extension == ".xml" ? ConfigFormat.Xml : ConfigFormat.Json,
                SizeBytes = info.Length,
                ModifiedUtc = info.LastWriteTimeUtc
            });
        }

        return result;
    }

    private static bool IsEditableXml(string path)
    {
        try
        {
            var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null) return false;

            // A configuration file has at least one attribute or a child element with text.
            return root.DescendantsAndSelf().Any(e =>
                e.Attributes().Any(a => !a.IsNamespaceDeclaration) ||
                (!e.HasElements && !string.IsNullOrWhiteSpace(e.Value)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsEditableJson(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node is JsonObject;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ModConfigDocument Read(ModConfigFile file)
    {
        var document = new ModConfigDocument { File = file };

        try
        {
            if (!File.Exists(file.FullPath))
            {
                document.Error = Localization.S("cfg.fileNotFound");
                return document;
            }

            document.Fields = file.Format == ConfigFormat.Xml
                ? ReadXml(file.FullPath)
                : ReadJson(file.FullPath);

            if (document.Fields.Count == 0)
                document.Error = Localization.S("cfg.noEditableValues");

            if (File.GetAttributes(file.FullPath).HasFlag(FileAttributes.ReadOnly))
                document.Notice = Localization.S("cfg.readOnly");
        }
        catch (Exception ex)
        {
            document.Error = ex.Message;
        }

        return document;
    }

    // ------------------------------------------------------------------ XML

    private static List<ModConfigField> ReadXml(string path)
    {
        var fields = new List<ModConfigField>();
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root is null) return fields;

        foreach (var element in root.DescendantsAndSelf())
        {
            if (fields.Count >= MaxFields) break;

            var key = BuildXmlKey(element);
            var group = BuildXmlGroup(element);
            var hint = element.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals("Description", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("key")?.Value;

            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || !attribute.Name.NamespaceName.Equals(string.Empty)) continue;

                fields.Add(new ModConfigField
                {
                    Key = $"{key}@{attribute.Name.LocalName}",
                    Group = group,
                    Label = attribute.Name.LocalName,
                    Value = attribute.Value,
                    Kind = InferKind(attribute.Value),
                    Hint = hint
                });

                if (fields.Count >= MaxFields) break;
            }

            if (element.HasElements) continue;

            var text = element.Value.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            if (element.Attributes().Any(a => !a.IsNamespaceDeclaration)) continue;

            fields.Add(new ModConfigField
            {
                Key = $"{key}#text",
                Group = group,
                Label = element.Name.LocalName,
                Value = text,
                Kind = InferKind(text),
                Hint = hint
            });
        }

        return fields;
    }

    private static string BuildXmlKey(XElement element)
    {
        var parts = new List<string>();
        var current = element;

        while (current is not null)
        {
            var index = 0;
            if (current.Parent is not null)
            {
                foreach (var sibling in current.Parent.Elements(current.Name))
                {
                    if (ReferenceEquals(sibling, current)) break;
                    index++;
                }
            }

            var name = current.Name.LocalName;
            parts.Add(index == 0 ? name : $"{name}[{index}]");
            current = current.Parent;
        }

        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    private static string BuildXmlGroup(XElement element)
    {
        var names = new List<string>();
        var current = element;

        // Stop before the document root so "General" becomes its own group and
        // "Scanner / Category" keeps the parent context.
        while (current is not null && current.Parent is not null)
        {
            names.Add(current.Name.LocalName);
            current = current.Parent;
        }

        names.Reverse();
        return names.Count == 0 ? Localization.S("cfg.general") : string.Join(" / ", names);
    }

    private static XDocument LoadXmlForWrite(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static Encoding DetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> bom = stackalloc byte[3];
        var read = stream.Read(bom);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return new UTF8Encoding(false);
    }

    // ------------------------------------------------------------------ JSON

    private static List<ModConfigField> ReadJson(string path)
    {
        var fields = new List<ModConfigField>();
        var node = JsonNode.Parse(File.ReadAllText(path));
        if (node is null) return fields;

        WalkJson(node, "$", Localization.S("cfg.general"), (key, value, group) =>
        {
            if (fields.Count >= MaxFields) return;
            fields.Add(new ModConfigField
            {
                Key = key,
                Group = group,
                Label = key[(key.LastIndexOf('.') + 1)..],
                Value = value,
                Kind = InferKind(value)
            });
        });

        return fields;
    }

    private static void WalkJson(JsonNode? node, string path, string group, Action<string, string, string> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    var childPath = $"{path}.{pair.Key}";
                    var childGroup = path == "$" ? pair.Key : group;
                    WalkJson(pair.Value, childPath, childGroup, visit);
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    WalkJson(array[i], $"{path}[{i}]", group, visit);
                break;

            case JsonValue value:
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text)) visit(path, text, group);
                break;
        }
    }

    // ------------------------------------------------------------------ save

    public ConfigSaveResult Save(ModConfigFile file, string modFolder, IReadOnlyList<ConfigFieldChange> changes)
    {
        var result = new ConfigSaveResult();

        try
        {
            if (!File.Exists(file.FullPath))
            {
                result.Error = Localization.S("cfg.fileNotFound");
                return result;
            }

            if (changes.Count == 0)
            {
                result.Success = true;
                return result;
            }

            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (!TryNormalize(change.Kind, change.Value, out var value, out var error))
                {
                    result.Error = $"{change.Key}: {error}";
                    return result;
                }
                normalized[change.Key] = value;
            }

            var backup = CreateBackup(file, modFolder);
            result.BackupPath = backup;

            var changed = file.Format == ConfigFormat.Xml
                ? SaveXml(file, normalized, result)
                : SaveJson(file, normalized, result);

            result.Changed = changed;
            result.Success = result.Error is null;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    private static int SaveXml(ModConfigFile file, Dictionary<string, string> changes, ConfigSaveResult result)
    {
        var document = LoadXmlForWrite(file.FullPath);
        var root = document.Root;
        if (root is null)
        {
            result.Error = Localization.S("cfg.noRoot");
            return 0;
        }

        var applied = 0;
        var pending = new Dictionary<string, string>(changes, StringComparer.Ordinal);

        foreach (var element in root.DescendantsAndSelf())
        {
            var elementKey = BuildXmlKey(element);

            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration || !attribute.Name.NamespaceName.Equals(string.Empty)) continue;

                var key = $"{elementKey}@{attribute.Name.LocalName}";
                if (!pending.TryGetValue(key, out var value)) continue;

                attribute.Value = value;
                pending.Remove(key);
                applied++;
            }

            if (!element.HasElements && !element.Attributes().Any(a => !a.IsNamespaceDeclaration))
            {
                var textKey = $"{elementKey}#text";
                if (pending.TryGetValue(textKey, out var value))
                {
                    element.Value = value;
                    pending.Remove(textKey);
                    applied++;
                }
            }
        }

        foreach (var key in pending.Keys)
            result.Warnings.Add(Localization.SF("cfg.fieldNotFound", key));

        if (applied > 0)
            WriteXmlAtomically(file.FullPath, document);

        return applied;
    }

    private static void WriteXmlAtomically(string path, XDocument document)
    {
        var encoding = DetectEncoding(path);
        var temp = path + ".u7d2d.tmp";

        var settings = new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = false,
            Encoding = encoding,
            NewLineHandling = NewLineHandling.None
        };

        using (var writer = XmlWriter.Create(temp, settings))
            document.Save(writer);

        File.Move(temp, path, overwrite: true);
    }

    private static int SaveJson(ModConfigFile file, Dictionary<string, string> changes, ConfigSaveResult result)
    {
        var text = File.ReadAllText(file.FullPath);
        var node = JsonNode.Parse(text);
        if (node is null)
        {
            result.Error = Localization.S("cfg.noJsonObject");
            return 0;
        }

        var applied = 0;
        ApplyJson(node, "$", changes, ref applied);

        foreach (var key in changes.Keys)
            result.Warnings.Add(Localization.SF("cfg.fieldNotFound", key));

        if (applied > 0)
        {
            var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var temp = file.FullPath + ".u7d2d.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, file.FullPath, overwrite: true);
        }

        return applied;
    }

    private static void ApplyJson(JsonNode? node, string path, Dictionary<string, string> changes, ref int applied)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var childPath = $"{path}.{key}";
                    var child = obj[key];

                    if (child is JsonValue && changes.TryGetValue(childPath, out var value))
                    {
                        obj[key] = value;
                        changes.Remove(childPath);
                        applied++;
                        continue;
                    }

                    ApplyJson(child, childPath, changes, ref applied);
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var childPath = $"{path}[{i}]";
                    if (array[i] is JsonValue && changes.TryGetValue(childPath, out var value))
                    {
                        array[i] = value;
                        changes.Remove(childPath);
                        applied++;
                        continue;
                    }

                    ApplyJson(array[i], childPath, changes, ref applied);
                }
                break;
        }
    }

    private string? CreateBackup(ModConfigFile file, string modFolder)
    {
        try
        {
            var folder = Path.Combine(_paths.ConfigBackupDirectory, PathSafety.SanitizeFolderName(modFolder, "mod"));
            Directory.CreateDirectory(folder);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var name = $"{Path.GetFileNameWithoutExtension(file.FullPath)}-{stamp}{Path.GetExtension(file.FullPath)}";
            var target = Path.Combine(folder, name);

            var counter = 1;
            while (File.Exists(target))
            {
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(file.FullPath)}-{stamp}-{counter}{Path.GetExtension(file.FullPath)}");
                counter++;
            }

            File.Copy(file.FullPath, target, overwrite: false);
            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ConfigValueKind InferKind(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
            return ConfigValueKind.Bool;

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return ConfigValueKind.Number;

        return ConfigValueKind.Text;
    }

    private static bool TryNormalize(ConfigValueKind kind, string raw, out string value, out string? error)
    {
        value = raw.Trim();
        error = null;

        switch (kind)
        {
            case ConfigValueKind.Bool:
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
                {
                    value = "true";
                    return true;
                }
                if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
                {
                    value = "false";
                    return true;
                }
                error = Localization.S("cfg.expectBool");
                return false;

            case ConfigValueKind.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    error = Localization.S("cfg.expectNumber");
                    return false;
                }
                return true;

            default:
                return true;
        }
    }
}
