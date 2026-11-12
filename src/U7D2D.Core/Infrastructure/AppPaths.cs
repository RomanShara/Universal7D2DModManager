namespace U7D2D.Core.Infrastructure;

/// <summary>
/// Well-known folders used by the manager.
/// Everything lives in the Data folder next to the executable. There is deliberately no fallback to
/// the system drive: the manager never writes outside its own directory.
/// </summary>
public sealed class AppPaths
{
    private const string DataFolderName = "Data";

    public AppPaths() : this(null)
    {
    }

    /// <param name="rootOverride">Used by tests to isolate the database and temp folders.</param>
    public AppPaths(string? rootOverride)
    {
        var isOverride = !string.IsNullOrWhiteSpace(rootOverride);
        Root = isOverride ? rootOverride! : ResolveRoot();

        DatabasePath = Path.Combine(Root, "manager.db");
        LogDirectory = Path.Combine(Root, "logs");
        TempDirectory = Path.Combine(Root, "temp");
        CacheDirectory = Path.Combine(Root, "cache");
        ConfigBackupDirectory = Path.Combine(Root, "config-backups");
        EnsureCreated();
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string LogDirectory { get; }
    public string TempDirectory { get; }
    public string CacheDirectory { get; }

    /// <summary>Backups of mod configuration files made before every edit.</summary>
    public string ConfigBackupDirectory { get; }

    /// <summary>True when the Data folder next to the executable accepts writes.</summary>
    public bool IsWritable { get; private set; }

    private static string ResolveRoot() => Path.Combine(AppContext.BaseDirectory, DataFolderName);

    private static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ConfigBackupDirectory);

        IsWritable = CanWriteTo(Root);
    }

    /// <summary>Creates a unique working folder under temp and returns its path.</summary>
    public string CreateTempDirectory(string purpose)
    {
        var directory = Path.Combine(TempDirectory, $"{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Removes working folders left behind by an interrupted operation.</summary>
    public void CleanupTemp()
    {
        try
        {
            if (!Directory.Exists(TempDirectory)) return;

            foreach (var directory in Directory.EnumerateDirectories(TempDirectory))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception)
                {
                    // A folder still in use by another operation is left for the next start.
                }
            }
        }
        catch (Exception)
        {
            // Cleanup is best-effort and must never block startup.
        }
    }
}
