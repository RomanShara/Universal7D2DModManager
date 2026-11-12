using System.Text.RegularExpressions;
using U7D2D.Core.Models;

namespace U7D2D.Core.Nexus;

public sealed record NexusDetectedId(int ModId, string GameDomain, string Source);

/// <summary>
/// Recovers a Nexus mod id from data the manager already has. No network needed.
/// Priority: an explicit id in ModInfo.xml extras, the Website field, then the
/// filename of the archive the mod was installed from.
/// </summary>
public static partial class NexusModIdResolver
{
    public const string DefaultDomain = "7daystodie";

    [GeneratedRegex(@"nexusmods\.com/([A-Za-z0-9_\-]+)/mods/(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebsiteRegex();

    // Nexus download naming: Name-<modId>-<fileId>-<unix timestamp>.zip
    [GeneratedRegex(@"-(\d{2,7})-(\d{1,7})-(\d{9,11})(?:\.|_|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NexusDownloadRegex();

    // "Onsoku 12249 3.4 2026-11-10T03-48Z EjnRsi6xY.zip"
    [GeneratedRegex(@"(?:^|[\s_])(\d{3,7})(?=[\s_]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LooseIdRegex();

    // "SomeMod-13102-2.zip"
    [GeneratedRegex(@"-(\d{3,7})-(\d{1,7})(?:\.|_|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ShortPairRegex();

    public static NexusDetectedId? FromWebsite(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;
        var match = WebsiteRegex().Match(website);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[2].Value, out var modId) || !IsPlausible(modId)) return null;
        return new NexusDetectedId(modId, NormalizeDomain(match.Groups[1].Value), "website");
    }

    public static NexusDetectedId? FromArchiveName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var download = NexusDownloadRegex().Match(fileName);
        if (download.Success && int.TryParse(download.Groups[1].Value, out var id1) && IsPlausible(id1))
            return new NexusDetectedId(id1, DefaultDomain, "archive");

        var pair = ShortPairRegex().Match(fileName);
        if (pair.Success && int.TryParse(pair.Groups[1].Value, out var id2) && IsPlausible(id2))
            return new NexusDetectedId(id2, DefaultDomain, "archive");

        var loose = LooseIdRegex().Match(fileName);
        if (loose.Success && int.TryParse(loose.Groups[1].Value, out var id3) && IsPlausible(id3))
            return new NexusDetectedId(id3, DefaultDomain, "archive");

        return null;
    }

    public static NexusDetectedId? FromMetadata(ModMetadata info)
    {
        foreach (var key in new[] { "NexusId", "NexusModId", "NexusID", "ModId" })
        {
            if (!info.Extra.TryGetValue(key, out var raw)) continue;
            if (int.TryParse(raw?.Trim(), out var id) && IsPlausible(id))
                return new NexusDetectedId(id, DefaultDomain, "modinfo");
        }
        return null;
    }

    public static NexusDetectedId? Resolve(InstalledMod mod, string? additionalArchiveName = null)
    {
        var fromMeta = FromMetadata(mod.Info);
        if (fromMeta is not null) return fromMeta;

        var fromSite = FromWebsite(mod.Info.Website);
        if (fromSite is not null) return fromSite;

        var archive = !string.IsNullOrWhiteSpace(mod.SourceArchive)
            ? Path.GetFileName(mod.SourceArchive)
            : null;
        var fromArchive = FromArchiveName(archive);
        if (fromArchive is not null) return fromArchive;

        return FromArchiveName(additionalArchiveName);
    }

    /// <summary>
    /// Applies a manual override written by the player. Accepts either a plain number
    /// ("12194") or a full Nexus URL. Returns null when nothing valid was entered;
    /// an empty string means "remove the override".
    /// </summary>
    public static NexusLink? ParseManual(string folderName, string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return null;

        var site = FromWebsite(text);
        if (site is not null)
            return new NexusLink
            {
                FolderName = folderName, ModId = site.ModId, GameDomain = site.GameDomain,
                IsManual = true, Source = "manual"
            };

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var id) || !IsPlausible(id)) return null;

        return new NexusLink
        {
            FolderName = folderName, ModId = id, GameDomain = DefaultDomain,
            IsManual = true, Source = "manual"
        };
    }

    public static string NormalizeDomain(string? domain) =>
        string.IsNullOrWhiteSpace(domain) ? DefaultDomain : domain.Trim().ToLowerInvariant();

    private static bool IsPlausible(int id) => id >= 100 && id <= 9_999_999;
}
