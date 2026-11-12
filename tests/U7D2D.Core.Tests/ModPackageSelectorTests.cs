using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

public sealed class ModPackageSelectorTests
{
    private static InstalledMod Installed(string folder = "CoolMod", string version = "3.4") => new()
    {
        FolderName = folder,
        Info = new ModMetadata { Name = "Cool Mod", DisplayName = "Cool Mod", Version = version }
    };

    private static ModPackage Package(
        string version, string folder, DateTime modifiedUtc) => new()
    {
        FolderName = folder,
        ArchiveFileName = $"CoolMod-{version}.zip",
        ArchiveModifiedUtc = modifiedUtc,
        Info = new ModMetadata { Name = "Cool Mod", DisplayName = "Cool Mod", Version = version }
    };

    [Fact]
    public void New_archive_wins_even_when_the_old_one_is_listed_first()
    {
        var old = Package("3.4", "CoolMod", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var fresh = Package("3.5", "CoolMod-13102-3-5-1700000000", new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc));

        var picked = ModPackageSelector.PickNewest(new[] { old, fresh }, Installed());

        Assert.Same(fresh, picked);
    }

    [Fact]
    public void Equal_or_unparseable_versions_fall_back_to_the_newest_download()
    {
        var old = Package("Alpha 16", "CoolMod", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var fresh = Package("Alpha 17", "CoolMod-13102", new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc));

        var picked = ModPackageSelector.PickNewest(new[] { old, fresh }, Installed(version: "Alpha 16"));

        Assert.Same(fresh, picked);
    }

    [Fact]
    public void Update_always_targets_the_installed_folder_not_the_archive_name()
    {
        var installed = Installed(folder: "CoolMod");
        var fresh = Package("3.5", "CoolMod-13102-3-5-1700000000", DateTime.UtcNow);

        Assert.Equal("CoolMod",
            ModPackageSelector.EffectiveFolderName(fresh, new[] { installed }));
    }

    [Fact]
    public void Archive_without_a_matching_install_keeps_its_own_folder_name()
    {
        var fresh = Package("3.5", "CoolMod-13102", DateTime.UtcNow);

        Assert.Equal("CoolMod-13102",
            ModPackageSelector.EffectiveFolderName(fresh, Array.Empty<InstalledMod>()));
    }

    [Fact]
    public void ModInfo_name_matches_even_when_the_inner_folder_changed()
    {
        var installed = Installed(folder: "CoolMod");
        var fresh = Package("3.5", "CompletelyDifferentRoot", DateTime.UtcNow);

        Assert.True(ModPackageSelector.Matches(installed, fresh));
    }

    [Theory]
    [InlineData("3.4.0", "3.3.3.3", true, false)]
    [InlineData("3.1.3.2", "3.3.3.3", false, true)]
    [InlineData("3.3.3.3", "3.3.3.3", false, false)]
    [InlineData("Alpha 17", "Alpha 16", false, false)] // inconclusive → no badge either way
    public void Only_a_newer_archive_is_an_update(string archiveVersion, string installedVersion,
        bool expectedUpdate, bool expectedDowngrade)
    {
        var archive = Package(archiveVersion, "CoolMod", DateTime.UtcNow);
        var installed = Installed(version: installedVersion);

        Assert.Equal(expectedUpdate, ModPackageSelector.IsUpdateAvailable(archive, installed));
        Assert.Equal(expectedDowngrade, ModPackageSelector.IsDowngrade(archive, installed));
    }

    [Fact]
    public void Two_mods_without_metadata_are_not_matched_by_the_placeholder_name()
    {
        var installed = new InstalledMod { FolderName = "ModA" };
        var package = new ModPackage { FolderName = "ModB" };

        Assert.False(ModPackageSelector.Matches(installed, package));
    }
}
