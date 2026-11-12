using System.Security.Cryptography;
using System.Text;

namespace U7D2D.Core.Infrastructure;

public static class HashUtil
{
    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Fast structural fingerprint of a directory: relative path + size + last write ticks.
    /// Detects added/removed/changed files without reading every byte.
    /// </summary>
    public static string FingerprintDirectory(string root)
    {
        if (!Directory.Exists(root)) return string.Empty;

        var sb = new StringBuilder();
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .OrderBy(x => x.Rel, StringComparer.OrdinalIgnoreCase);

        foreach (var (full, rel) in files)
        {
            var info = new FileInfo(full);
            sb.Append(rel).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }

        return Sha256Hex(sb.ToString());
    }
}
