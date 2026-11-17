using U7D2D.Core.I18n;
using U7D2D.Core.Models;
using U7D2D.Core.Nexus;

namespace U7D2D.Core.Tests;

public sealed class NexusUpdateRulesTests
{
    // ---------------------------------------------------------------- real pages

    [Fact]
    public void Page_version_from_the_game_does_not_create_an_update()
    {
        // NextTIERGear: ModInfo 1.0.2.0, page "3.3" (game version), Nexus file "1".
        var verdict = NexusUpdateRules.Decide("1.0.2.0", "3.3", "1", ["1"]);

        Assert.Equal(NexusCheckStatus.UpToDate, verdict.Status);
        Assert.Equal("1", verdict.LibraryVersion);
    }

    [Fact]
    public void Page_newer_than_its_own_file_is_not_an_update()
    {
        // Onsoku: ModInfo 1.0.0, page "3.4", latest file "3.2".
        var verdict = NexusUpdateRules.Decide("1.0.0", "3.4", "3.2", []);

        Assert.Equal(NexusCheckStatus.UncomparableVersion, verdict.Status);
    }

    [Fact]
    public void A_different_major_number_is_not_claimed_as_an_update()
    {
        // Singularity Perk: ModInfo 1.0, page and file "5.0".
        var verdict = NexusUpdateRules.Decide("1.0", "5.0", "5", []);

        Assert.Equal(NexusCheckStatus.UncomparableVersion, verdict.Status);
    }

    [Fact]
    public void A_downloaded_file_is_recognised_by_the_library()
    {
        // SDN Mods: ModInfo 0.0.1, page and file "1", the archive "… 13642 1 …" is in the library.
        var verdict = NexusUpdateRules.Decide("0.0.1", "1", "1", ["1"]);

        Assert.Equal(NexusCheckStatus.UpToDate, verdict.Status);
        Assert.Equal("1", verdict.LibraryVersion);
    }

    [Fact]
    public void Library_match_uses_version_rules_not_string_equality()
    {
        var verdict = NexusUpdateRules.Decide("3.1.3.2", "3.3.3.3", "3.3.3.3", ["V3.3.3.3"]);

        Assert.Equal(NexusCheckStatus.UpToDate, verdict.Status);
        Assert.Equal("V3.3.3.3", verdict.LibraryVersion);
    }

    // ---------------------------------------------------------------- genuine updates

    [Fact]
    public void A_newer_file_in_the_same_numbering_is_an_update()
    {
        var verdict = NexusUpdateRules.Decide("3.4.04", "3.4.05", "3.4.05", ["3.4.04"]);

        Assert.Equal(NexusCheckStatus.UpdateAvailable, verdict.Status);
        Assert.Equal("3.4.05", verdict.AvailableVersion);
    }

    [Fact]
    public void A_minor_jump_is_an_update()
    {
        var verdict = NexusUpdateRules.Decide("1.8.1", "1.9.0", "1.9.0", ["1.8.1"]);

        Assert.Equal(NexusCheckStatus.UpdateAvailable, verdict.Status);
    }

    // ---------------------------------------------------------------- edge cases

    [Fact]
    public void Installed_build_equal_to_the_file_is_up_to_date()
    {
        var verdict = NexusUpdateRules.Decide("2.0.0", "3.3", "2.0.0", []);

        Assert.Equal(NexusCheckStatus.UpToDate, verdict.Status);
    }

    [Fact]
    public void Missing_file_list_gives_no_verdict()
    {
        // CF Weapon Master (personal port): the website field points to the original mod.
        var verdict = NexusUpdateRules.Decide("1.1.6", "1.12.2", null, []);

        Assert.Equal(NexusCheckStatus.NoFiles, verdict.Status);
    }

    [Fact]
    public void A_local_build_newer_than_the_file_is_up_to_date()
    {
        var verdict = NexusUpdateRules.Decide("1.1.4", "1.0.0", "1.1.4", []);

        Assert.Equal(NexusCheckStatus.UpToDate, verdict.Status);
    }

    [Fact]
    public void Missing_local_version_is_not_compared()
    {
        var verdict = NexusUpdateRules.Decide(null, "3.4", "3.4", []);

        Assert.Equal(NexusCheckStatus.UncomparableVersion, verdict.Status);
    }

    // ---------------------------------------------------------------- messages

    [Fact]
    public void A_downloaded_build_is_explained_in_the_card()
    {
        var item = new NexusModUpdate
        {
            Status = NexusCheckStatus.UpToDate,
            LibraryVersion = "1",
            RemoteVersion = "1",
            LocalVersion = "0.0.1"
        };

        Assert.Equal(Localization.SF("nx2.alreadyInLibrary", "1"), NexusUpdateRules.Describe(item));
    }

    [Fact]
    public void An_inconsistent_numbering_asks_to_check_the_page()
    {
        var item = new NexusModUpdate
        {
            Status = NexusCheckStatus.UncomparableVersion,
            RemoteVersion = "5",
            LocalVersion = "1.0"
        };

        Assert.Equal(Localization.SF("nx2.checkManually", "5", "1.0"), NexusUpdateRules.Describe(item));
    }

    // ---------------------------------------------------------------- archive names

    [Theory]
    [InlineData(@"D:\mods\Onsoku 12249 3.4 2026-11-10T03-48Z EjnRsi6xY.zip", 12249, "3.4")]
    [InlineData(@"D:\mods\NextTIERGear V1.0.2 7D2D V3.4.0 13863 1 2026-11-09T15-21Z 3T2xvgl8l.zip", 13863, "1")]
    [InlineData(@"D:\mods\Zzzzxcpv Classic All In One V3.3 10710 3.3.3.3 2026-10-17T01-17Z EjnRsi6kH.zip", 10710, "3.3.3.3")]
    [InlineData(@"D:\mods\0 Vault 12194 1.4.0 2026-11-06T19-22Z M3zICRmLv.zip", 12194, "1.4.0")]
    public void Nexus_download_name_yields_the_mod_and_file_version(string path, int id, string version)
    {
        Assert.True(NexusArchiveName.TryRead(path, out var actualId, out var actualVersion));
        Assert.Equal(id, actualId);
        Assert.Equal(version, actualVersion);
    }

    [Theory]
    [InlineData(@"D:\mods\SomeMod-13102-2-1700000000.zip")]
    [InlineData(@"D:\mods\random archive.zip")]
    public void Names_without_a_file_version_are_ignored(string path)
    {
        Assert.False(NexusArchiveName.TryRead(path, out _, out _));
    }
}
