using System.IO;

namespace U7D2D.Core.Logs;

public sealed class GameLogFile
{
    public string Path { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public DateTime ModifiedUtc { get; init; }
    public long Size { get; init; }
}

/// <summary>
/// Finds the logs the game leaves behind. 7 Days to Die is a Unity game, so the main file is
/// Player.log in LocalLow; repacks and older builds also drop output_log*.txt next to the executable.
/// </summary>
public static class GameLogLocator
{
    public static List<GameLogFile> Locate(string? gamePath)
    {
        var found = new List<GameLogFile>();

        foreach (var folder in CandidateFolders(gamePath))
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

            Add(found, System.IO.Path.Combine(folder, "Player.log"), "Player.log");
            Add(found, System.IO.Path.Combine(folder, "Player-prev.log"), "previous session");

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "output_log*.txt"))
                    Add(found, file, "output log");
            }
            catch (Exception)
            {
                // A folder we cannot read is simply skipped.
            }
        }

        return found
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(f => f.ModifiedUtc)
            .ToList();
    }

    private static IEnumerable<string> CandidateFolders(string? gamePath)
    {
        var localLow = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localLow))
        {
            // LocalApplicationData points at ...\AppData\Local, Unity writes to ...\AppData\LocalLow.
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(localLow, "..", "LocalLow"));
            yield return System.IO.Path.Combine(root, "The Fun Pimps", "7 Days To Die");
        }

        if (!string.IsNullOrWhiteSpace(gamePath)) yield return gamePath;
    }

    private static void Add(List<GameLogFile> list, string path, string label)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return;

            list.Add(new GameLogFile
            {
                Path = info.FullName,
                Label = label,
                ModifiedUtc = info.LastWriteTimeUtc,
                Size = info.Length
            });
        }
        catch (Exception)
        {
            // Locked or inaccessible file, nothing to add.
        }
    }
}
