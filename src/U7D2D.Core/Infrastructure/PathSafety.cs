namespace U7D2D.Core.Infrastructure;

public static class PathSafety
{
    private static readonly char[] InvalidFolderChars =
        Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

    /// <summary>Normalizes an archive entry key to forward slashes without leading separators.</summary>
    public static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var value = key.Replace('\\', '/').Trim();
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        value = value.TrimStart('/');
        while (value.Contains("//", StringComparison.Ordinal)) value = value.Replace("//", "/", StringComparison.Ordinal);
        return value;
    }

    public static bool IsUnderRoot(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Combines a destination folder with a relative archive path, rejecting traversal attempts.</summary>
    public static string? SafeCombine(string destinationRoot, string relativePath)
    {
        var rel = NormalizeKey(relativePath);
        if (string.IsNullOrWhiteSpace(rel)) return null;
        if (rel.Contains("..", StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(rel)) return null;
        if (rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

        var combined = Path.GetFullPath(Path.Combine(destinationRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        return IsUnderRoot(destinationRoot, combined) ? combined : null;
    }

    public static string SanitizeFolderName(string? name, string fallback = "Mod")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var cleaned = new string(name.Trim().Where(c => !InvalidFolderChars.Contains(c)).ToArray()).Trim();
        cleaned = cleaned.TrimEnd('.');
        if (cleaned.Length == 0) return fallback;
        if (cleaned.Length > 96) cleaned = cleaned[..96].Trim();
        return cleaned;
    }

    public static bool LooksLikeUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Full path without a trailing separator. Relative paths are resolved against the process
    /// working directory, so a saved "Mods" never depends on where the app was started from.
    /// </summary>
    public static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var value = path.Trim().Trim('"');
        if (value.Length == 0) return string.Empty;

        try
        {
            var full = Path.GetFullPath(value);
            var root = Path.GetPathRoot(full);

            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return value;
        }
    }

    /// <summary>
    /// Where the directory really lives. A junction or symlink is followed to its final target so
    /// an install writes into the real folder instead of moving the link itself.
    /// </summary>
    public static string ResolveRealPath(string? path)
    {
        var full = NormalizeDirectory(path);
        if (full.Length == 0) return full;

        try
        {
            var info = new DirectoryInfo(full);
            if (!info.Exists) return full;

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? full : NormalizeDirectory(target.FullName);
        }
        catch (Exception)
        {
            return full;
        }
    }

    /// <summary>True when the folder exists or can be created and a file can actually be written into it.</summary>
    public static bool IsDirectoryWritable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var directory = NormalizeDirectory(path);
            if (directory.Length == 0) return false;

            Directory.CreateDirectory(directory);

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
}
