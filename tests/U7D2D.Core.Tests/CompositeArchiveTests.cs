using U7D2D.Core.Data;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

/// <summary>
/// A container with shared files and several child mods. The game ignores a folder without a
/// ModInfo.xml and reads DLLs only from a mod folder root, so the shared files must become a mod of
/// their own; children stay separate.
/// </summary>
public sealed class CompositeArchiveTests
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
    public void A_container_with_shared_files_and_two_mods_becomes_one_composite_package()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Composite.zip",
            ("mod/0Harmony.dll", "shared"),
            ("mod/mod-a/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0")),
            ("mod/mod-b/ModInfo.xml", TestWorkspace.ModernModInfo("Mod B", "2.0")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);

        Assert.True(package.IsComposite);
        Assert.Equal("mod", package.RootInArchive);
        Assert.Equal(2, package.Components.Count);
        Assert.Contains("0Harmony.dll", package.SharedFiles);
        Assert.Equal("000-Composite", package.SharedFolder);
        Assert.Contains("mod/mod-a", package.ExcludeRoots);
        Assert.Contains("mod/mod-b", package.ExcludeRoots);
        Assert.True(package.GeneratesModInfo);
    }

    [Fact]
    public void Two_mods_without_shared_files_stay_separate()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Collection.zip",
            ("mod-a/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0")),
            ("mod-b/ModInfo.xml", TestWorkspace.ModernModInfo("Mod B", "2.0")));

        var result = new ArchiveScanner(ws.Paths).Scan(zip);

        Assert.Equal(2, result.Mods.Count);
        Assert.All(result.Mods, package => Assert.False(package.IsComposite));
    }

    [Fact]
    public void One_child_mod_with_a_shared_dll_is_not_composite()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Single.zip",
            ("mod/0Harmony.dll", "shared"),
            ("mod/mod-a/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);

        Assert.False(package.IsComposite);
        Assert.Equal("mod-a", package.FolderName);
    }

    [Fact]
    public void Composite_install_writes_a_generated_mod_and_both_children()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Composite.zip",
            ("mod/0Harmony.dll", "shared"),
            ("mod/mod-a/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0")),
            ("mod/mod-a/Config/blocks.xml", TestWorkspace.BlocksPatch("a")),
            ("mod/mod-b/ModInfo.xml", TestWorkspace.ModernModInfo("Mod B", "2.0")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);
        var (installer, settings) = Create(ws);

        var outcome = installer.Install(package, settings, new InstallOptions { AllowReplace = false });

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(File.Exists(Path.Combine(ws.Mods, "000-Composite", "ModInfo.xml")));
        Assert.True(File.Exists(Path.Combine(ws.Mods, "000-Composite", "0Harmony.dll")));
        Assert.False(Directory.Exists(Path.Combine(ws.Mods, "000-Composite", "mod-a")));
        Assert.False(Directory.Exists(Path.Combine(ws.Mods, "000-Composite", "mod-b")));

        Assert.True(File.Exists(Path.Combine(ws.Mods, "mod-a", "ModInfo.xml")));
        Assert.True(File.Exists(Path.Combine(ws.Mods, "mod-a", "Config", "blocks.xml")));
        Assert.True(File.Exists(Path.Combine(ws.Mods, "mod-b", "ModInfo.xml")));

        var generated = File.ReadAllText(Path.Combine(ws.Mods, "000-Composite", "ModInfo.xml"));
        Assert.Contains("<Name value=\"000-Composite\"", generated);
        Assert.Contains("<DisplayName", generated);
        Assert.DoesNotContain("&lt;", generated);
    }

    [Fact]
    public void Reinstalling_a_composite_requires_explicit_replace()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Composite.zip",
            ("mod/0Harmony.dll", "shared"),
            ("mod/mod-a/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0")),
            ("mod/mod-b/ModInfo.xml", TestWorkspace.ModernModInfo("Mod B", "2.0")));

        var package = Assert.Single(new ArchiveScanner(ws.Paths).Scan(zip).Mods);
        var (installer, settings) = Create(ws);

        Assert.True(installer.Install(package, settings, new InstallOptions()).Success);

        var blocked = installer.Install(package, settings, new InstallOptions { AllowReplace = false });
        Assert.False(blocked.Success);
        Assert.False(string.IsNullOrWhiteSpace(blocked.Error));

        var replaced = installer.Install(package, settings, new InstallOptions { AllowReplace = true });
        Assert.True(replaced.Success, replaced.Error);
    }
}
