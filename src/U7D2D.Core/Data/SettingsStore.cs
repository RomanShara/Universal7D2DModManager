using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Data;

public sealed class SettingsStore
{
    private const string Key = "app.settings";
    private readonly Database _db;

    public SettingsStore(Database db) => _db = db;

    public AppSettings Load()
    {
        var json = _db.Scalar<string>("SELECT Value FROM Settings WHERE Key = $key", ("key", Key));
        var settings = JsonConfig.Deserialize<AppSettings>(json) ?? new AppSettings();
        settings.ArchivePaths ??= new List<string>();
        if (settings.MaxScanDepth <= 0) settings.MaxScanDepth = 12;
        if (string.IsNullOrWhiteSpace(settings.NexusGameDomain))
            settings.NexusGameDomain = "7daystodie";

        Normalize(settings);

        // Legacy: older builds stored a personal Nexus API key, which the AUP no longer allows.
        // AppSettings no longer has the property, so a save rewrites the JSON without it.
        if (!string.IsNullOrEmpty(json) && json.Contains("\"NexusApiKey\"", StringComparison.Ordinal))
            Save(settings);

        return settings;
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);

        _db.Execute(
            "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            ("key", Key), ("value", JsonConfig.Serialize(settings)));
    }

    /// <summary>
    /// Stored paths are always full and real: no relative fragments, no trailing separators and no
    /// junction indirection. A user who types "..\Mods" or moves the folder between drives keeps a
    /// path that still points at the same place.
    /// </summary>
    private static void Normalize(AppSettings settings)
    {
        settings.GamePath = PathSafety.ResolveRealPath(settings.GamePath);
        settings.ModsPath = PathSafety.ResolveRealPath(settings.ModsPath);
        settings.BackupsPath = PathSafety.NormalizeDirectory(settings.BackupsPath);

        settings.ArchivePaths = settings.ArchivePaths
            .Select(PathSafety.NormalizeDirectory)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
