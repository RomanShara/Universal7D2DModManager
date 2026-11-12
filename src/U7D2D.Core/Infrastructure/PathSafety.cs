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
}
