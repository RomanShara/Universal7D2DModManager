using U7D2D.Core.Models;

namespace U7D2D.Core.Data;

public sealed class LogStore
{
    private readonly Database _db;

    public LogStore(Database db) => _db = db;

    public void Add(string kind, string message, string? details)
    {
        _db.Execute(
            "INSERT INTO OperationLog (TimestampUtc, Kind, Message, Details) VALUES ($ts, $kind, $message, $details)",
            ("ts", Database.ToDb(DateTime.UtcNow)),
            ("kind", kind),
            ("message", message),
            ("details", details ?? string.Empty));
    }

    public List<OperationLogEntry> List(int limit = 200)
    {
        return _db.Query("""
            SELECT Id, TimestampUtc, Kind, Message, Details
            FROM OperationLog ORDER BY Id DESC LIMIT $limit
            """, r => new OperationLogEntry
        {
            Id = r.GetInt64(0),
            TimestampUtc = Database.FromDb(r.GetString(1)),
            Kind = r.GetString(2),
            Message = r.GetString(3),
            Details = r.IsDBNull(4) ? string.Empty : r.GetString(4)
        }, ("limit", limit));
    }

    public void Clear() => _db.Execute("DELETE FROM OperationLog");
}
