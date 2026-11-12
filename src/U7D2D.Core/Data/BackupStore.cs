using U7D2D.Core.Models;

namespace U7D2D.Core.Data;

public sealed class BackupStore
{
    private readonly Database _db;

    public BackupStore(Database db) => _db = db;

    public List<BackupRecord> List()
    {
        return _db.Query("""
            SELECT Id, ModFolder, ArchivePath, SizeBytes, ModName, ModVersion, Reason, CreatedUtc
            FROM Backups ORDER BY CreatedUtc DESC
            """, r => new BackupRecord
        {
            Id = r.GetString(0),
            ModFolder = r.GetString(1),
            ArchivePath = r.GetString(2),
            SizeBytes = r.GetInt64(3),
            ModName = r.IsDBNull(4) ? string.Empty : r.GetString(4),
            ModVersion = r.IsDBNull(5) ? string.Empty : r.GetString(5),
            Reason = r.IsDBNull(6) ? string.Empty : r.GetString(6),
            CreatedUtc = Database.FromDb(r.GetString(7))
        });
    }

    public void Add(BackupRecord record)
    {
        _db.Execute("""
            INSERT INTO Backups (Id, ModFolder, ArchivePath, SizeBytes, ModName, ModVersion, Reason, CreatedUtc)
            VALUES ($id, $folder, $path, $size, $name, $version, $reason, $created)
            ON CONFLICT(Id) DO UPDATE SET
                ArchivePath = excluded.ArchivePath,
                SizeBytes = excluded.SizeBytes
            """,
            ("id", record.Id),
            ("folder", record.ModFolder),
            ("path", record.ArchivePath),
            ("size", record.SizeBytes),
            ("name", record.ModName),
            ("version", record.ModVersion),
            ("reason", record.Reason),
            ("created", Database.ToDb(record.CreatedUtc)));
    }

    public void Remove(string id) =>
        _db.Execute("DELETE FROM Backups WHERE Id = $id", ("id", id));

    public void RemoveForFolder(string folder) =>
        _db.Execute("DELETE FROM Backups WHERE ModFolder = $folder", ("folder", folder));
}
