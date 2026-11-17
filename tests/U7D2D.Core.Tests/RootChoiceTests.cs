using U7D2D.Core.Infrastructure;
using U7D2D.Core.Mods;
using U7D2D.Core.Models;

namespace U7D2D.Core.Tests;

/// <summary>
/// The scanner must stay silent on a normal archive and hand over only when the install folder or the
/// content root cannot be chosen without guessing. These tests pin the exact conditions.
/// </summary>
public sealed class RootChoiceTests
{
    private static ModPackage ScanSingle(TestWorkspace ws, string fileName, params (string Path, string Content)[] files)
    {
        var zip = ws.WriteZip(fileName, files);
        return new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single();
    }

    [Fact]
    public void A_normal_archive_needs_no_choice()
    {
        using var ws = new TestWorkspace();
        var package = ScanSingle(ws, "Plain.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0")));

        Assert.False(package.NeedsRootChoice);
        Assert.Null(package.AmbiguityReason);
        Assert.Empty(package.RootCandidates);
        Assert.Equal("ModA", package.FolderName);
    }

    [Fact]
    public void A_prefix_on_the_mod_folder_itself_needs_no_choice()
    {
        using var ws = new TestWorkspace();
        var package = ScanSingle(ws, "Core.zip",
            ("0-Core/ModInfo.xml", TestWorkspace.ModernModInfo("Core", "1.0")));

        Assert.False(package.NeedsRootChoice);
        Assert.Equal("0-Core", package.FolderName);
    }

    [Fact]
    public void A_wrapper_without_a_prefix_needs_no_choice()
    {
        using var ws = new TestWorkspace();
        var package = ScanSingle(ws, "Wrapped.zip",
            ("Mods/Wrapped/ModInfo.xml", TestWorkspace.ModernModInfo("Wrapped", "1.0")));

        Assert.False(package.NeedsRootChoice);
        Assert.Equal("Wrapped", package.FolderName);
    }

    [Fact]
    public void A_prefix_above_the_mod_folder_offers_the_levels()
    {
        using var ws = new TestWorkspace();
        var package = ScanSingle(ws, "ExampleMod.zip",
            ("0-Core/Release/CoreMod/ModInfo.xml", TestWorkspace.ModernModInfo("Core Mod", "1.0")));

        Assert.True(package.NeedsRootChoice);
        Assert.Equal("prefix", package.AmbiguityReason);
        Assert.Equal("CoreMod", package.FolderName);
        Assert.Equal(new[] { "0-Core", "Release", "CoreMod" }, package.RootCandidates.Select(c => c.Name));
        Assert.Equal(new[] { "0-Core", "0-Core/Release", "0-Core/Release/CoreMod" },
            package.RootCandidates.Select(c => c.DisplayPath));
        Assert.All(package.RootCandidates, c => Assert.Equal("0-Core/Release/CoreMod", c.ContentRoot));
        Assert.Equal("CoreMod", Assert.Single(package.RootCandidates, c => c.IsDefault).Name);
    }

    [Fact]
    public void The_chosen_folder_keeps_its_own_prefix()
    {
        using var ws = new TestWorkspace();
        var package = ScanSingle(ws, "Last.zip",
            ("zzz_Last/mod/ModInfo.xml", TestWorkspace.ModernModInfo("Last", "1.0")));

        Assert.False(package.NeedsRootChoice);
        Assert.Equal("zzz_Last", package.FolderName);
    }

    [Fact]
    public void A_nested_modinfo_offers_the_content_roots()
    {
        using var ws = new TestWorkspace();
        var package = ScanSingle(ws, "Layer.zip",
            ("Layer/ModInfo.xml", TestWorkspace.ModernModInfo("Outer", "1.0")),
            ("Layer/Inner/ModInfo.xml", TestWorkspace.ModernModInfo("Inner", "2.0")));

        Assert.True(package.NeedsRootChoice);
        Assert.Equal("nested", package.AmbiguityReason);

        var defaultCandidate = Assert.Single(package.RootCandidates, c => c.IsDefault);
        Assert.Equal("Layer", defaultCandidate.Name);
        Assert.Equal("Layer", defaultCandidate.ContentRoot);

        var inner = Assert.Single(package.RootCandidates, c => c.ContentRoot == "Layer/Inner");
        Assert.Equal("Inner", inner.Name);
    }

    [Fact]
    public void Nested_roots_and_a_prefix_are_reported_together()
    {
        using var ws = new TestWorkspace();
        var package = ScanSingle(ws, "Both.zip",
            ("0-Core/Release/Inner/ModInfo.xml", TestWorkspace.ModernModInfo("Outer", "1.0")),
            ("0-Core/Release/Inner/Extra/ModInfo.xml", TestWorkspace.ModernModInfo("Extra", "2.0")));

        Assert.True(package.NeedsRootChoice);
        Assert.Equal("nested+prefix", package.AmbiguityReason);
        Assert.Contains(package.RootCandidates, c => c.Name == "0-Core" && c.ContentRoot == "0-Core/Release/Inner");
        Assert.Contains(package.RootCandidates, c => c.Name == "Inner" && c.IsDefault);
        Assert.Contains(package.RootCandidates, c => c.Name == "Extra" && c.ContentRoot == "0-Core/Release/Inner/Extra");
    }

    [Fact]
    public void Root_candidates_survive_the_scan_cache()
    {
        using var ws = new TestWorkspace();
        var package = ScanSingle(ws, "ExampleMod.zip",
            ("0-Core/Release/CoreMod/ModInfo.xml", TestWorkspace.ModernModInfo("Core Mod", "1.0")));

        var copy = JsonConfig.Deserialize<ModPackage>(JsonConfig.Serialize(package));

        Assert.NotNull(copy);
        Assert.True(copy!.NeedsRootChoice);
        Assert.Equal("prefix", copy.AmbiguityReason);
        Assert.Equal(package.RootCandidates.Select(c => c.Name), copy.RootCandidates.Select(c => c.Name));
        Assert.Equal(package.RootCandidates.Select(c => c.ContentRoot), copy.RootCandidates.Select(c => c.ContentRoot));
    }
}
