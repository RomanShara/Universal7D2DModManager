using U7D2D.Core.Data;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

/// <summary>
/// The game folder is the user's data. These tests pin the promise that a failed operation
/// leaves the mod that was already installed exactly as it was.
/// </summary>
public sealed class SafetyTests
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

    private static string[] WorkDirectories(TestWorkspace ws) =>
        Directory.GetDirectories(ws.Mods, "*u7d2d*");

    [Fact]
    public void Commit_replaces_the_folder_and_leaves_no_work_directories()
    {
        using var ws = new TestWorkspace();

        var target = Path.Combine(ws.Mods, "ModA");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "old");

        var prepared = Path.Combine(ws.Root, "prepared");
        Directory.CreateDirectory(prepared);
        File.WriteAllText(Path.Combine(prepared, "marker.txt"), "new");
        File.WriteAllText(Path.Combine(prepared, "ModInfo.xml"), TestWorkspace.ModernModInfo("Mod A", "2.0"));

        var result = FileOps.CommitDirectory(prepared, target);

        Assert.True(result.Success, result.Error);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "marker.txt")));
        Assert.Empty(WorkDirectories(ws));
    }

    [Fact]
    public void Commit_keeps_the_previous_version_when_the_copy_fails()
    {
        using var ws = new TestWorkspace();

        var target = Path.Combine(ws.Mods, "ModA");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "old");

        var prepared = Path.Combine(ws.Root, "prepared");
        Directory.CreateDirectory(prepared);
        var locked = Path.Combine(prepared, "locked.bin");
        File.WriteAllText(locked, "payload");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = FileOps.CommitDirectory(prepared, target);

            Assert.False(result.Success);
            Assert.False(result.RolledBack);
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "marker.txt")));
            Assert.Empty(WorkDirectories(ws));
        }
    }

    [Fact]
    public void Commit_refuses_a_file_at_the_target_path()
    {
        using var ws = new TestWorkspace();

        var target = Path.Combine(ws.Mods, "ModA");
        File.WriteAllText(target, "not a folder");

        var prepared = Path.Combine(ws.Root, "prepared");
        Directory.CreateDirectory(prepared);
        File.WriteAllText(Path.Combine(prepared, "marker.txt"), "new");

        var result = FileOps.CommitDirectory(prepared, target);

        Assert.False(result.Success);
        Assert.Equal("not a folder", File.ReadAllText(target));
    }

    [Fact]
    public void Update_without_a_backup_still_replaces_the_mod_completely()
    {
        using var ws = new TestWorkspace();
        var (installer, _, db, settings) = Create(ws);

        var v1 = ws.WriteZip("ModA_v1.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0.0")),
            ("ModA/marker.txt", "first"));
        Assert.True(installer.Install(new ArchiveScanner(ws.Paths).Scan(v1).Mods.Single(), settings,
            new InstallOptions { AllowReplace = true, CreateBackup = false }).Success);

        var v2 = ws.WriteZip("ModA_v2.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "2.0.0")),
            ("ModA/marker.txt", "second"));
        var outcome = installer.Install(new ArchiveScanner(ws.Paths).Scan(v2).Mods.Single(), settings,
            new InstallOptions { AllowReplace = true, CreateBackup = false });

        Assert.True(outcome.Success, outcome.Error);
        Assert.False(outcome.BackupCreated);
        Assert.Equal("second", File.ReadAllText(Path.Combine(ws.Mods, "ModA", "marker.txt")));
        Assert.Empty(WorkDirectories(ws));
        Assert.Contains("ModA", new InstalledStore(db).LoadAll().Keys);
    }

    [Fact]
    public void Install_from_a_broken_archive_does_not_touch_the_installed_mod()
    {
        using var ws = new TestWorkspace();
        var (installer, _, _, settings) = Create(ws);

        var v1 = ws.WriteZip("ModA_v1.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0.0")),
            ("ModA/marker.txt", "first"));
        Assert.True(installer.Install(new ArchiveScanner(ws.Paths).Scan(v1).Mods.Single(), settings,
            new InstallOptions { AllowReplace = true }).Success);

        var broken = Path.Combine(ws.Archives, "broken.zip");
        File.WriteAllText(broken, "this is not an archive");

        var package = new ModPackage
        {
            ArchivePath = broken,
            ArchiveFileName = "broken.zip",
            FolderName = "ModA",
            RootInArchive = "ModA",
            HasModInfo = true,
            Info = new ModMetadata { Name = "Mod A", Version = "2.0.0" }
        };

        var outcome = installer.Install(package, settings,
            new InstallOptions { AllowReplace = true, CreateBackup = false });

        Assert.False(outcome.Success);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
        Assert.Equal("first", File.ReadAllText(Path.Combine(ws.Mods, "ModA", "marker.txt")));
        Assert.Empty(WorkDirectories(ws));
    }

    [Fact]
    public void Uninstall_refuses_the_mods_folder_itself()
    {
        using var ws = new TestWorkspace();
        var (installer, _, _, settings) = Create(ws);

        Directory.CreateDirectory(Path.Combine(ws.Mods, "SomeMod"));

        var record = new InstalledMod
        {
            FolderName = "Mods",
            FullPath = ws.Mods,
            Info = ModMetadata.Unknown("Mods"),
            HasModInfo = false
        };

        var outcome = installer.Uninstall(record, settings, createBackup: false);

        Assert.False(outcome.Success);
        Assert.Contains("Mods", outcome.Error);
        Assert.True(Directory.Exists(ws.Mods));
        Assert.True(Directory.Exists(Path.Combine(ws.Mods, "SomeMod")));
    }

    [Fact]
    public void Backup_list_keeps_records_when_the_backup_folder_is_unavailable()
    {
        using var ws = new TestWorkspace();
        var (_, backups, db, _) = Create(ws);

        var record = new BackupRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ModFolder = "ModA",
            ArchivePath = Path.Combine(ws.Root, "offline-drive", "ModA-20260101-000000.zip"),
            ModName = "Mod A",
            Reason = "test",
            CreatedUtc = DateTime.UtcNow
        };

        new BackupStore(db).Add(record);

        Assert.Empty(backups.List());
        Assert.Single(new BackupStore(db).List());
    }

    [Fact]
    public void Backup_create_never_reuses_an_existing_name()
    {
        using var ws = new TestWorkspace();
        var (_, backups, _, settings) = Create(ws);

        var folder = Path.Combine(ws.Mods, "ModA");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "marker.txt"), "payload");

        var first = backups.Create(folder, settings, "first");
        var second = backups.Create(folder, settings, "second");

        Assert.NotEqual(first.ArchivePath, second.ArchivePath);
        Assert.True(File.Exists(first.ArchivePath));
        Assert.True(File.Exists(second.ArchivePath));
        Assert.Equal(2, backups.List().Count);
    }
}
