using System.IO;

namespace U7D2D.Core.Nexus;

/// <summary>
/// Nexus download names carry the mod id and, right after it, the version of the downloaded file:
/// "Onsoku 12249 3.4 2026-11-10T03-48Z EjnRsi6xY.zip". The archive library keeps those names, so the
/// build the player already downloaded can be recovered without a single request.
///
/// Only space separated names are read. A hyphenated name puts the file id after the mod id, which
/// looks like a version, so such names are left alone instead of guessing.
/// </summary>
public static class NexusArchiveName
{
    public static bool TryRead(string? archivePath, out int modId, out string version)
    {
        modId = 0;
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(archivePath)) return false;

        var name = Path.GetFileNameWithoutExtension(archivePath);
        if (string.IsNullOrWhiteSpace(name)) return false;

        var tokens = name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!int.TryParse(tokens[i], out var id) || id < 10 || id > 9_999_999) continue;

            var candidate = tokens[i + 1].Trim();
            if (!LooksLikeVersion(candidate)) continue;

            modId = id;
            version = candidate;
            return true;
        }

        return false;
    }

    private static bool LooksLikeVersion(string text)
    {
        if (text.Length is 0 or > 24) return false;
        if (text[0] is 'v' or 'V') text = text[1..];
        if (text.Length == 0 || !char.IsDigit(text[0])) return false;

        var dots = 0;
        foreach (var ch in text)
        {
            if (ch == '.')
            {
                dots++;
                continue;
            }

            if (!char.IsDigit(ch) && !char.IsLetter(ch)) return false;
        }

        return dots <= 3;
    }
}
