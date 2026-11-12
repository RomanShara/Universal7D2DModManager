using System.Globalization;
using Microsoft.Data.Sqlite;
using U7D2D.Core.Infrastructure;

namespace U7D2D.Core.Data;

/// <summary>Thin ADO.NET wrapper around the SQLite database. No ORM, explicit schema, WAL journaling.</summary>
public sealed class Database
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public Database(AppPaths paths)
    {
        FilePath = paths.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public string FilePath { get; }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        lock (_writeLock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=8000;";
        pragma.ExecuteNonQuery();
        return connection;
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

        PRAGMA user_version = 2;
        """;
}
