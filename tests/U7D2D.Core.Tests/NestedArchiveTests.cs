using U7D2D.Core.Data;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

/// <summary>
/// Archives where ModInfo.xml sits several folders deep. The extracted mod must still end up as a
/// single folder directly under Mods, with ModInfo.xml at its root and the author's load-order prefix
/// intact: the game sorts mods by folder name, so renaming "0-Core" to "mod" changes the load order.
/// </summary>
public sealed class NestedArchiveTests
{
    private static (ModInstaller Installer, AppSettings Settings) Create(TestWorkspace ws)
    {
        var db = new Database(ws.Paths);
        db.Initialize();
        var backups = new BackupService(ws.Paths, new BackupStore(db));
        var installer = new ModInstaller(ws.Paths, backups, new InstalledStore(db));
        var settings = new AppSettings
        {
            GamePath = ws.Game,
            ModsPath = ws.Mods,
            BackupsPath = ws.Backups,
            BackupBeforeChange = false
        };
        return (installer, settings);
    }

    [Fact]
    public void A_mod_six_folders_deep_is_detected_and_installed_flat()
    {
        using var ws = new TestWorkspace();
        const string root = "mod/mod/mod/mod/mod/mod";
        var zip = ws.WriteZip("DeepMod.zip",
            ($"{root}/ModInfo.xml", TestWorkspace.ModernModInfo("Deep Mod", "1.2.3")),
            ($"{root}/Config/blocks.xml", TestWorkspace.BlocksPatch("deep")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);
        Assert.Equal(root, package.RootInArchive);
        Assert.Equal("DeepMod", package.FolderName);
        Assert.Equal("Deep Mod", package.Info.Name);

        var (installer, settings) = Create(ws);
        var outcome = installer.Install(package, settings, new InstallOptions { AllowReplace = false });

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(File.Exists(Path.Combine(ws.Mods, "DeepMod", "ModInfo.xml")));
        Assert.True(File.Exists(Path.Combine(ws.Mods, "DeepMod", "Config", "blocks.xml")));
        Assert.False(Directory.Exists(Path.Combine(ws.Mods, "DeepMod", "mod")));
    }

    [Fact]
    public void The_load_order_prefix_survives_a_generic_wrapper()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("WrappedCore.zip",
            ("0-Core/mod/mod/ModInfo.xml", TestWorkspace.ModernModInfo("Core", "3.4")),
            ("0-Core/mod/mod/Config/blocks.xml", TestWorkspace.BlocksPatch("core")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);
        Assert.Equal("0-Core/mod/mod", package.RootInArchive);
        Assert.Equal("0-Core", package.FolderName);

        var (installer, settings) = Create(ws);
        var outcome = installer.Install(package, settings, new InstallOptions { AllowReplace = false });

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(File.Exists(Path.Combine(ws.Mods, "0-Core", "ModInfo.xml")));
        Assert.False(Directory.Exists(Path.Combine(ws.Mods, "mod")));
    }

    [Theory]
    [InlineData("zzz_Last/mod/ModInfo.xml", "zzz_Last")]
    [InlineData("Mods/AuthorPack/ModInfo.xml", "AuthorPack")]
    [InlineData("files/content/ModInfo.xml", "ArchiveName")] // all generic: the archive name wins
    public void A_generic_wrapper_does_not_replace_the_real_folder(string root, string expected)
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("ArchiveName.zip",
            ($"{root}", TestWorkspace.ModernModInfo("Some Mod", "1.0")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);
        Assert.Equal(expected, package.FolderName);
    }

    [Fact]
    public void A_nested_modinfo_inside_another_mod_is_ignored()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Layered.zip",
            ("Layer/ModInfo.xml", TestWorkspace.ModernModInfo("Outer", "1.0")),
            ("Layer/Inner/ModInfo.xml", TestWorkspace.ModernModInfo("Inner", "2.0")));

        var result = new ArchiveScanner(ws.Paths).Scan(zip);

        var package = Assert.Single(result.Mods);
        Assert.Equal("Layer", package.FolderName);
        Assert.Equal("Layer", package.RootInArchive);
        Assert.Contains(result.Warnings, w => w.Contains("nested ModInfo.xml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Installed_folders_keep_the_order_the_game_sorts_by()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Pack.zip",
            ("0-Core/ModInfo.xml", TestWorkspace.ModernModInfo("Core", "1.0")),
            ("Middle/ModInfo.xml", TestWorkspace.ModernModInfo("Middle", "1.0")),
            ("zzz_Last/ModInfo.xml", TestWorkspace.ModernModInfo("Last", "1.0")));

        var packages = new ArchiveScanner(ws.Paths).Scan(zip).Mods;
        var (installer, settings) = Create(ws);

        foreach (var package in packages)
        {
            var outcome = installer.Install(package, settings, new InstallOptions { AllowReplace = false });
            Assert.True(outcome.Success, outcome.Error);
        }

        var installed = Directory.GetDirectories(ws.Mods)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "0-Core", "Middle", "zzz_Last" }, installed);
        foreach (var folder in installed)
            Assert.True(File.Exists(Path.Combine(ws.Mods, folder!, "ModInfo.xml")));
    }
}
