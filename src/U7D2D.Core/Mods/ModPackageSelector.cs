using U7D2D.Core.Models;
using U7D2D.Core.Nexus;

namespace U7D2D.Core.Mods;

/// <summary>
/// Decides which archive in the library corresponds to an installed mod.
///
/// A freshly downloaded Nexus file usually has a different filename and sometimes a
/// different inner folder, so matching by one field alone either loses the update or
/// installs it into a second folder next to the old one.
/// </summary>
public static class ModPackageSelector
{
    public static bool Matches(InstalledMod installed, ModPackage package)
    {
        var installedName = installed.Info.EffectiveName;
        var packageName = package.Info.EffectiveName;
        var namesKnown =
            !installedName.Equals("Unknown mod", StringComparison.OrdinalIgnoreCase) &&
            !packageName.Equals("Unknown mod", StringComparison.OrdinalIgnoreCase);

        return (namesKnown && installedName.Equals(packageName, StringComparison.OrdinalIgnoreCase)) ||
               installed.FolderName.Equals(package.FolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Folder that must be replaced. When the mod is already installed this is ALWAYS the
    /// existing folder, never the name guessed from the new archive.
    /// </summary>
    public static string EffectiveFolderName(ModPackage package, IEnumerable<InstalledMod> installed) =>
        installed.FirstOrDefault(i => Matches(i, package))?.FolderName ?? package.FolderName;

    /// <summary>
    /// Relation between the version inside an archive and the installed version.
    /// LeftNewer  = genuine update; RightNewer = the archive is OLDER (a downgrade);
    /// Inconclusive = the strings cannot be compared safely.
    /// </summary>
    public static VersionRelation Relation(ModPackage package, InstalledMod installed) =>
        ModVersionComparer.Compare(package.Info.Version, installed.Info.Version);

    /// <summary>True only for a real upgrade. A different string is not an update.</summary>
    public static bool IsUpdateAvailable(ModPackage package, InstalledMod installed) =>
        !string.IsNullOrWhiteSpace(package.Info.Version) &&
        !string.IsNullOrWhiteSpace(installed.Info.Version) &&
        Relation(package, installed) == VersionRelation.LeftNewer;

    public static bool IsDowngrade(ModPackage package, InstalledMod installed) =>
        !string.IsNullOrWhiteSpace(package.Info.Version) &&
        !string.IsNullOrWhiteSpace(installed.Info.Version) &&
        Relation(package, installed) == VersionRelation.RightNewer;

    /// <summary>
    /// Highest version wins; on equal/unparseable versions the most recently modified
    /// archive wins, so an updated download is never passed over in favour of the old one.
    /// </summary>
    public static ModPackage? PickNewest(IEnumerable<ModPackage> packages, InstalledMod installed)
    {
        return packages
            .Where(p => Matches(installed, p))
            .OrderBy(p => p, NewestFirst.Instance)
            .FirstOrDefault();
    }

    private sealed class NewestFirst : IComparer<ModPackage>
    {
        public static readonly NewestFirst Instance = new();

        public int Compare(ModPackage? x, ModPackage? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;

            var relation = ModVersionComparer.Compare(x.Info.Version, y.Info.Version);
            if (relation == VersionRelation.LeftNewer) return -1;
            if (relation == VersionRelation.RightNewer) return 1;

            return y.ArchiveModifiedUtc.CompareTo(x.ArchiveModifiedUtc);
        }
    }
}
