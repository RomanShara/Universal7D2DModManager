using System.IO;
using System.Text;

namespace U7D2D.Core.Logs;

/// <summary>
/// Reads a log file line by line. Long sessions produce files with hundreds of thousands of lines,
/// and only the tail matters, so anything above the limit is read from the end.
/// </summary>
public static class GameLogReader
{
    private const long TailLimitBytes = 32L * 1024 * 1024;

    public static IEnumerable<string> ReadLines(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return Array.Empty<string>();

        if (info.Length <= TailLimitBytes)
            return File.ReadLines(path, Encoding.UTF8);

        return ReadTail(path, info.Length - TailLimitBytes);
    }

    private static IEnumerable<string> ReadTail(string path, long from)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(from, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        // Skip the first partial line, it starts in the middle of a record.
        reader.ReadLine();

        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }
}
