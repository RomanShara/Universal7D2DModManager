namespace U7D2D.Core.Data;

/// <summary>
/// Remembers which root the player picked for an ambiguous archive. The key is the package
/// fingerprint, so a changed archive (new size, mtime or structure) asks again instead of silently
/// reusing an old decision.
/// </summary>
public sealed class RootChoiceStore
{
    private readonly Database _db;

    public RootChoiceStore(Database db) => _db = db;

    public (string Name, string ContentRoot)? Get(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;

        var rows = _db.Query(
            "SELECT Name, ContentRoot FROM RootChoices WHERE Fingerprint = $fingerprint",
            reader => (reader.GetString(0), reader.GetString(1)),
            ("fingerprint", fingerprint));

        return rows.Count == 0 ? null : rows[0];
    }

    public void Save(string fingerprint, string name, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return;

        _db.Execute(
            "INSERT INTO RootChoices (Fingerprint, Name, ContentRoot, SavedUtc) " +
            "VALUES ($fingerprint, $name, $root, $saved) " +
            "ON CONFLICT(Fingerprint) DO UPDATE SET Name = excluded.Name, " +
            "ContentRoot = excluded.ContentRoot, SavedUtc = excluded.SavedUtc",
            ("fingerprint", fingerprint), ("name", name), ("root", contentRoot),
            ("saved", Database.ToDb(DateTime.UtcNow)));
    }
}
