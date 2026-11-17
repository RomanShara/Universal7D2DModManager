using U7D2D.Core.I18n;

namespace U7D2D.Core.Infrastructure;

public static class FileOps
{
    /// <summary>Outcome of replacing a directory with a fully prepared copy.</summary>
    public sealed class DirectoryCommitResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }

        /// <summary>True when the previous directory was moved back into place after a failure.</summary>
        public bool RolledBack { get; init; }

        /// <summary>True when the failure looks like a file lock rather than a full or broken disk.</summary>
        public bool LikelyLocked { get; init; }

        /// <summary>Where the previous directory was kept when it could not be moved back.</summary>
        public string? KeptOldPath { get; init; }

        /// <summary>Where the new files were left when the target could not be replaced.</summary>
        public string? KeptNewPath { get; init; }

        /// <summary>Non-fatal problem, for example an old directory that could not be removed.</summary>
        public string? Warning { get; init; }
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with the prepared directory.
    ///
    /// The new files are first copied next to the target, so the copy happens on the target volume
    /// and the swap itself is a rename. The old directory is renamed aside until the new one is in
    /// place, which gives a rollback window even when the user turned backups off. Nothing is
    /// deleted before the new version is confirmed in place.
    /// </summary>
    public static DirectoryCommitResult CommitDirectory(string prepared, string target)
    {
        if (string.IsNullOrWhiteSpace(prepared) || !Directory.Exists(prepared))
            return new() { Error = Localization.SF("io.preparedMissing", prepared) };

        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent))
            return new() { Error = Localization.SF("io.invalidTarget", target) };

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new() { Error = ex.Message, LikelyLocked = IsLock(ex) };
        }

        if (File.Exists(target) && !Directory.Exists(target))
            return new() { Error = Localization.SF("io.targetIsFile", target) };

        var newDirectory = UniqueSibling(target, "new");

        try
        {
            CopyDirectory(prepared, newDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(newDirectory);
            return new() { Error = ex.Message, LikelyLocked = IsLock(ex) };
        }

        // A short copy is not a usable mod: stop before the old version is touched.
        var expected = CountFiles(prepared);
        var copied = CountFiles(newDirectory);

        if (expected == 0 || copied != expected)
        {
            TryDeleteDirectory(newDirectory);
            return new()
            {
                Error = Localization.SF("io.copyIncomplete", copied, expected),
                KeptNewPath = null
            };
        }

        var hadTarget = Directory.Exists(target);
        var oldDirectory = UniqueSibling(target, "old");

        if (hadTarget)
        {
            try
            {
                Directory.Move(target, oldDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteDirectory(newDirectory);
                return new() { Error = ex.Message, LikelyLocked = true };
            }
        }

        try
        {
            Directory.Move(newDirectory, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rolledBack = false;

            if (hadTarget)
            {
                try
                {
                    Directory.Move(oldDirectory, target);
                    rolledBack = true;
                }
                catch (Exception)
                {
                    // The old copy stays aside; the caller reports both paths.
                }
            }

            TryDeleteDirectory(newDirectory);

            // The previous version is aside and the new one could not take its place. Delete the
            // new copy only when the old one is back, otherwise both are kept for manual recovery.
            if (!hadTarget || rolledBack) TryDeleteDirectory(newDirectory);

            return new()
            {
                Error = ex.Message,
                RolledBack = rolledBack,
                LikelyLocked = IsLock(ex),
                KeptOldPath = hadTarget && !rolledBack ? oldDirectory : null,
                KeptNewPath = hadTarget && !rolledBack && Directory.Exists(newDirectory) ? newDirectory : null
            };
        }

        string? warning = null;
        if (hadTarget && !DeleteDirectory(oldDirectory))
            warning = Localization.SF("io.oldNotRemoved", oldDirectory);

        return new() { Success = true, Warning = warning, KeptOldPath = warning is null ? null : oldDirectory };
    }

    private static string UniqueSibling(string target, string purpose)
    {
        var parent = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        return Path.Combine(parent, $".{name}.u7d2d-{purpose}-{Guid.NewGuid():N}");
    }

    /// <summary>Win32 sharing and lock violations, the usual reason a mod folder cannot be moved.</summary>
    private static bool IsLock(Exception exception)
    {
        if (exception is UnauthorizedAccessException) return true;

        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.Copy(file, destination, overwrite: true);
        }
    }

    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    public static int CountFiles(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try { return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caller decides how to surface the failure.
        }
    }

    /// <summary>Deletes a directory and returns false if it still exists afterwards.</summary>
    public static bool DeleteDirectory(string path)
    {
        TryDeleteDirectory(path);
        return !Directory.Exists(path);
    }

    public static string FormatSize(long bytes)
    {
        string[] units =
        [
            Localization.S("io.unitB"), Localization.S("io.unitKb"), Localization.S("io.unitMb"),
            Localization.S("io.unitGb"), Localization.S("io.unitTb")
        ];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
