using System.IO;
using System.Text.Json;

namespace U7D2D.Core.Logs;

/// <summary>
/// Disk cache for the per-DLL metadata read by the log analysis: type names and Harmony targets.
///
/// Reading every attribute of every mod DLL costs about twenty seconds on a large collection, and
/// the answer only changes when a file changes. Entries are keyed by path and validated by size and
/// last write time, so an updated mod is re-read while the rest come from the cache.
/// </summary>
public sealed class DllCache
{
    private sealed class Entry
    {
        public long Size { get; set; }
        public long Ticks { get; set; }
        public List<string> Types { get; set; } = new();
        public List<string> PatchTypes { get; set; } = new();
        public List<string> PatchMembers { get; set; } = new();
        public int Skipped { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries;

    private DllCache(Dictionary<string, Entry> entries)
    {
        _entries = entries;
    }

    public static DllCache Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var json = File.ReadAllText(file);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json);
                if (parsed is not null)
                    return new DllCache(new Dictionary<string, Entry>(parsed, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception)
        {
            // A broken cache is not a problem: everything is simply read again.
        }

        return new DllCache(new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
    }

    public bool TryGet(
        string path, long size, DateTime modifiedUtc,
        out List<string> types, out List<HarmonyTargetReader.Target> patches) =>
        TryGet(path, size, modifiedUtc, out types, out patches, out _);

    public bool TryGet(
        string path, long size, DateTime modifiedUtc,
        out List<string> types, out List<HarmonyTargetReader.Target> patches, out int skipped)
    {
        types = new List<string>();
        patches = new List<HarmonyTargetReader.Target>();
        skipped = 0;

        // Mods are indexed in parallel, so reads and writes reach the dictionary from several
        // threads at once. A plain Dictionary cannot be read while another thread writes it.
        Entry? entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(path, out var found)) return false;
            entry = found;
        }

        if (entry is null || entry.Size != size || entry.Ticks != modifiedUtc.Ticks) return false;

        types = entry.Types;
        skipped = entry.Skipped;

        for (var i = 0; i < entry.PatchTypes.Count; i++)
        {
            patches.Add(new HarmonyTargetReader.Target
            {
                DeclaringType = entry.PatchTypes[i],
                MemberName = i < entry.PatchMembers.Count ? entry.PatchMembers[i] : string.Empty
            });
        }

        return true;
    }

    public void Set(
        string path, long size, DateTime modifiedUtc,
        List<string> types, List<HarmonyTargetReader.Target> patches, int skipped = 0)
    {
        var entry = new Entry
        {
            Size = size,
            Ticks = modifiedUtc.Ticks,
            Types = types,
            Skipped = skipped
        };

        foreach (var patch in patches)
        {
            entry.PatchTypes.Add(patch.DeclaringType);
            entry.PatchMembers.Add(patch.MemberName);
        }

        lock (_entries) _entries[path] = entry;
    }

    public void Save(string file)
    {
        try
        {
            var folder = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            // Snapshot under the same lock the writers take, so serialization never walks a
            // dictionary that is being modified.
            Dictionary<string, Entry> snapshot;
            lock (_entries) snapshot = new Dictionary<string, Entry>(_entries);

            var json = JsonSerializer.Serialize(snapshot);
            var temp = file + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception)
        {
            // Losing the cache only costs time on the next run.
        }
    }
}
