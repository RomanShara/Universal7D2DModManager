using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Data;

/// <summary>
/// Persists manual mod→Nexus bindings and the last check result for each installed mod.
/// The results are cached so a restart shows the previous verdict without a single request
/// (the player chose "check only by button", there is no background polling).
/// </summary>
public sealed class NexusStore
{
    private readonly Database _db;

    public NexusStore(Database db) => _db = db;

    public Dictionary<string, NexusLink> GetLinks() =>
        _db.Query(
            "SELECT FolderName, ModId, GameDomain, IsManual, Source FROM NexusLinks",
            r => new NexusLink
            {
                FolderName = r.GetString(0),
                ModId = r.GetInt32(1),
                GameDomain = r.GetString(2),
                IsManual = r.GetInt64(3) != 0,
                Source = r.GetString(4)
            })
        .GroupBy(l => l.FolderName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public void SetLink(NexusLink link) =>
        _db.Execute(
            "INSERT INTO NexusLinks (FolderName, ModId, GameDomain, IsManual, Source) " +
            "VALUES ($folder, $id, $domain, $manual, $source) " +
            "ON CONFLICT(FolderName) DO UPDATE SET ModId = excluded.ModId, " +
            "GameDomain = excluded.GameDomain, IsManual = excluded.IsManual, Source = excluded.Source",
            ("folder", link.FolderName), ("id", link.ModId), ("domain", link.GameDomain),
            ("manual", link.IsManual ? 1 : 0), ("source", link.Source));

    public void RemoveLink(string folderName) =>
        _db.Execute("DELETE FROM NexusLinks WHERE FolderName = $folder", ("folder", folderName));

    public List<NexusModUpdate> LoadResults() =>
        _db.Query(
            "SELECT Payload FROM NexusCache",
            r => JsonConfig.Deserialize<NexusModUpdate>(r.GetString(0)))
        .Where(x => x is not null)
        .Select(x => x!)
        .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public void SaveResults(IEnumerable<NexusModUpdate> items)
    {
        foreach (var item in items)
        {
            _db.Execute(
                "INSERT INTO NexusCache (FolderName, Payload, CheckedUtc) VALUES ($folder, $payload, $checked) " +
                "ON CONFLICT(FolderName) DO UPDATE SET Payload = excluded.Payload, CheckedUtc = excluded.CheckedUtc",
                ("folder", item.FolderName), ("payload", JsonConfig.Serialize(item)),
                ("checked", Database.ToDb(item.CheckedUtc)));
        }
    }

    public void ClearResults() => _db.Execute("DELETE FROM NexusCache");
}
