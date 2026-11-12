using System.Text;
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
    /// </summary>
    public MergedPatchResult CreateMergedPatch(
        ConflictEntry conflict,
        ConflictResolution choice,
        string modsPath,
        string? gameVersion = null)
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
            var folderName = $"ZZZ_MergedPatch_{DateTime.Now:yyyyMMdd_HHmmss}";
            var folder = Path.Combine(modsPath, folderName);
            Directory.CreateDirectory(folder);

            var modInfo = $"""
                <?xml version="1.0" encoding="UTF-8" ?>
                <xml>
                  <Name value="{folderName}" />
                  <DisplayName value="Merged patch: {Escape(conflict.Subject)}" />
                  <Description value="{Localization.SF("res.patchDescription", Escape(choice.Value))}" />
                  <Author value="Universal 7 Days to Die Mod Manager" />
                  <Version value="1.0" />
                </xml>
                """;

            File.WriteAllText(Path.Combine(folder, "ModInfo.xml"), modInfo, new UTF8Encoding(false));

            var configPath = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            var root = Path.GetFileNameWithoutExtension(file);
            var content = $"""
                <?xml version="1.0" encoding="UTF-8" ?>
                <{root}>
                  <set xpath="{Escape(xpath)}">{Escape(choice.Value)}</set>
                </{root}>
                """;

            File.WriteAllText(configPath, content, new UTF8Encoding(false));

            return new MergedPatchResult { Success = true, FolderPath = folder };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MergedPatchResult { Error = ex.Message };
        }
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
