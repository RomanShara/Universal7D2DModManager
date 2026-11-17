using System.Diagnostics;
using System.IO;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.I18n;

namespace U7D2D.App.Infrastructure;

/// <summary>
/// A portable program can be unpacked into Program Files, where a standard user cannot create the
/// Data folder or the extracted web assets. Instead of failing on the first file write, the manager
/// copies itself into the user profile and starts that copy.
///
/// User data lives outside the versioned copy: %LOCALAPPDATA%\Universal7D2DModManager\Data. Every
/// version uses the same database, settings and backups, and an update only replaces the program
/// files. On the first relocation the existing Data folder is copied there, so nothing is stranded
/// in the read-only original or in an older copy.
/// </summary>
internal static class ReadOnlyInstall
{
    private static readonly string[] SkipFolders = ["Data", "wwwroot", "web"];

    private static readonly HashSet<string> SkipInData =
        new(StringComparer.OrdinalIgnoreCase) { "webview", "temp", "cache" };

    public static bool TryRelaunch(string[] args, out string? error)
    {
        error = null;

        var source = AppContext.BaseDirectory;
        if (CanRunInPlace(source)) return false;

        var target = FallbackDirectory();
        if (SamePath(source, target))
        {
            error = Localization.SF("app.readOnlyFailed", source);
            return false;
        }

        var dataRoot = DataDirectory();
        if (!PathSafety.IsDirectoryWritable(dataRoot))
        {
            error = Localization.SF("app.readOnlyFailed", dataRoot);
            return false;
        }

        try
        {
            // Bring the database, settings and backups over before the new copy starts.
            var migrated = MigrateData(source, dataRoot);

            Directory.CreateDirectory(target);

            var signature = typeof(ReadOnlyInstall).Assembly.ManifestModule.ModuleVersionId.ToString("N");
            var marker = Path.Combine(target, ".build");
            var executableName = Path.GetFileName(Environment.ProcessPath ?? "Universal7D2DModManager.exe");
            var executable = Path.Combine(target, executableName);

            var needsCopy = !File.Exists(executable) ||
                            !File.Exists(marker) ||
                            !string.Equals(File.ReadAllText(marker).Trim(), signature, StringComparison.OrdinalIgnoreCase);

            if (needsCopy)
            {
                CopyApplication(source, target);
                File.WriteAllText(marker, signature);
            }

            if (!File.Exists(executable))
            {
                error = Localization.SF("app.readOnlyFailed", executable);
                return false;
            }

            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = target,
                UseShellExecute = false
            };

            // One data folder for every version of the per-user copy.
            start.Environment["U7D2D_DATA_ROOT"] = dataRoot;

            foreach (var arg in args) start.ArgumentList.Add(arg);

            Process.Start(start);
            WriteNote(source, target);

            // Old copies are deleted only after every file was merged; a failed copy keeps them.
            if (migrated) CleanupOldCopies(target);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Where all per-user data lives, independent of the copied build.</summary>
    private static string DataDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();

        return Path.Combine(local, "Universal7D2DModManager", "Data");
    }

    /// <summary>
    /// Copies the newest database and every backup from the read-only folder and from older
    /// per-user copies into the shared Data folder. An existing shared database always wins.
    /// </summary>
    private static bool MigrateData(string source, string dataRoot)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);

            var parent = Path.GetDirectoryName(dataRoot)!;
            var candidates = new List<string>();

            var sourceData = Path.Combine(source, "Data");
            if (Directory.Exists(sourceData)) candidates.Add(sourceData);

            foreach (var copy in Directory.EnumerateDirectories(parent, "app-*"))
            {
                var data = Path.Combine(copy, "Data");
                if (Directory.Exists(data)) candidates.Add(data);
            }

            if (candidates.Count == 0) return true;

            var targetDb = Path.Combine(dataRoot, "manager.db");

            if (!File.Exists(targetDb))
            {
                var best = candidates
                    .Select(directory => (Directory: directory, Database: Path.Combine(directory, "manager.db")))
                    .Where(candidate => File.Exists(candidate.Database))
                    .OrderByDescending(candidate => File.GetLastWriteTimeUtc(candidate.Database))
                    .Select(candidate => candidate.Directory)
                    .FirstOrDefault();

                if (best is not null)
                {
                    CopyFileIfMissing(best, dataRoot, "manager.db");
                    CopyFileIfMissing(best, dataRoot, "manager.db-wal");
                    CopyFileIfMissing(best, dataRoot, "manager.db-shm");
                }
            }

            var skip = new HashSet<string>(SkipInData, StringComparer.OrdinalIgnoreCase);
            if (File.Exists(targetDb))
            {
                skip.Add("manager.db-wal");
                skip.Add("manager.db-shm");
            }

            foreach (var candidate in candidates)
                CopyMissing(candidate, dataRoot, skip);

            return true;
        }
        catch (Exception)
        {
            // Migration is best effort: the app still opens with a fresh Data folder. Old copies
            // stay in place because their data was not merged.
            return false;
        }
    }

    private static void CopyFileIfMissing(string sourceDirectory, string targetDirectory, string name)
    {
        var source = Path.Combine(sourceDirectory, name);
        if (!File.Exists(source)) return;

        var target = Path.Combine(targetDirectory, name);
        if (!File.Exists(target)) File.Copy(source, target);
    }

    private static void CopyMissing(string source, string target, HashSet<string> skip)
    {
        if (!Directory.Exists(source)) return;

        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (skip.Contains(name)) continue;

            var destination = Path.Combine(target, name);
            if (!File.Exists(destination)) File.Copy(file, destination);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (skip.Contains(name)) continue;

            CopyMissing(directory, Path.Combine(target, name), skip);
        }
    }

    /// <summary>The program folder and its Data folder must both accept a file.</summary>
    private static bool CanRunInPlace(string directory)
    {
        if (!CanWrite(directory)) return false;

        try
        {
            var data = Path.Combine(directory, "Data");
            Directory.CreateDirectory(data);
            return CanWrite(data);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".u7d2d-write-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.WriteByte(0x31);

            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FallbackDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();

        var version = typeof(ReadOnlyInstall).Assembly.GetName().Version?.ToString() ?? "0";
        return Path.Combine(local, "Universal7D2DModManager", $"app-{version}");
    }

    private static void CopyApplication(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (SkipFolders.Any(skip => skip.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

            CopyTree(directory, Path.Combine(target, name));
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static void CleanupOldCopies(string target)
    {
        try
        {
            var parent = Path.GetDirectoryName(target);
            if (parent is null || !Directory.Exists(parent)) return;

            foreach (var directory in Directory.EnumerateDirectories(parent, "app-*"))
            {
                if (SamePath(directory, target)) continue;

                // Data was already merged into the shared folder; deleting the old program copy
                // must not delete a backup that the merge could not read.
                if (!File.Exists(Path.Combine(directory, ".build"))) continue;

                try { Directory.Delete(directory, true); }
                catch (Exception) { }
            }
        }
        catch (Exception)
        {
            // Old copies are a convenience to remove; a running old build keeps its files.
        }
    }

    private static bool SamePath(string left, string right)
    {
        static string Clean(string value) =>
            Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(Clean(left), Clean(right), StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteNote(string source, string target)
    {
        try
        {
            var folder = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(folder);

            File.AppendAllText(
                Path.Combine(folder, "bootstrap.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} read-only folder, running from a copy: {source} -> {target}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception)
        {
            // The copy itself worked; a missing note must not stop the app.
        }
    }
}
