using U7D2D.Core.I18n;

namespace U7D2D.Core.Infrastructure;

public static class FileOps
{
    /// <summary>Moves a directory, falling back to a recursive copy when source and target are on different volumes.</summary>
    public static void MoveDirectory(string source, string target)
    {
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            CopyDirectory(source, target);
            Directory.Delete(source, true);
        }
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.Copy(file, destination, overwrite: true);
        }
    }

    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    public static int CountFiles(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try { return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caller decides how to surface the failure.
        }
    }

    /// <summary>Deletes a directory and returns false if it still exists afterwards.</summary>
    public static bool DeleteDirectory(string path)
    {
        TryDeleteDirectory(path);
        return !Directory.Exists(path);
    }

    public static string FormatSize(long bytes)
    {
        string[] units =
        [
            Localization.S("io.unitB"), Localization.S("io.unitKb"), Localization.S("io.unitMb"),
            Localization.S("io.unitGb"), Localization.S("io.unitTb")
        ];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
