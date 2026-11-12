using U7D2D.Core.Data;
using U7D2D.Core.Models;

namespace U7D2D.Core.Tests;

public sealed class StorageTests
{
    [Fact]
    public void Settings_roundtrip_preserves_paths_and_flags()
    {
        using var ws = new TestWorkspace();
        var db = new Database(ws.Paths);
        db.Initialize();
        var store = new SettingsStore(db);

        var settings = new AppSettings
        {
            GamePath = ws.Game,
            ModsPath = ws.Mods,
            BackupsPath = ws.Backups,
            SetupCompleted = true,
            BackupBeforeChange = false,
            MaxScanDepth = 7,
            ArchivePaths = { ws.Archives, Path.Combine(ws.Root, "second") }
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(settings.GamePath, loaded.GamePath);
        Assert.Equal(settings.ModsPath, loaded.ModsPath);
        Assert.True(loaded.SetupCompleted);
        Assert.False(loaded.BackupBeforeChange);
        Assert.Equal(7, loaded.MaxScanDepth);
        Assert.Equal(2, loaded.ArchivePaths.Count);
    }

    [Fact]
    public void Settings_defaults_are_sane_on_empty_database()
    {
        using var ws = new TestWorkspace();
        var db = new Database(ws.Paths);
        db.Initialize();

        var loaded = new SettingsStore(db).Load();

        Assert.False(loaded.SetupCompleted);
        Assert.True(loaded.BackupBeforeChange);
        Assert.Equal(12, loaded.MaxScanDepth);
        Assert.Empty(loaded.ArchivePaths);
    }

    [Fact]
    public void Catalog_cache_returns_result_only_for_unchanged_archive()
    {
        using var ws = new TestWorkspace();
        var db = new Database(ws.Paths);
        db.Initialize();
        var store = new CatalogStore(db);

        var zip = ws.WriteZip("Cached.zip", ("ModC/ModInfo.xml", TestWorkspace.ModernModInfo("Cached", "1.0")));
        var scanner = new Core.Mods.ArchiveScanner(ws.Paths);
        var result = scanner.Scan(zip);

        store.Save(result);
        var info = new FileInfo(zip);

        var hit = store.TryGetCached(zip, info.Length, info.LastWriteTimeUtc, Core.Mods.ArchiveScanner.Version, string.Empty);
        Assert.NotNull(hit);
        Assert.True(hit!.FromCache);
        Assert.Single(hit.Mods);

        var miss = store.TryGetCached(zip, info.Length + 10, info.LastWriteTimeUtc, Core.Mods.ArchiveScanner.Version, string.Empty);
        Assert.Null(miss);
    }

    [Fact]
    public void Catalog_cache_rejects_entries_from_older_scanner_version()
    {
        using var ws = new TestWorkspace();
        var db = new Database(ws.Paths);
        db.Initialize();
        var store = new CatalogStore(db);

        var zip = ws.WriteZip("Stale.zip", ("ModS/ModInfo.xml", TestWorkspace.ModernModInfo("Stale", "1.0")));
        var result = new Core.Mods.ArchiveScanner(ws.Paths).Scan(zip);
        result.ScannerVersion = 1; // simulate a cache entry written by an older parser
        store.Save(result);

        var info = new FileInfo(zip);
        var hit = store.TryGetCached(zip, info.Length, info.LastWriteTimeUtc, Core.Mods.ArchiveScanner.Version, string.Empty);

        Assert.Null(hit);
    }

    [Fact]
    public void Operation_log_keeps_newest_entries_first()
    {
        using var ws = new TestWorkspace();
        var db = new Database(ws.Paths);
        db.Initialize();
        var log = new LogStore(db);

        log.Add("install", "first", string.Empty);
        log.Add("uninstall", "second", string.Empty);

        var entries = log.List(10);

        Assert.Equal(2, entries.Count);
        Assert.Equal("second", entries[0].Message);
    }

    [Fact]
    public void Installed_store_upserts_and_removes_records()
    {
        using var ws = new TestWorkspace();
        var db = new Database(ws.Paths);
        db.Initialize();
        var store = new InstalledStore(db);

        var mod = new InstalledMod
        {
            FolderName = "ModA",
            FullPath = Path.Combine(ws.Mods, "ModA"),
            Info = new ModMetadata { Name = "Mod A", Version = "1.0" },
            ContentHash = "abc",
            InstalledUtc = DateTime.UtcNow
        };

        store.Upsert(mod);
        Assert.Single(store.LoadAll());

        mod.Info.Version = "1.1";
        store.Upsert(mod);
        Assert.Equal("1.1", store.LoadAll()["ModA"].Info.Version);

        store.Remove("ModA");
        Assert.Empty(store.LoadAll());
    }
}
