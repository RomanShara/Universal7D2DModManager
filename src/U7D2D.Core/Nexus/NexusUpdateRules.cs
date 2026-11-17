using System.Collections.Generic;
using System.Linq;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Nexus;

/// <summary>Outcome of comparing one installed mod with the Nexus page and its files.</summary>
public sealed record NexusUpdateVerdict(
    NexusCheckStatus Status,
    string AvailableVersion,
    string? LibraryVersion);

/// <summary>
/// Decides whether a newer build really exists. The Nexus page version is not a mod version on many
/// 7 Days to Die pages: authors put the game version there ("3.3", "3.4", "V3.1.0"), while ModInfo.xml
/// keeps a semantic version ("1.0.2.0"). Comparing those numbers produces updates that do not exist.
///
/// Rules, in order:
/// 1. the file version is the downloadable build; without files there is no verdict;
/// 2. a build equal to the installed one means up to date;
/// 3. a version already present in the archive library means the player downloaded it;
/// 4. a page version newer than its own file, or a different major number, means the numbering is
///    not comparable; the card asks to check the page instead of claiming an update.
/// </summary>
public static class NexusUpdateRules
{
    /// <summary>Bumped when the decision changes; older cache entries are recalculated on load.</summary>
    public const int Version = 1;

    public static NexusUpdateVerdict Decide(
        string? localVersion,
        string? pageVersion,
        string? fileVersion,
        IReadOnlyCollection<string> libraryVersions)
    {
        var local = localVersion?.Trim() ?? string.Empty;
        var page = pageVersion?.Trim() ?? string.Empty;
        var file = fileVersion?.Trim() ?? string.Empty;

        if (local.Length == 0)
            return new(NexusCheckStatus.UncomparableVersion, file, null);

        // No file list: the page version alone is not trustworthy.
        if (file.Length == 0)
            return new(NexusCheckStatus.NoFiles, page, null);

        if (IsEqual(file, local) || (page.Length > 0 && IsEqual(page, local)))
            return new(NexusCheckStatus.UpToDate, file, null);

        foreach (var candidate in new[] { file, page })
        {
            if (candidate.Length == 0) continue;
            var match = libraryVersions.FirstOrDefault(v => IsEqual(v, candidate));
            if (match is not null)
                return new(NexusCheckStatus.UpToDate, file, match);
        }

        var relation = ModVersionComparer.Compare(file, local);
        if (relation is VersionRelation.Equal or VersionRelation.RightNewer)
            return new(NexusCheckStatus.UpToDate, file, null);
        if (relation == VersionRelation.Inconclusive)
            return new(NexusCheckStatus.UncomparableVersion, file, null);

        // The file is newer, but the page claims an even newer version: the two fields live in
        // different scales (page version is often the game version).
        if (page.Length > 0 && ModVersionComparer.Compare(page, file) == VersionRelation.LeftNewer)
            return new(NexusCheckStatus.UncomparableVersion, file, null);

        // 1.0 installed and 5.0 on Nexus is either a numbering jump or a game version. Claiming an
        // update here is wrong more often than right, so the card asks to check the page.
        if (TryMajor(file, out var fileMajor) && TryMajor(local, out var localMajor) &&
            fileMajor != localMajor)
            return new(NexusCheckStatus.UncomparableVersion, file, null);

        return new(NexusCheckStatus.UpdateAvailable, file, null);
    }

    /// <summary>Localized text for the verdict; rebuilt on every language switch.</summary>
    public static string Describe(NexusModUpdate item)
    {
        return item.Status switch
        {
            NexusCheckStatus.NotLinked => Localization.S("nx.idNotFoundMessage"),
            NexusCheckStatus.UpdateAvailable => Localization.SF(
                "nx.updateAvailable", item.RemoteVersion, item.LocalVersion),
            NexusCheckStatus.UpToDate => item.LibraryVersion is { Length: > 0 } library
                ? Localization.SF("nx2.alreadyInLibrary", library)
                : Localization.S("nx.upToDate"),
            NexusCheckStatus.UncomparableVersion => string.IsNullOrWhiteSpace(item.LocalVersion)
                ? Localization.S("nx2.noLocalVersion")
                : Localization.SF("nx2.checkManually", item.RemoteVersion, item.LocalVersion),
            NexusCheckStatus.NoFiles => Localization.S("nx2.filesUnavailable"),
            _ => item.Message
        };
    }

    private static bool IsEqual(string left, string right) =>
        ModVersionComparer.Compare(left, right) == VersionRelation.Equal;

    private static bool TryMajor(string version, out int major)
    {
        major = 0;
        var text = version.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text[1..];

        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out major);
    }
}
