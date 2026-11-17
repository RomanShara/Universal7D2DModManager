using System.Text.RegularExpressions;

namespace U7D2D.Core.Logs;

public sealed class ModMention
{
    public string Name { get; init; } = string.Empty;
    public DateTime? FirstSeen { get; init; }
}

/// <summary>
/// Compares three separate sources without mixing them:
///
/// 1. the current log, which lists the mods the game actually mentioned while loading;
/// 2. the previous log, which lists the mods it mentioned last time;
/// 3. the Mods folder, which is the state right now.
///
/// The folder is not history. It says what is installed at this moment, not what was installed during
/// the previous session, so it is never presented as one. Every list below is labelled with the
/// source it came from.
/// </summary>
public sealed partial class LogComparison
{
    [GeneratedRegex(@"Loading localization from mod:\s*(?<name>[^\s]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LocalizationMod();

    // Greedy on purpose: folder names contain apostrophes, for example "Vayben's Mega Cities".
    // Stopping at the first quote used to report a mod named "Vayben" that does not exist.
    [GeneratedRegex(@"Trying to load from folder:\s*'(?<name>.+)'\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LoadFolder();

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2})T(?<time>\d{2}:\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();

    public bool HasPreviousSession { get; set; }
    public DateTime? FirstError { get; set; }

    /// <summary>Mods named in the current log.</summary>
    public List<ModMention> LoadedCurrent { get; } = new();

    /// <summary>Mods named in the previous log.</summary>
    public List<ModMention> LoadedPrevious { get; } = new();

    /// <summary>Folders present in Mods right now. Current state, not history.</summary>
    public List<string> InstalledNow { get; } = new();

    /// <summary>Mentioned now, not mentioned before.</summary>
    public List<ModMention> NewInCurrent { get; } = new();

    /// <summary>Mentioned before, not mentioned now.</summary>
    public List<string> MissingInCurrent { get; } = new();

    /// <summary>Installed in the folder but never mentioned by the current log.</summary>
    public List<string> InstalledNotLoaded { get; } = new();

    /// <summary>Mentioned by the current log but not present in the folder.</summary>
    public List<string> LoadedNotInstalled { get; } = new();

    /// <summary>
    /// Incremental form of <see cref="Scan"/>. Reading a 50 MB log is the expensive part, so the
    /// parser feeds every line it already reads here and the comparison is built from that pass
    /// instead of opening the same file a second time.
    /// </summary>
    public sealed class ScanState
    {
        private readonly Dictionary<string, DateTime?> _mods =
            new(StringComparer.OrdinalIgnoreCase);

        private DateTime? _firstError;
        private DateTime? _start;

        /// <summary>Consumes one log line. Empty lines and lines without a match are ignored.</summary>
        public void Observe(string line)
        {
            if (line.Length == 0) return;

            DateTime? stamp = null;
            var match = Stamp().Match(line);
            if (match.Success)
            {
                stamp = TryStamp(match.Groups["date"].Value, match.Groups["time"].Value);
                _start ??= stamp;
            }

            var name = MatchModName(line);
            if (name is not null)
            {
                if (!_mods.ContainsKey(name)) _mods[name] = stamp;
                return;
            }

            if (_firstError is null &&
                (line.Contains(" ERR ", StringComparison.Ordinal) ||
                 line.Contains("Exception", StringComparison.Ordinal)))
                _firstError = stamp;
        }

        /// <summary>Snapshot of everything observed so far, safe to hand to the comparison builder.</summary>
        public (Dictionary<string, DateTime?> Mods, DateTime? FirstError, DateTime? Start) Build() =>
            (new Dictionary<string, DateTime?>(_mods, StringComparer.OrdinalIgnoreCase),
             _firstError, _start);
    }

    /// <summary>Reads one log and returns the mods it names, with the first time each was seen.</summary>
    public static (Dictionary<string, DateTime?> Mods, DateTime? FirstError, DateTime? Start) Scan(
        IEnumerable<string> lines, CancellationToken ct = default)
    {
        var state = new ScanState();
        var index = 0;

        foreach (var line in lines)
        {
            if ((++index & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            state.Observe(line);
        }

        return state.Build();
    }

    private static string? MatchModName(string line)
    {
        var localization = LocalizationMod().Match(line);
        if (localization.Success) return localization.Groups["name"].Value.Trim();

        var folder = LoadFolder().Match(line);
        if (folder.Success)
        {
            var name = folder.Groups["name"].Value.Trim();
            return name.Length == 0 ? null : name;
        }

        return null;
    }

    private static DateTime? TryStamp(string date, string time)
    {
        if (date.Length != 10 || time.Length != 8) return null;

        try
        {
            return new DateTime(
                int.Parse(date.AsSpan(0, 4)), int.Parse(date.AsSpan(5, 2)), int.Parse(date.AsSpan(8, 2)),
                int.Parse(time.AsSpan(0, 2)), int.Parse(time.AsSpan(3, 2)), int.Parse(time.AsSpan(6, 2)),
                DateTimeKind.Unspecified);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Builds the comparison. The installed list is optional and kept strictly separate.</summary>
    public static LogComparison Build(
        Dictionary<string, DateTime?> current,
        Dictionary<string, DateTime?>? previous,
        DateTime? firstError,
        IEnumerable<string>? installed = null,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        var result = new LogComparison
        {
            HasPreviousSession = previous is not null,
            FirstError = firstError
        };

        foreach (var (name, seen) in current)
            result.LoadedCurrent.Add(new ModMention { Name = name, FirstSeen = seen });

        result.LoadedCurrent.Sort((a, b) =>
            (a.FirstSeen ?? DateTime.MinValue).CompareTo(b.FirstSeen ?? DateTime.MinValue));

        if (previous is not null)
        {
            foreach (var (name, seen) in previous)
                result.LoadedPrevious.Add(new ModMention { Name = name, FirstSeen = seen });

            foreach (var mention in result.LoadedCurrent)
                if (!previous.ContainsKey(mention.Name)) result.NewInCurrent.Add(mention);

            foreach (var name in previous.Keys)
                if (!current.ContainsKey(name)) result.MissingInCurrent.Add(name);

            result.MissingInCurrent.Sort(StringComparer.OrdinalIgnoreCase);
        }

        if (installed is null) return result;

        var installedSet = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        result.InstalledNow.AddRange(installedSet.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        // The log prints "CasesMod" while the folder is "Zzzzzzzzzzzzzzzz_CasesMod", so a plain
        // string comparison would report half the collection as missing. Names are matched through
        // the same prefix-stripping rules the analyzer uses.
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in current.Keys)
        {
            var folder = installedSet.FirstOrDefault(f => Matches(name, f));

            if (folder is null && aliases is not null)
            {
                // The log can name an assembly or the internal name from ModInfo.xml instead of the
                // folder. Both still mean the mod is installed, and the folder must be marked as
                // loaded so it does not show up in the opposite list.
                folder = aliases
                    .Where(pair => Matches(name, pair.Key))
                    .Select(pair => pair.Value)
                    .FirstOrDefault();
            }

            if (folder is null) result.LoadedNotInstalled.Add(name);
            else matched.Add(folder);
        }

        foreach (var folder in installedSet)
            if (!matched.Contains(folder)) result.InstalledNotLoaded.Add(folder);

        result.InstalledNotLoaded.Sort(StringComparer.OrdinalIgnoreCase);
        result.LoadedNotInstalled.Sort(StringComparer.OrdinalIgnoreCase);

        return result;
    }

    /// <summary>True when the log name and the folder name denote the same mod.</summary>
    private static bool Matches(string logName, string folder)
    {
        var wanted = Loose(logName);

        if (Loose(folder) == wanted) return true;

        return Candidates(folder).Any(c => Loose(c) == wanted);
    }

    /// <summary>
    /// Letters and digits only, lower case. The log writes "MazriqTruck" where the folder is
    /// "Mazriq_Truck", and "CasesMod" for "Zzzzzzzzzzzzzzzz_CasesMod"; separators are noise here.
    /// </summary>
    private static string Loose(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));

        return builder.ToString();
    }

    /// <summary>Folder names with load-order prefixes removed, the same rules the analyzer uses.</summary>
    private static IEnumerable<string> Candidates(string folder)
    {
        yield return folder;

        var noDigits = folder.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-', '_', ' ', '.');
        if (noDigits.Length >= 4 && noDigits.Length != folder.Length) yield return noDigits;

        var noPadding = folder.TrimStart('z', 'Z', 'x', 'X', '0', '-', '_', ' ', '.');
        if (noPadding.Length >= 4 && noPadding.Length != folder.Length) yield return noPadding;

        if (folder.Contains('_')) yield return folder.Replace('_', ' ');
    }

    /// <summary>Minutes between the first sighting of a mod and the first error, or null when unknown.</summary>
    public static int? MinutesBefore(ModMention mention, DateTime? firstError)
    {
        if (mention.FirstSeen is null || firstError is null) return null;

        var delta = (firstError.Value - mention.FirstSeen.Value).TotalMinutes;
        return delta < 0 ? null : (int)Math.Round(delta);
    }
}
