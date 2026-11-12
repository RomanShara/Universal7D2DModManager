using System.Xml.Linq;
using System.Xml.XPath;

namespace U7D2D.Core.Analysis;

/// <summary>
/// Resolves a value in the installed game's own config, so a conflict can show what a mod
/// changes from, not only what it changes to. Any xpath that cannot be evaluated yields null
/// instead of a guess: an unresolvable value is reported as unknown, not as empty.
/// </summary>
public sealed class VanillaConfigResolver
{
    private const long MaxBytes = 32L * 1024 * 1024;

    private readonly string? _configRoot;
    private readonly Dictionary<string, XDocument?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public VanillaConfigResolver(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return;

        var root = Path.Combine(gamePath, "Data", "Config");
        if (Directory.Exists(root)) _configRoot = root;
    }

    public bool IsAvailable => _configRoot is not null;

    public string? Resolve(string relativeFile, string targetPath, string? attribute)
    {
        var document = Load(relativeFile);
        if (document is null) return null;

        try
        {
            var node = document.XPathSelectElement(targetPath);
            if (node is null) return null;

            var value = attribute is null ? node.Value : node.Attribute(attribute)?.Value;
            return value?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Deep copy of the game config for replay, so the cached document is never mutated.</summary>
    public XDocument? CreateWorkingCopy(string relativeFile)
    {
        var document = Load(relativeFile);
        return document is null ? null : new XDocument(document);
    }

    private XDocument? Load(string relativeFile)
    {
        if (_configRoot is null) return null;
        if (_cache.TryGetValue(relativeFile, out var cached)) return cached;

        XDocument? document = null;
        try
        {
            var name = relativeFile.Replace('\\', '/');
            var index = name.IndexOf("Config/", StringComparison.OrdinalIgnoreCase);
            if (index >= 0) name = name[(index + "Config/".Length)..];

            var full = Path.Combine(_configRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full) && new FileInfo(full).Length <= MaxBytes)
            {
                using var stream = File.OpenRead(full);
                document = XDocument.Load(stream, LoadOptions.None);
            }
        }
        catch (Exception)
        {
            document = null;
        }

        _cache[relativeFile] = document;
        return document;
    }
}
