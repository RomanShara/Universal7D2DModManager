using System.IO;
using System.Text;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.App.Services;

/// <summary>
/// Writes a single text file a user can attach to a bug report: environment, paths, what the manager
/// sees, and the last operations. Nobody should have to reproduce a problem from a screenshot.
/// </summary>
public static class Diagnostics
{
    public static string Save(
        AppPaths paths,
        AppSettings settings,
        GameInstallInfo? game,
        IEnumerable<string> installedMods,
        IEnumerable<OperationLogEntry> log,
        int archivesScanned,
        int archivesFailed)
    {
        Directory.CreateDirectory(paths.LogDirectory);

        var name = $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var file = Path.Combine(paths.LogDirectory, name);

        var text = new StringBuilder();

        text.AppendLine("Universal 7 Days to Die Mod Manager, diagnostics");
        text.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine();

        text.AppendLine("[Application]");
        text.AppendLine($"Version: {typeof(Diagnostics).Assembly.GetName().Version}");
        text.AppendLine($"OS: {Environment.OSVersion}");
        text.AppendLine($"Runtime: {Environment.Version}");
        text.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
        text.AppendLine($"Interface language: {Localization.ToCode(new Localization().Current)}");
        text.AppendLine();

        text.AppendLine("[Folders]");
        text.AppendLine($"Root: {paths.Root}");
        text.AppendLine($"Game: {settings.GamePath}");
        text.AppendLine($"Mods: {settings.ModsPath}");
        text.AppendLine($"Backups: {settings.BackupsPath}");
        text.AppendLine($"Archive folders: {settings.ArchivePaths.Count}");
        text.AppendLine();

        text.AppendLine("[Game]");
        if (game is null)
        {
            text.AppendLine("Not probed.");
        }
        else
        {
            text.AppendLine($"Detected: {game.IsValid}");
            text.AppendLine($"Version: {game.VersionString ?? "not detected"}");
            text.AppendLine($"Version source: {game.VersionSource ?? "none"}");
            foreach (var problem in game.Problems) text.AppendLine($"Problem: {problem}");
        }

        text.AppendLine();

        text.AppendLine("[State]");
        text.AppendLine($"Installed mods: {installedMods.Count()}");
        text.AppendLine($"Archives scanned: {archivesScanned}, failed: {archivesFailed}");
        text.AppendLine($"Nexus key set: {(string.IsNullOrWhiteSpace(settings.NexusApiKey) ? "no" : "yes")}");
        text.AppendLine();

        text.AppendLine("[Installed mods]");
        foreach (var mod in installedMods.Take(200)) text.AppendLine(mod);
        text.AppendLine();

        text.AppendLine("[Last operations]");
        foreach (var entry in log.Take(80))
        {
            var details = string.IsNullOrWhiteSpace(entry.Details) ? string.Empty : $" | {entry.Details}";
            text.AppendLine($"{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} [{entry.Kind}] {entry.Message}{details}");
        }

        var crashes = LatestCrash(paths.LogDirectory);
        if (crashes is not null)
        {
            text.AppendLine();
            text.AppendLine($"[Last crash] {Path.GetFileName(crashes)}");
            foreach (var line in File.ReadLines(crashes).TakeLast(60)) text.AppendLine(line);
        }

        File.WriteAllText(file, text.ToString(), new UTF8Encoding(false));
        return file;
    }

    /// <summary>Appends an unhandled exception to logs/crash-*.log so it survives the dialog.</summary>
    public static string? SaveCrash(AppPaths paths, Exception exception, string source)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            var file = Path.Combine(paths.LogDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            File.WriteAllText(
                file,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {source}{Environment.NewLine}{exception}{Environment.NewLine}",
                new UTF8Encoding(false));

            return file;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? LatestCrash(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "crash-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
