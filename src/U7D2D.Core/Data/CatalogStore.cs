using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Data;

/// <summary>Caches archive scan results so repeated scans of unchanged archives are instant.</summary>
public sealed class CatalogStore
{
    private readonly Database _db;

    public CatalogStore(Database db) => _db = db;

    public ArchiveScanResult? TryGetCached(string archivePath, long size, DateTime modifiedUtc, int requiredScannerVersion, string sourceRoot)
    {
        var rows = _db.Query(
            "SELECT Payload, ArchiveSize, ArchiveModifiedUtc FROM ArchiveScans WHERE ArchivePath = $path",
            reader => new
            {
                Payload = reader.GetString(0),
                Size = reader.GetInt64(1),
                Modified = reader.GetString(2)
            },
            ("path", archivePath));

        var row = rows.FirstOrDefault();
        if (row is null) return null;
        if (row.Size != size) return null;

        var cachedModified = Database.FromDb(row.Modified);
        if (Math.Abs((cachedModified - modifiedUtc.ToUniversalTime()).TotalSeconds) > 1) return null;

        var result = JsonConfig.Deserialize<ArchiveScanResult>(row.Payload);
        if (result is null) return null;

        if (result.ScannerVersion != requiredScannerVersion ||
            !string.Equals(result.SourceRoot ?? string.Empty, sourceRoot ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            // Parsing logic or configured roots changed — drop the entry instead of showing stale data.
            _db.Execute("DELETE FROM ArchiveScans WHERE ArchivePath = $path", ("path", archivePath));
            return null;
        }

        result.FromCache = true;
        return result;
    }

    /// <summary>Loads every cached scan whose parser version matches and whose archive still exists.</summary>
    public List<ArchiveScanResult> LoadAll(int requiredScannerVersion)
    {
        var rows = _db.Query("SELECT ArchivePath, Payload FROM ArchiveScans",
            r => (Path: r.GetString(0), Payload: r.GetString(1)));

        var results = new List<ArchiveScanResult>();
        foreach (var (path, payload) in rows)
        {
            var result = JsonConfig.Deserialize<ArchiveScanResult>(payload);
            if (result is null || result.ScannerVersion != requiredScannerVersion || !File.Exists(path))
            {
                _db.Execute("DELETE FROM ArchiveScans WHERE ArchivePath = $path", ("path", path));
                continue;
            }

            result.FromCache = true;
            results.Add(result);
        }

        return results;
    }

    public void Save(ArchiveScanResult result)
    {
        _db.Execute("""
            INSERT INTO ArchiveScans (ArchivePath, ArchiveSize, ArchiveModifiedUtc, Success, Error, Format, Payload, ScannedUtc)
            VALUES ($path, $size, $modified, $success, $error, $format, $payload, $scanned)
            ON CONFLICT(ArchivePath) DO UPDATE SET
                ArchiveSize = excluded.ArchiveSize,
                ArchiveModifiedUtc = excluded.ArchiveModifiedUtc,
                Success = excluded.Success,
                Error = excluded.Error,
                Format = excluded.Format,
                Payload = excluded.Payload,
                ScannedUtc = excluded.ScannedUtc
            """,
            ("path", result.ArchivePath),
            ("size", result.ArchiveSize),
            ("modified", Database.ToDb(result.ArchiveModifiedUtc)),
            ("success", result.Success ? 1 : 0),
            ("error", result.Error),
            ("format", result.Format),
            ("payload", JsonConfig.Serialize(result)),
            ("scanned", Database.ToDb(DateTime.UtcNow)));
    }

    public void Clear()
    {
        _db.Execute("DELETE FROM ArchiveScans");
    }

    public void RemoveStale(IEnumerable<string> existingArchivePaths)
    {
        var keep = new HashSet<string>(existingArchivePaths, StringComparer.OrdinalIgnoreCase);
        var stored = _db.Query("SELECT ArchivePath FROM ArchiveScans", r => r.GetString(0));
        foreach (var path in stored.Where(p => !keep.Contains(p)))
            _db.Execute("DELETE FROM ArchiveScans WHERE ArchivePath = $path", ("path", path));
    }
}
