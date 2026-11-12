using U7D2D.Core.Data;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

public sealed class InstallerTests
{
    private static (ModInstaller Installer, BackupService Backups, Database Db, AppSettings Settings) Create(TestWorkspace ws)
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
            BackupBeforeChange = true
        };
        return (installer, backups, db, settings);
    }

    [Fact]
    public void Installs_mod_folder_and_registers_it()
    {
        using var ws = new TestWorkspace();
        var (installer, _, db, settings) = Create(ws);

        var zip = ws.WriteZip("ModA.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0.0")),
            ("ModA/Config/blocks.xml", TestWorkspace.BlocksPatch("a")));

        var package = new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single();
        var outcome = installer.Install(package, settings, new InstallOptions { AllowReplace = false, CreateBackup = true });

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(File.Exists(Path.Combine(ws.Mods, "ModA", "ModInfo.xml")));
        Assert.True(File.Exists(Path.Combine(ws.Mods, "ModA", "Config", "blocks.xml")));
        Assert.False(outcome.ReplacedExisting);
        Assert.Contains("ModA", new InstalledStore(db).LoadAll().Keys);
    }

    [Fact]
    public void Refuses_silent_overwrite_of_existing_folder()
    {
        using var ws = new TestWorkspace();
        var (installer, _, _, settings) = Create(ws);

        var v1 = ws.WriteZip("ModA_v1.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0.0")),
            ("ModA/marker.txt", "first"));

        var package = new ArchiveScanner(ws.Paths).Scan(v1).Mods.Single();
        Assert.True(installer.Install(package, settings, new InstallOptions { AllowReplace = false }).Success);

        var v2 = ws.WriteZip("ModA_v2.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "2.0.0")),
            ("ModA/marker.txt", "second"));

        var second = new ArchiveScanner(ws.Paths).Scan(v2).Mods.Single();
        var outcome = installer.Install(second, settings, new InstallOptions { AllowReplace = false });

        Assert.False(outcome.Success);
        Assert.Contains("already exists", outcome.Error);
        Assert.Equal("first", File.ReadAllText(Path.Combine(ws.Mods, "ModA", "marker.txt")));
    }

    [Fact]
    public void Replaces_existing_mod_with_backup_when_confirmed()
    {
        using var ws = new TestWorkspace();
        var (installer, _, _, settings) = Create(ws);

        var v1 = ws.WriteZip("ModA_v1.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0.0")),
            ("ModA/marker.txt", "first"));
        Assert.True(installer.Install(new ArchiveScanner(ws.Paths).Scan(v1).Mods.Single(), settings, new InstallOptions()).Success);

        var v2 = ws.WriteZip("ModA_v2.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "2.0.0")),
            ("ModA/marker.txt", "second"));

        var outcome = installer.Install(
            new ArchiveScanner(ws.Paths).Scan(v2).Mods.Single(), settings,
            new InstallOptions { AllowReplace = true, CreateBackup = true });

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(outcome.ReplacedExisting);
        Assert.True(outcome.BackupCreated);
        Assert.True(File.Exists(outcome.BackupPath));
        Assert.Equal("second", File.ReadAllText(Path.Combine(ws.Mods, "ModA", "marker.txt")));
    }

    [Fact]
    public void Uninstall_removes_folder_and_creates_backup()
    {
        using var ws = new TestWorkspace();
        var (installer, _, db, settings) = Create(ws);

        var zip = ws.WriteZip("ModA.zip", ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0.0")));
        Assert.True(installer.Install(new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single(), settings, new InstallOptions()).Success);

        var installed = new InstalledModScanner(new InstalledStore(db)).Scan(ws.Mods).Single();
        var outcome = installer.Uninstall(installed, settings, createBackup: true);

        Assert.True(outcome.Success, outcome.Error);
        Assert.False(Directory.Exists(Path.Combine(ws.Mods, "ModA")));
        Assert.True(outcome.BackupCreated);
        Assert.True(File.Exists(outcome.BackupPath));
    }

    [Fact]
    public void Uninstall_outside_mods_folder_is_refused()
    {
        using var ws = new TestWorkspace();
        var (installer, _, _, settings) = Create(ws);

        var outside = new InstalledMod
        {
            FolderName = "Outside",
            FullPath = ws.Archives,
            Info = ModMetadata.Unknown("Outside"),
            HasModInfo = true
        };

        var outcome = installer.Uninstall(outside, settings, createBackup: false);

        Assert.False(outcome.Success);
        Assert.Contains("outside", outcome.Error);
        Assert.True(Directory.Exists(ws.Archives));
    }

    [Fact]
    public void Restore_returns_previous_mod_content()
    {
        using var ws = new TestWorkspace();
        var (installer, backups, _, settings) = Create(ws);

        var folder = Path.Combine(ws.Mods, "Restorable");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ModInfo.xml"), TestWorkspace.ModernModInfo("Restorable", "1.0"));
        File.WriteAllText(Path.Combine(folder, "data.txt"), "original");

        var backup = backups.Create(folder, settings, "test");

        File.WriteAllText(Path.Combine(folder, "data.txt"), "changed");
        File.WriteAllText(Path.Combine(folder, "extra.txt"), "extra");

        var outcome = backups.Restore(backup, settings);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("original", File.ReadAllText(Path.Combine(folder, "data.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "extra.txt")));
    }
}
