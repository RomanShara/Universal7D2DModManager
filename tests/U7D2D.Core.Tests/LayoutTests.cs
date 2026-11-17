using System.IO.Compression;
using System.Text;
using U7D2D.Core.Data;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

/// <summary>
/// The manager has to work wherever the user puts it: another drive for the program, the game,
/// the archives and the backups, and folder names with spaces, Cyrillic or a long path.
/// These tests build real folders on C:, D: and E: and run the real scanner, installer and
/// backup service against them.
/// </summary>
public sealed class LayoutTests
{
    private static readonly string LocalApp = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "u7d2d-layout-tests");

    // Tests must run on any machine. CI runners often have a single fixed drive, so the "another
    // drive" roles are filled from whatever volumes exist and fall back to the temp folder when a
    // volume is missing or not writable. The layout logic is about separate roots, not about the
    // literal letters C, D and E.
    private static readonly string[] VolumeBases = BuildVolumeBases();

    private static string VolumeBase(int slot) => VolumeBases[slot % VolumeBases.Length];

    private static string[] BuildVolumeBases()
    {
        List<string> roots;
        try
        {
            roots = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            roots = new List<string>();
        }

        return ResolveBases(roots);
    }

    /// <summary>
    /// Fills three "drive" slots from the roots that exist. With a single root all slots share it,
    /// which is exactly what a CI runner with one fixed drive needs.
    /// </summary>
    private static string[] ResolveBases(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
            roots = new[] { Path.GetPathRoot(Path.GetTempPath()) ?? Path.GetTempPath() };

        var bases = new string[Math.Max(3, roots.Count)];
        for (var i = 0; i < bases.Length; i++)
            bases[i] = PrepareBase(roots[i % roots.Count], i);

        return bases;
    }

    [Fact]
    public void A_single_volume_is_enough_for_the_layout_tests()
    {
        var single = Path.Combine(Path.GetTempPath(), $"u7d2d-single-{Guid.NewGuid():N}");
        Directory.CreateDirectory(single);

        try
        {
            var bases = ResolveBases(new[] { single });

            Assert.Equal(3, bases.Length);
            Assert.All(bases, b => Assert.True(Directory.Exists(b)));
            Assert.All(bases, b => Assert.StartsWith(single, b, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(single, true); } catch (Exception) { }
        }
    }

    private static string PrepareBase(string volumeRoot, int slot)
    {
        var candidate = Path.Combine(volumeRoot, "_u7d2d_layout_tests");
        try
        {
            Directory.CreateDirectory(candidate);
            var probe = Path.Combine(candidate, $".probe-{slot}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return candidate;
        }
        catch (Exception)
        {
            var fallback = Path.Combine(Path.GetTempPath(), $"_u7d2d_layout_tests_vol{slot}");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private sealed class Lab : IDisposable
    {
        public Lab(AppPaths paths, BackupService backups, ModInstaller installer, AppSettings settings,
            params string[] roots)
        {
            Paths = paths;
            Backups = backups;
            Installer = installer;
            Settings = settings;
            Roots = roots;
        }

        public AppPaths Paths { get; }
        public BackupService Backups { get; }
        public ModInstaller Installer { get; }
        public AppSettings Settings { get; }
        public string[] Roots { get; }

        public void Dispose()
        {
            // Pooled SQLite connections keep the database file open for a moment; without this the
            // test leaves its temporary folders behind on C:, D: or E:.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            foreach (var root in Roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;

                    try { Directory.Delete(root, true); }
                    catch (IOException) { Directory.Delete(@"\\?\" + root, true); }

                    // The shared test parent is removed once the last root is gone.
                    var parent = Path.GetDirectoryName(root);
                    if (parent is not null && Directory.Exists(parent) &&
                        !Directory.EnumerateFileSystemEntries(parent).Any())
                        Directory.Delete(parent);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static Lab CreateLab(string appRoot, string gameRoot, string archiveRoot, string backupRoot)
    {
        foreach (var folder in new[] { appRoot, gameRoot, archiveRoot, backupRoot })
            Directory.CreateDirectory(folder);

        var paths = new AppPaths(appRoot);
        var database = new Database(paths);
        database.Initialize();

        var backups = new BackupService(paths, new BackupStore(database));
        var installer = new ModInstaller(paths, backups, new InstalledStore(database));

        var settings = new AppSettings
        {
            GamePath = gameRoot,
            ModsPath = Path.Combine(gameRoot, "Mods"),
            BackupsPath = backupRoot,
            ArchivePaths = { archiveRoot },
            BackupBeforeChange = true
        };

        Directory.CreateDirectory(settings.ModsPath);
        return new Lab(paths, backups, installer, settings, appRoot, gameRoot, archiveRoot, backupRoot);
    }

    private static string WriteModArchive(string archiveRoot, string folder, string version, string marker)
    {
        var path = Path.Combine(archiveRoot, $"{folder}.zip");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Add(zip, $"{folder}/ModInfo.xml", TestWorkspace.ModernModInfo(folder, version));
        Add(zip, $"{folder}/marker.txt", marker);
        Add(zip, $"{folder}/Config/items.xml", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <configs>
              <set xpath="/items/item[@name='meleeToolRepairT0StoneAxe']/property[@name='EconomicValue']/@value">321</set>
            </configs>
            """);

        return path;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void InstallBackupRestore(Lab lab, string folder, string marker)
    {
        var archive = WriteModArchive(lab.Settings.ArchivePaths[0], folder, "1.0.0", marker);
        var package = new ArchiveScanner(lab.Paths).Scan(archive).Mods.Single();

        var install = lab.Installer.Install(package, lab.Settings,
            new InstallOptions { AllowReplace = true, CreateBackup = true });

        Assert.True(install.Success, install.Error);

        var target = Path.Combine(lab.Settings.ModsPath, folder);
        Assert.Equal(marker, File.ReadAllText(Path.Combine(target, "marker.txt")));

        var backup = lab.Backups.Create(target, lab.Settings, "layout test");
        File.WriteAllText(Path.Combine(target, "marker.txt"), "tampered");

        var restore = lab.Backups.Restore(backup, lab.Settings);
        Assert.True(restore.Success, restore.Error);
        Assert.Equal(marker, File.ReadAllText(Path.Combine(target, "marker.txt")));

        // A cross-drive install stages on the program drive and copies to the game drive; nothing
        // may be left next to the mod.
        Assert.Empty(Directory.GetDirectories(lab.Settings.ModsPath, "*.u7d2d*"));
    }

    [Fact]
    public void Manager_on_C_game_on_D_archives_on_E()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        using var lab = CreateLab(
            Path.Combine(LocalApp, $"app-{id}"),
            Path.Combine(VolumeBase(1), $"game-{id}"),
            Path.Combine(VolumeBase(2), $"archives-{id}"),
            Path.Combine(VolumeBase(2), $"backups-{id}"));

        InstallBackupRestore(lab, "ModA", "v1");
    }

    [Fact]
    public void Manager_on_D_game_on_C_archives_on_E()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        using var lab = CreateLab(
            Path.Combine(VolumeBase(1), $"app-{id}"),
            Path.Combine(LocalApp, $"game-{id}"),
            Path.Combine(VolumeBase(2), $"archives-{id}"),
            Path.Combine(LocalApp, $"backups-{id}"));

        InstallBackupRestore(lab, "ModA", "v1");
    }

    [Fact]
    public void Backups_on_a_different_drive_than_program_and_game()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        using var lab = CreateLab(
            Path.Combine(LocalApp, $"app-{id}"),
            Path.Combine(VolumeBase(1), $"game-{id}"),
            Path.Combine(VolumeBase(1), $"archives-{id}"),
            Path.Combine(VolumeBase(2), $"backups-{id}"));

        InstallBackupRestore(lab, "ModA", "v1");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            try { Directory.Delete(path, true); }
            catch (IOException) { Directory.Delete(@"\\?\" + path, true); }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteEmptyParent(string path)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (parent is null || !Directory.Exists(parent)) return;
            if (Directory.EnumerateFileSystemEntries(parent).Any()) return;

            Directory.Delete(parent);
        }
        catch (Exception)
        {
        }
    }

    [Fact]
    public void Spaces_cyrillic_and_a_long_path()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var longName = string.Concat(Enumerable.Repeat("Очень длинная папка с пробелами и кириллицей ", 5)).Trim();

        // Around 300 characters with the files inside: longer than the old MAX_PATH limit.
        var root = Path.Combine(VolumeBase(1), $"{longName} {id}");

        try
        {
            using (var lab = CreateLab(
                       Path.Combine(root, "менеджер"),
                       Path.Combine(root, "игра"),
                       Path.Combine(root, "архивы модов"),
                       Path.Combine(root, "резервные копии")))
            {
                InstallBackupRestore(lab, "Мод А", "v1");
            }
        }
        finally
        {
            TryDelete(root);
            TryDeleteEmptyParent(root);
        }
    }

    [Fact]
    public void Cross_volume_copy_failure_keeps_the_old_mod_and_leaves_no_garbage()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var prepared = Path.Combine(LocalApp, $"prepared-{id}");
        var targetRoot = Path.Combine(VolumeBase(1), $"target-{id}");
        var target = Path.Combine(targetRoot, "ModA");

        Directory.CreateDirectory(prepared);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "old");
        File.WriteAllText(Path.Combine(prepared, "marker.txt"), "new");

        var locked = Path.Combine(prepared, "locked.bin");
        File.WriteAllText(locked, "payload");

        try
        {
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Prepared is on C:, the target is on D:; the copy fails before the swap.
                var result = FileOps.CommitDirectory(prepared, target);

                Assert.False(result.Success);
                Assert.Equal("old", File.ReadAllText(Path.Combine(target, "marker.txt")));
                Assert.Empty(Directory.GetDirectories(targetRoot, "*.u7d2d*"));
            }
        }
        finally
        {
            try { Directory.Delete(prepared, true); } catch (Exception) { }
            try { Directory.Delete(targetRoot, true); } catch (Exception) { }
        }
    }
}
