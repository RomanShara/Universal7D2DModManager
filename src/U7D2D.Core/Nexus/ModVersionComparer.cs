using System.Text.RegularExpressions;

namespace U7D2D.Core.Nexus;

public enum VersionRelation
{
    Equal,
    LeftNewer,
    RightNewer,
    Inconclusive
}

/// <summary>
/// Compares mod version strings without producing false "update available" badges.
/// The old code used plain string inequality, so "1.0" vs "1.0b" or "v2" vs "2.0"
/// was reported as an update even when it wasn't. Anything ambiguous is reported as
/// Inconclusive and the UI asks the player to check the page manually.
/// </summary>
public static partial class ModVersionComparer
{
    [GeneratedRegex(@"^\s*[vV]?(\d+(?:\.\d+)*)(?:[\s.\-_]*([A-Za-z]+)\s*\d*)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    public static VersionRelation Compare(string? left, string? right)
    {
        var l = (left ?? string.Empty).Trim();
        var r = (right ?? string.Empty).Trim();

        if (l.Length == 0 || r.Length == 0) return VersionRelation.Inconclusive;
        if (string.Equals(l, r, StringComparison.OrdinalIgnoreCase)) return VersionRelation.Equal;

        if (!TryParse(l, out var lNumbers, out var lSuffix) ||
            !TryParse(r, out var rNumbers, out var rSuffix))
            return VersionRelation.Inconclusive;

        var length = Math.Max(lNumbers.Count, rNumbers.Count);
        for (var i = 0; i < length; i++)
        {
            var a = i < lNumbers.Count ? lNumbers[i] : 0;
            var b = i < rNumbers.Count ? rNumbers[i] : 0;
            if (a > b) return VersionRelation.LeftNewer;
            if (a < b) return VersionRelation.RightNewer;
        }

        // "1.0" vs "1.0b" — deliberately inconclusive rather than guessing.
        if (!string.Equals(lSuffix, rSuffix, StringComparison.OrdinalIgnoreCase))
            return VersionRelation.Inconclusive;

        return VersionRelation.Equal;
    }

    /// <summary>True only when <paramref name="candidate"/> is unambiguously newer.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        Compare(candidate, current) == VersionRelation.LeftNewer;

    public static bool IsInconclusive(string? candidate, string? current) =>
        Compare(candidate, current) == VersionRelation.Inconclusive;

    private static bool TryParse(string value, out List<int> numbers, out string suffix)
    {
        numbers = new List<int>();
        suffix = string.Empty;

        var match = VersionRegex().Match(value);
        if (!match.Success) return false;

        foreach (var part in match.Groups[1].Value.Split('.'))
        {
            if (!int.TryParse(part, out var number)) return false;
            numbers.Add(number);
        }

        suffix = match.Groups[2].Value;
        return numbers.Count > 0;
    }
}
