using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Analysis;

/// <summary>One concrete way to resolve a conflict. Nothing is applied until the user picks one.</summary>
public sealed class ConflictResolution
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Value the merged patch would write.</summary>
    public string? Value { get; init; }

    /// <summary>Mod whose value is kept, when the option keeps an existing value.</summary>
    public string? Mod { get; init; }

    /// <summary>True when the option can be written as a patch file.</summary>
    public bool CanExport { get; init; }
}

public sealed class MergedPatchResult
{
    public bool Success { get; init; }
    public string? FolderPath { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// How far the written patch was checked: verified against the game config, or only for structure
    /// because the game config was not available.
    /// </summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Builds resolution options for a conflict and writes the chosen one as a separate patch mod.
/// Originals are never touched: the result is a new folder with its own ModInfo.xml.
/// </summary>
public sealed class ConflictResolutionService
{
    public IReadOnlyList<ConflictResolution> Suggest(ConflictEntry conflict)
    {
        var options = new List<ConflictResolution>();

        if (conflict.Kind != ConflictKind.ConflictingValue)
        {
            options.Add(new ConflictResolution
            {
                Title = Localization.S("res.keepAsIs"),
                Description = Localization.S("res.keepAsIsDescription"),
                CanExport = false
            });
            return options;
        }

        // One option per distinct value: several mods may write the same number, and offering
        // a separate button for each of them is noise, not choice.
        var originals = conflict.Participants
            .Where(p => p.OriginalValue is not null)
            .Select(p => p.OriginalValue!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var group in conflict.Participants
                     .Where(p => p.NewValue is not null)
                     .GroupBy(p => p.NewValue!, StringComparer.Ordinal))
        {
            var mods = group.Select(p => p.Mod).Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            options.Add(new ConflictResolution
            {
                Title = Localization.SF("res.keepValue", group.Key),
                Description = (mods.Count == 1
                                  ? Localization.SF("res.valueFromMod", mods[0])
                                  : Localization.SF("res.valueFromMods", string.Join(", ", mods))) +
                              (originals.Count == 1
                                  ? Localization.SF("res.valueCurrent", originals[0])
                                  : string.Empty),
                Value = group.Key,
                Mod = string.Join(", ", mods),
                CanExport = true
            });
        }

        foreach (var original in originals)
        {
            options.Add(new ConflictResolution
            {
                Title = Localization.SF("res.restoreValue", original),
                Description = Localization.S("res.restoreDescription"),
                Value = original,
                CanExport = true
            });
        }

        return options;
    }

    /// <summary>
    /// Writes the chosen value as a new mod folder so it applies after the conflicting mods.
    /// The folder is created only here, on explicit request, and never overwrites an existing one.
    /// When the game path is known the patch is replayed on a copy of the game config, and a patch
    /// that cannot find its target there is removed again instead of being left as a dead mod.
    /// </summary>
    public MergedPatchResult CreateMergedPatch(
        ConflictEntry conflict,
        ConflictResolution choice,
        string modsPath,
        string? gameVersion = null,
        string? gamePath = null)
    {
        if (!choice.CanExport || choice.Value is null)
            return new MergedPatchResult { Error = Localization.S("res.cannotExport") };

        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
            return new MergedPatchResult { Error = Localization.S("res.modsNotFound") };

        var file = conflict.Participants.FirstOrDefault()?.File;
        var xpath = conflict.Participants.FirstOrDefault()?.XPath;

        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(xpath))
            return new MergedPatchResult { Error = Localization.S("res.noFileData") };

        try
        {
            var folder = CreatePatchFolder(modsPath);
            var description = Localization.SF("res.patchDescription", choice.Value);
            var displayName = $"Merged patch: {conflict.Subject}";

            // Modern layout only: <ModInfo> with value attributes, which ModInfoParser reads as
            // ModInfoFormat.ValueAttribute. Text nodes inside the fields would make it Mixed.
            var modInfo = $"""
                <?xml version="1.0" encoding="UTF-8" ?>
                <ModInfo>
                  <Name value="{Escape(Path.GetFileName(folder))}" />
                  <DisplayName value="{Escape(displayName)}" />
                  <Description value="{Escape(description)}" />
                  <Author value="Universal 7 Days to Die Mod Manager" />
                  <Version value="1.0" />
                </ModInfo>
                """;

            File.WriteAllText(Path.Combine(folder, "ModInfo.xml"), modInfo, new UTF8Encoding(false));

            var configPath = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            // The game applies xpath commands only inside <configs>. Any other root is read as a
            // replacement of the whole config file and would drop every other mod's change.
            var content = $"""
                <?xml version="1.0" encoding="UTF-8" ?>
                <configs>
                  <set xpath="{Escape(xpath)}">{Escape(choice.Value)}</set>
                </configs>
                """;

            File.WriteAllText(configPath, content, new UTF8Encoding(false));

            var invalid = ValidatePatchFile(configPath);
            if (invalid is not null)
            {
                RemoveFolder(folder);
                return new MergedPatchResult { Error = Localization.SF("res.patchInvalid", invalid) };
            }

            var result = new MergedPatchResult { Success = true, FolderPath = folder };

            var resolver = new VanillaConfigResolver(gamePath);
            var workingCopy = resolver.IsAvailable ? resolver.CreateWorkingCopy(file) : null;

            if (workingCopy is null)
            {
                result.Warnings.Add(Localization.S("res.patchUnverified"));
                return result;
            }

            if (!FindsTarget(workingCopy, xpath))
            {
                // A patch whose xpath finds nothing does nothing in game, so it is not left behind.
                RemoveFolder(folder);
                return new MergedPatchResult { Error = Localization.SF("res.patchDoesNotApply", xpath) };
            }

            result.Warnings.Add(Localization.S("res.patchVerified"));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MergedPatchResult { Error = ex.Message };
        }
    }

    /// <summary>
    /// Creates a patch folder that cannot collide with an earlier one: two patches made in the same
    /// second get _2, _3 and so on instead of sharing a folder.
    /// </summary>
    private static string CreatePatchFolder(string modsPath)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var folder = Path.Combine(modsPath, $"ZZZ_MergedPatch_{stamp}");

        for (var suffix = 2; Path.Exists(folder); suffix++)
            folder = Path.Combine(modsPath, $"ZZZ_MergedPatch_{stamp}_{suffix}");

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Re-reads the written file and checks what the game depends on: the root has to be configs,
    /// there has to be exactly one set command with an xpath, and that xpath has to compile.
    /// Returns null when the file is usable, otherwise the technical reason.
    /// </summary>
    private static string? ValidatePatchFile(string path)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            };

            XDocument document;
            using (var reader = XmlReader.Create(path, settings))
                document = XDocument.Load(reader);

            var root = document.Root;
            if (root is null || !root.Name.LocalName.Equals("configs", StringComparison.OrdinalIgnoreCase))
                return $"root element is <{root?.Name.LocalName}>, expected <configs>";

            var commands = root.Elements()
                .Where(e => e.Name.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (commands.Count != 1)
                return $"expected one <set> command, found {commands.Count}";

            var xpath = commands[0].Attribute("xpath")?.Value;
            if (string.IsNullOrWhiteSpace(xpath))
                return "the <set> command carries no xpath";

            XPathExpression.Compile(xpath);
            return null;
        }
        catch (Exception ex) when (ex is XmlException or XPathException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// True when the xpath finds its target in the given config copy. The attribute step is a node
    /// of the XPath tree, so selecting the attribute itself checks both that the element matches and
    /// that the attribute exists: selecting a missing attribute yields an empty set, not an error,
    /// and would otherwise pass as a working patch.
    /// </summary>
    private static bool FindsTarget(XDocument document, string xpath)
    {
        try
        {
            var navigator = document.CreateNavigator();
            if (navigator is null) return false;

            var matches = navigator.Select(XPathExpression.Compile(xpath));
            return matches.MoveNext();
        }
        catch (Exception ex) when (ex is XPathException or XmlException)
        {
            return false;
        }
    }

    /// <summary>Removes a folder created by this method, so a patch that cannot work is never left behind.</summary>
    private static void RemoveFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder stays on disk; the caller still reports why the patch was rejected.
        }
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
