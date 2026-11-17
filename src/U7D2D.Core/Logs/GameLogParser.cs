using System.IO;
using System.Text.RegularExpressions;

namespace U7D2D.Core.Logs;

/// <summary>
/// Parses Unity player logs as 7 Days to Die writes them. Two kinds of lines matter:
/// timestamped engine messages, and exception blocks that Unity prints without a timestamp
/// followed by "  at Type.Method (...)" frames.
/// </summary>
public static partial class GameLogParser
{
    [GeneratedRegex(
        // The [tag] is optional: many engine lines are written as "ERR XML loader: ... failed".
        // Making the bracket group optional keeps such a line in Message instead of swallowing the
        // whole text into Tag, which used to hide warnings from the relevance filter.
        @"^(?<date>\d{4}-\d{2}-\d{2})T(?<time>\d{2}:\d{2}:\d{2})\s+(?<uptime>[\d.]+)\s+(?<level>INF|WRN|ERR|LOG|EXC)\s+(?:\[(?<tag>[^\]]*)\]\s*)?(?<msg>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EngineLine();

    [GeneratedRegex(
        @"^(?<type>[A-Za-z_][A-Za-z0-9_.]*(?:Exception|Error))\s*:?\s*(?<msg>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionLine();

    [GeneratedRegex(
        // Unity writes the exception type at the start of an EXC message. The colon is required
        // here: a message that merely contains a word ending in "Exception" is not a typed header.
        @"^(?<type>[A-Za-z_][A-Za-z0-9_.]*(?:Exception|Error)):\s*",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionTypePrefix();

    [GeneratedRegex(@"^\s*at\s+(?<sig>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FrameLine();

    // "in <hash>:0" carries the assembly hash Unity prints instead of a name, and the trailing
    // ":0" is a file offset. Both are noise for the report.
    [GeneratedRegex(@"\s+in\s+<(?<asm>[^>]*)>:\d+", RegexOptions.CultureInvariant)]
    private static partial Regex AssemblyHint();

    [GeneratedRegex(@"\s*\[0x[0-9a-fA-F]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex ILOffset();

    // The parameter list may hold nested generics and dots, so it is cut from the first bracket.
    [GeneratedRegex(@"\s*\(.*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterList();

    [GeneratedRegex(@"<[^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex GenericArgs();

    public static List<LogEntry> Parse(IEnumerable<string> lines, out int linesRead)
        => Parse(lines, out linesRead, null, CancellationToken.None);

    /// <summary>
    /// Streaming parse. <paramref name="keep"/> is applied when a block is finished, so noise never
    /// reaches the list and a 50 MB log does not turn into a hundred thousand objects.
    /// <paramref name="lineObserver"/> receives every line that was read, including the ones the
    /// parser ignores, so the same file can be scanned for mod names in the same pass.
    /// </summary>
    public static List<LogEntry> Parse(
        IEnumerable<string> lines,
        out int linesRead,
        Predicate<LogEntry>? keep,
        CancellationToken ct,
        Action<string>? lineObserver = null)
    {
        var entries = new List<LogEntry>();
        var count = 0;

        var pendingFrames = new List<StackFrame>();
        var pendingLine = 0;
        string? pendingType = null;     // set only for exception headers, null for ERR blocks
        var pendingTag = string.Empty;
        var pendingMessage = new System.Text.StringBuilder();
        DateTime? pendingTime = null;

        void Flush()
        {
            // Nothing is being collected. Frames without any header cannot be attributed to
            // anything, and an empty card is worse than no card, so they are dropped here rather
            // than leaking into the next block.
            if (pendingType is null && pendingMessage.Length == 0)
            {
                pendingFrames = new List<StackFrame>();
                pendingTag = string.Empty;
                return;
            }

            var entry = new LogEntry
            {
                Severity = LogSeverity.Error,
                Time = pendingTime,
                Tag = pendingTag,
                ExceptionType = pendingType,
                Message = pendingMessage.ToString().Trim(),
                LineNumber = pendingLine
            };

            entry.Frames.AddRange(pendingFrames);
            if (keep is null || keep(entry)) entries.Add(entry);

            pendingType = null;
            pendingTag = string.Empty;
            pendingFrames = new List<StackFrame>();
            pendingMessage.Clear();
        }

        // The exception type belongs in ExceptionType, not in the message: the card title and the
        // grouping both use it, and keeping the prefix would show it twice.
        void TakeExceptionType()
        {
            var typed = ExceptionTypePrefix().Match(pendingMessage.ToString());
            if (!typed.Success) return;

            pendingType = typed.Groups["type"].Value;
            pendingMessage.Remove(0, typed.Length);
        }

        foreach (var raw in lines)
        {
            count++;

            // Checked every line: the loop is cheap and a cancellation must land quickly.
            if ((count & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            var line = raw.TrimEnd('\r');
            lineObserver?.Invoke(line);
            if (line.Length == 0) continue;

            // Cheap dispatch before any regex: three regex calls per line is what made a 50 MB log
            // take a minute. The first character already tells what the line is.
            var first = line[0];

            if (first == ' ' && line.Length > 3 && line[1] == ' ' && line[2] == 'a' && line[3] == 't')
            {
                var frame = FrameLine().Match(line);
                if (!frame.Success) { Flush(); continue; }

                var sig = frame.Groups["sig"].Value;
                var asm = AssemblyHint().Match(sig);

                if (asm.Success) sig = sig.Replace(asm.Value, string.Empty).Trim();
                sig = ILOffset().Replace(sig, string.Empty).Trim();
                sig = GenericArgs().Replace(sig, string.Empty).Trim();
                sig = ParameterList().Replace(sig, string.Empty).Trim();

                // "(wrapper dynamic-method) DialogFromXml.DMD<...>" loses everything to the
                // generic, parameter and assembly cleanups above. Keeping it produced rows of
                // "at " with no name, which is formatting noise, not a stack frame.
                if (sig.Length == 0) continue;

                var (typeName, member) = SplitSignature(sig);

                pendingFrames.Add(new StackFrame
                {
                    Index = pendingFrames.Count,
                    TypeName = typeName,
                    MemberName = member,
                    AssemblyHint = asm.Success ? asm.Groups["asm"].Value : null
                });
                continue;
            }

            if (first == ' ' || first == '\t')
            {
                // Continuation of an exception message.
                if (pendingType is not null)
                {
                    if (pendingMessage.Length > 0) pendingMessage.Append(' ');
                    pendingMessage.Append(line.Trim());
                }
                continue;
            }

            if (char.IsDigit(first))
            {
                var engine = EngineLine().Match(line);
                if (!engine.Success) { Flush(); continue; }

                Flush();

                var levelToken = engine.Groups["level"].Value;
                var level = levelToken switch
                {
                    "WRN" => LogSeverity.Warning,
                    "ERR" or "EXC" => LogSeverity.Error,
                    _ => LogSeverity.Info
                };

                // Both "ERR" and "EXC" are headers: when a stack follows, its frames belong to
                // them. Emitting ERR at once used to drop every frame, because no block was open
                // and the bare "at ..." rows were discarded as unattributable. A following
                // exception header flushes this block first, so the two entries stay separate and
                // the exception keeps its own stack.
                if (levelToken is "ERR" or "EXC")
                {
                    pendingLine = count;
                    // The tag is the ERR line's subject ("XML loader", "XUi") and becomes the card
                    // title. Exception headers never used it: their title is the exception type,
                    // or the message when the type could not be parsed.
                    pendingTag = levelToken == "ERR" ? engine.Groups["tag"].Value.Trim() : string.Empty;
                    pendingTime = ParseStamp(
                        engine.Groups["date"].Value, engine.Groups["time"].Value);
                    pendingMessage.Append(engine.Groups["msg"].Value.Trim());

                    if (levelToken == "EXC") TakeExceptionType();

                    continue;
                }

                var entry = new LogEntry
                {
                    Severity = level,
                    Tag = engine.Groups["tag"].Value.Trim(),
                    Message = engine.Groups["msg"].Value.Trim(),
                    LineNumber = count
                };

                // Timestamp parsing and the allocation itself only matter for entries that survive
                // the filter. On a 50 MB log this skips roughly half a million parses.
                if (keep is null || keep(entry))
                {
                    entry.Time = ParseStamp(
                        engine.Groups["date"].Value, engine.Groups["time"].Value);

                    entries.Add(entry);
                }

                continue;
            }

            // Anything else is either an exception header or engine chatter.
            if (!line.Contains("Exception", StringComparison.Ordinal) &&
                !line.Contains("Error", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            var exception = ExceptionLine().Match(line);
            if (exception.Success)
            {
                Flush();
                pendingLine = count;
                pendingType = exception.Groups["type"].Value;
                pendingMessage.Append(exception.Groups["msg"].Value);
                pendingTime = null;
                continue;
            }

            // Continuation of an exception message (Unity wraps long messages over several lines).
            if (pendingType is not null && line.StartsWith(' ') && !line.TrimStart().StartsWith("at "))
            {
                if (pendingMessage.Length > 0) pendingMessage.Append(' ');
                pendingMessage.Append(line.Trim());
                continue;
            }

            // Anything else closes the block and is ignored as engine chatter.
            Flush();
        }

        Flush();
        linesRead = count;
        return entries;
    }

    /// <summary>Fast parse of the "2026-11-12" and "23:04:29" groups without culture lookups.</summary>
    private static DateTime? ParseStamp(string date, string time)
    {
        if (date.Length != 10 || time.Length != 8) return null;

        try
        {
            var year = int.Parse(date.AsSpan(0, 4));
            var month = int.Parse(date.AsSpan(5, 2));
            var day = int.Parse(date.AsSpan(8, 2));
            var hour = int.Parse(time.AsSpan(0, 2));
            var minute = int.Parse(time.AsSpan(3, 2));
            var second = int.Parse(time.AsSpan(6, 2));

            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// "Gravebound.Vault.UI.VaultUiBootstrap.Init" becomes type "Gravebound.Vault.UI.VaultUiBootstrap"
    /// and member "Init". Nested types use '+', generics were stripped earlier.
    /// </summary>
    internal static (string Type, string Member) SplitSignature(string signature)
    {
        var dot = signature.LastIndexOf('.');
        if (dot <= 0) return (signature, string.Empty);

        // Unity prints Type.Method. The last segment is the method even when it is PascalCase,
        // so there is no reliable way to tell "Namespace.Type" from "Type.Method" other than
        // taking the last dot as the separator. The index also retries without the member.
        return (signature[..dot], signature[(dot + 1)..]);
    }
}
