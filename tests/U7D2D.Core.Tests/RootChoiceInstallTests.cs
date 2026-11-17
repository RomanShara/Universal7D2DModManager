using U7D2D.Core.Data;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

public sealed class RootChoiceInstallTests
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
    public void Installing_a_picked_inner_root_uses_its_files_and_the_picked_name()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Layer.zip",
            ("Layer/ModInfo.xml", TestWorkspace.ModernModInfo("Outer", "1.0")),
            ("Layer/Config/blocks.xml", TestWorkspace.BlocksPatch("outer")),
            ("Layer/Inner/ModInfo.xml", TestWorkspace.ModernModInfo("Inner", "2.0")),
            ("Layer/Inner/Config/items.xml", "<items/>"));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);
        var inner = Assert.Single(package.RootCandidates, c => c.ContentRoot == "Layer/Inner");

        var (installer, settings) = Create(ws);
        var outcome = installer.Install(package, settings, new InstallOptions
        {
            AllowReplace = false,
            FolderNameOverride = "0-Inner",
            ContentRootOverride = inner.ContentRoot
        });

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(File.Exists(Path.Combine(ws.Mods, "0-Inner", "ModInfo.xml")));
        Assert.True(File.Exists(Path.Combine(ws.Mods, "0-Inner", "Config", "items.xml")));
        Assert.False(File.Exists(Path.Combine(ws.Mods, "0-Inner", "Config", "blocks.xml")));
        Assert.False(Directory.Exists(Path.Combine(ws.Mods, "0-Inner", "Inner")));
    }

    [Fact]
    public void A_content_root_outside_the_package_is_rejected()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Layer.zip",
            ("Layer/ModInfo.xml", TestWorkspace.ModernModInfo("Outer", "1.0")),
            ("Layer/Inner/ModInfo.xml", TestWorkspace.ModernModInfo("Inner", "2.0")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);
        var (installer, settings) = Create(ws);

        var outcome = installer.Install(package, settings, new InstallOptions
        {
            AllowReplace = false,
            FolderNameOverride = "0-Inner",
            ContentRootOverride = "Other/Mod"
        });

        Assert.False(outcome.Success);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        Assert.False(Directory.Exists(Path.Combine(ws.Mods, "0-Inner")));
    }

    [Fact]
    public void A_parent_traversal_in_the_content_root_is_rejected()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Layer.zip",
            ("Layer/ModInfo.xml", TestWorkspace.ModernModInfo("Outer", "1.0")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);
        var (installer, settings) = Create(ws);

        var outcome = installer.Install(package, settings, new InstallOptions
        {
            AllowReplace = false,
            FolderNameOverride = "0-Outer",
            ContentRootOverride = "Layer/../Layer"
        });

        Assert.False(outcome.Success);
    }

    [Fact]
    public void The_remembered_root_is_stored_per_archive_fingerprint()
    {
        using var ws = new TestWorkspace();
        var db = new Database(ws.Paths);
        db.Initialize();
        var store = new RootChoiceStore(db);

        store.Save("fingerprint-a", "0-Core", "0-Core/Release/CoreMod");
        var saved = store.Get("fingerprint-a");

        Assert.NotNull(saved);
        Assert.Equal("0-Core", saved!.Value.Name);
        Assert.Equal("0-Core/Release/CoreMod", saved.Value.ContentRoot);
        Assert.Null(store.Get("fingerprint-b"));
    }

    [Fact]
    public void Candidates_carry_a_preview_of_what_will_be_installed()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("ExampleMod.zip",
            ("0-Core/Release/CoreMod/ModInfo.xml", TestWorkspace.ModernModInfo("Core Mod", "1.0")),
            ("0-Core/Release/CoreMod/Config/blocks.xml", TestWorkspace.BlocksPatch("core")),
            ("0-Core/Release/CoreMod/Config/items.xml", "<items/>"));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);

        Assert.All(package.RootCandidates, candidate =>
        {
            Assert.Contains("ModInfo.xml", candidate.TopLevelEntries);
            Assert.Contains("Config/", candidate.TopLevelEntries);
            Assert.Equal(3, candidate.FileCount);
        });
    }
}
