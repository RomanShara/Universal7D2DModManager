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
        return settings;
    }

    public void Save(AppSettings settings)
    {
        _db.Execute(
            "INSERT INTO Settings (Key, Value) VALUES ($key, $value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            ("key", Key), ("value", JsonConfig.Serialize(settings)));
    }
}
