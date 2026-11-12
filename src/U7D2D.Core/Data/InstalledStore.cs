using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Data;

/// <summary>Remembers which mod folders the manager installed, so updates and origins survive restarts.</summary>
public sealed class InstalledStore
{
    private readonly Database _db;

    public InstalledStore(Database db) => _db = db;

    public Dictionary<string, InstalledMod> LoadAll()
    {
        var result = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        var rows = _db.Query("SELECT FolderName, Payload FROM InstalledMods", r => (Folder: r.GetString(0), Payload: r.GetString(1)));
        foreach (var (folder, payload) in rows)
        {
            var mod = JsonConfig.Deserialize<InstalledMod>(payload);
            if (mod is not null) result[folder] = mod;
        }
        return result;
    }

    public void Upsert(InstalledMod mod)
    {
        _db.Execute("""
            INSERT INTO InstalledMods (FolderName, FullPath, Name, Version, ContentHash, SourceArchive, InstalledUtc, Payload)
            VALUES ($folder, $full, $name, $version, $hash, $source, $installed, $payload)
            ON CONFLICT(FolderName) DO UPDATE SET
                FullPath = excluded.FullPath,
                Name = excluded.Name,
                Version = excluded.Version,
                ContentHash = excluded.ContentHash,
                SourceArchive = excluded.SourceArchive,
                InstalledUtc = excluded.InstalledUtc,
                Payload = excluded.Payload
            """,
            ("folder", mod.FolderName),
            ("full", mod.FullPath),
            ("name", mod.Info.EffectiveName),
            ("version", mod.Info.Version),
            ("hash", mod.ContentHash),
            ("source", mod.SourceArchive),
            ("installed", Database.ToDb(mod.InstalledUtc ?? DateTime.UtcNow)),
            ("payload", JsonConfig.Serialize(mod)));
    }

    public void Remove(string folderName) =>
        _db.Execute("DELETE FROM InstalledMods WHERE FolderName = $folder", ("folder", folderName));

    public void Clear() => _db.Execute("DELETE FROM InstalledMods");
}
