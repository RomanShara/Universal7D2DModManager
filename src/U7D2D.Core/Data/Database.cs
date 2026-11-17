using System.Globalization;
using Microsoft.Data.Sqlite;
using U7D2D.Core.Infrastructure;

namespace U7D2D.Core.Data;

/// <summary>Thin ADO.NET wrapper around the SQLite database. No ORM, explicit schema, WAL journaling.</summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly string _logDirectory;
    private readonly object _writeLock = new();

    public Database(AppPaths paths)
    {
        FilePath = paths.DatabasePath;
        _logDirectory = paths.LogDirectory;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache: every operation opens its own connection, WAL handles the concurrency,
            // and a shared cache keeps the file handle alive after a failed open, which blocks
            // moving a damaged database aside.
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public string FilePath { get; }

    /// <summary>Set when a damaged database was moved aside and a new one was created.</summary>
    public DatabaseRecoveryInfo? Recovery { get; private set; }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        lock (_writeLock)
        {
            if (TryPrepare(out var error)) return;

            // Only a damaged file is replaced. A locked file, a full disk or a permission problem
            // must not cost the user the settings that are still readable.
            if (error is null || !IsCorruption(error))
                throw new InvalidOperationException(
                    $"The database at {FilePath} could not be opened.", error);

            Recovery = Quarantine(error);

            if (!TryPrepare(out var second))
                throw new InvalidOperationException(
                    $"The database at {FilePath} could not be created after recovery.", second);
        }
    }

    private bool TryPrepare(out SqliteException? error)
    {
        error = null;

        try
        {
            using var connection = Open();

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check;";
                var result = check.ExecuteScalar() as string;

                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    error = new SqliteException($"Integrity check failed: {result}", 11);
                    return false;
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = Schema;
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>SQLITE_CORRUPT, SQLITE_NOTADB and the messages they produce.</summary>
    private static bool IsCorruption(SqliteException error)
    {
        if (error.SqliteErrorCode is 11 or 26) return true;

        return error.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
               || error.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase)
               || error.Message.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase);
    }

    private DatabaseRecoveryInfo Quarantine(SqliteException error)
    {
        // Pooled connections keep the file handle after Dispose; release them before the move.
        SqliteConnection.ClearAllPools();

        var directory = Path.GetDirectoryName(FilePath)!;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var quarantine = Path.Combine(directory, $"manager.corrupt-{stamp}.db");

        var counter = 1;
        while (File.Exists(quarantine))
        {
            quarantine = Path.Combine(directory, $"manager.corrupt-{stamp}-{counter}.db");
            counter++;
        }

        MoveIfExists(FilePath, quarantine);
        MoveIfExists(FilePath + "-wal", quarantine + "-wal");
        MoveIfExists(FilePath + "-shm", quarantine + "-shm");

        var info = new DatabaseRecoveryInfo
        {
            QuarantinePath = quarantine,
            Reason = error.Message,
            RecoveredUtc = DateTime.UtcNow
        };

        WriteRecoveryLog(info);
        return info;
    }

    private static void MoveIfExists(string source, string destination)
    {
        if (!File.Exists(source)) return;

        // An antivirus or a just-closed connection can hold the file for a moment.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(100);
            }
        }
    }

    private void WriteRecoveryLog(DatabaseRecoveryInfo info)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var file = Path.Combine(_logDirectory, $"db-recovery-{info.RecoveredUtc.ToLocalTime():yyyyMMdd-HHmmss}.log");

            File.WriteAllText(file,
                $"{info.RecoveredUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} database recovered{Environment.NewLine}" +
                $"database: {FilePath}{Environment.NewLine}" +
                $"quarantine: {info.QuarantinePath}{Environment.NewLine}" +
                $"reason: {info.Reason}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception)
        {
            // The recovery itself must not fail because its note could not be written.
        }
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);

        try
        {
            connection.Open();

            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=8000;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            // A failed pragma used to leave the handle alive until the finalizer ran, which also
            // blocked recovery from moving a damaged file aside.
            connection.Dispose();
            throw;
        }
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_writeLock)
        {
            using var connection = Open();
            using var command = CreateCommand(connection, sql, parameters);
            return command.ExecuteNonQuery();
        }
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = CreateCommand(connection, sql, parameters);
        var result = command.ExecuteScalar();
        if (result is null || result is DBNull) return default;

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(string)) return (T)(object)Convert.ToString(result, CultureInfo.InvariantCulture)!;
        if (target == typeof(long)) return (T)(object)Convert.ToInt64(result, CultureInfo.InvariantCulture);
        if (target == typeof(int)) return (T)(object)Convert.ToInt32(result, CultureInfo.InvariantCulture);
        if (target == typeof(double)) return (T)(object)Convert.ToDouble(result, CultureInfo.InvariantCulture);
        if (target == typeof(bool)) return (T)(object)(Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0);
        if (target == typeof(DateTime))
            return (T)(object)DateTime.Parse(Convert.ToString(result, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return (T)Convert.ChangeType(result, target, CultureInfo.InvariantCulture);
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = CreateCommand(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name.StartsWith('$') ? name : "$" + name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    public static string ToDb(DateTime value) => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public static DateTime FromDb(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DateTime.MinValue
            : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Settings (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ArchiveScans (
            ArchivePath TEXT PRIMARY KEY,
            ArchiveSize INTEGER NOT NULL,
            ArchiveModifiedUtc TEXT NOT NULL,
            Success INTEGER NOT NULL,
            Error TEXT,
            Format TEXT,
            Payload TEXT NOT NULL,
            ScannedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS InstalledMods (
            FolderName TEXT PRIMARY KEY,
            FullPath TEXT NOT NULL,
            Name TEXT,
            Version TEXT,
            ContentHash TEXT,
            SourceArchive TEXT,
            InstalledUtc TEXT,
            Payload TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Backups (
            Id TEXT PRIMARY KEY,
            ModFolder TEXT NOT NULL,
            ArchivePath TEXT NOT NULL,
            SizeBytes INTEGER NOT NULL,
            ModName TEXT,
            ModVersion TEXT,
            Reason TEXT,
            CreatedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS OperationLog (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TimestampUtc TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Message TEXT NOT NULL,
            Details TEXT
        );

        CREATE TABLE IF NOT EXISTS AnalysisCache (
            Key TEXT PRIMARY KEY,
            Payload TEXT NOT NULL,
            CreatedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS NexusLinks (
            FolderName TEXT PRIMARY KEY,
            ModId INTEGER NOT NULL,
            GameDomain TEXT NOT NULL,
            IsManual INTEGER NOT NULL,
            Source TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS NexusCache (
            FolderName TEXT PRIMARY KEY,
            Payload TEXT NOT NULL,
            CheckedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS RootChoices (
            Fingerprint TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            ContentRoot TEXT NOT NULL,
            SavedUtc TEXT NOT NULL
        );

        PRAGMA user_version = 3;
        """;
}
