using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;
using U7D2D.Core.I18n;
using U7D2D.Core.Infrastructure;

namespace U7D2D.Core.Archives;

public sealed class ArchiveEntryInfo
{
    public string Key { get; init; } = string.Empty;
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
    public DateTime? ModifiedUtc { get; init; }
}

public sealed class ArchiveOpenException : Exception
{
    public ArchiveOpenException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Opened archive with normalized entry keys. Handles single-layer compression
/// (.gz/.bz2/.xz) and nested tarballs transparently.
/// </summary>
public sealed class ArchiveHandle : IDisposable
{
    private readonly IArchive? _archive;
    private readonly string? _singleFilePath;
    private readonly string? _tempDirectory;
    private bool _disposed;

    internal ArchiveHandle(
        string originalPath,
        string sourcePath,
        string formatLabel,
        ArchiveFormatInfo format,
        IArchive? archive,
        List<ArchiveEntryInfo> entries,
        string? singleFilePath,
        string? tempDirectory,
        bool isEncrypted)
    {
        OriginalPath = originalPath;
        SourcePath = sourcePath;
        FormatLabel = formatLabel;
        Format = format;
        _archive = archive;
        Entries = entries;
        _singleFilePath = singleFilePath;
        _tempDirectory = tempDirectory;
        IsEncrypted = isEncrypted;
    }

    public string OriginalPath { get; }
    public string SourcePath { get; }
    public string FormatLabel { get; }
    public ArchiveFormatInfo Format { get; }
    public List<ArchiveEntryInfo> Entries { get; }
    public bool IsEncrypted { get; }
    public bool IsSingleFile => _singleFilePath is not null;

    /// <summary>Tar containers support cheap random access, so entries are extracted directly.</summary>
    public bool IsTarBased => Format.Kind is ArchiveKind.Tar or ArchiveKind.TarGz or ArchiveKind.TarBz2 or ArchiveKind.TarXz;

    public Stream OpenEntryStream(string key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_singleFilePath is not null)
            return new FileStream(_singleFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        if (_archive is null) throw new ArchiveOpenException(Localization.S("arch.notOpen"));

        var normalized = PathSafety.NormalizeKey(key);
        var entry = _archive.Entries.FirstOrDefault(e =>
            !e.IsDirectory && string.Equals(PathSafety.NormalizeKey(e.Key ?? string.Empty), normalized, StringComparison.OrdinalIgnoreCase));

        if (entry is null) throw new ArchiveOpenException(Localization.SF("arch.entryNotFound", key));
        return entry.OpenEntryStream();
    }

    public byte[] ReadEntryBytes(string key, long maxBytes = 8 * 1024 * 1024)
    {
        using var stream = OpenEntryStream(key);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new ArchiveOpenException(Localization.SF("arch.entryTooLarge", key, maxBytes));
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _archive?.Dispose(); } catch (Exception) { /* ignore */ }

        if (_tempDirectory is not null)
        {
            try { Directory.Delete(_tempDirectory, true); } catch (Exception) { /* best effort */ }
        }
    }

    internal static ArchiveHandle Create(
        string originalPath,
        string sourcePath,
        ArchiveFormatInfo format,
        IArchive? archive,
        List<ArchiveEntryInfo> entries,
        string? singleFilePath,
        string? tempDirectory,
        bool isEncrypted) =>
        new(originalPath, sourcePath, format.Label, format, archive, entries, singleFilePath, tempDirectory, isEncrypted);
}

public static class ArchiveReader
{
    private static ReaderOptions Options => new()
    {
        LeaveStreamOpen = false,
        LookForHeader = true
    };

    /// <summary>Opens an archive of any supported format and enumerates its entries.</summary>
    public static ArchiveHandle Open(string archivePath, string tempRoot, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
            throw new ArchiveOpenException(Localization.SF("arch.fileNotFound", archivePath));

        var format = ArchiveFormats.Detect(archivePath);
        if (!format.IsSupported)
            throw new ArchiveOpenException(Localization.SF("arch.unsupportedFormat", Path.GetExtension(archivePath)));

        try
        {
            return format.Kind switch
            {
                ArchiveKind.Gz or ArchiveKind.Bz2 or ArchiveKind.Xz or ArchiveKind.TarGz or ArchiveKind.TarBz2 or ArchiveKind.TarXz
                    => OpenCompressed(archivePath, format, tempRoot, cancellationToken),
                _ => OpenDirect(archivePath, format)
            };
        }
        catch (ArchiveOpenException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ArchiveOpenException(
                Localization.SF("arch.openFailed", Path.GetFileName(archivePath), ex.Message), ex);
        }
    }

    private static ArchiveHandle OpenDirect(string archivePath, ArchiveFormatInfo format)
    {
        var archive = ArchiveFactory.OpenArchive(archivePath, Options);
        var entries = new List<ArchiveEntryInfo>();
        foreach (var entry in archive.Entries)
        {
            var key = PathSafety.NormalizeKey(entry.Key ?? string.Empty);
            if (string.IsNullOrEmpty(key)) continue;
            entries.Add(new ArchiveEntryInfo
            {
                Key = key,
                Size = entry.Size,
                IsDirectory = entry.IsDirectory,
                ModifiedUtc = entry.LastModifiedTime?.ToUniversalTime()
            });
        }

        return ArchiveHandle.Create(archivePath, archivePath, format, archive, entries, null, null, archive.IsEncrypted);
    }

    private static ArchiveHandle OpenCompressed(string archivePath, ArchiveFormatInfo format, string tempRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tempRoot);
        var tempDir = Path.Combine(tempRoot, $"arc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var isTarContainer = format.Kind is ArchiveKind.TarGz or ArchiveKind.TarBz2 or ArchiveKind.TarXz;
        var tempFile = Path.Combine(tempDir, "payload.bin");

        using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var decompressed = CreateDecompressionStream(input, format.Kind))
        using (var output = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            decompressed.CopyTo(output, 81920);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var looksLikeTar = isTarContainer || IsTarFile(tempFile);

        if (!looksLikeTar)
        {
            var fileName = Path.GetFileNameWithoutExtension(archivePath);
            return ArchiveHandle.Create(
                archivePath, tempFile, format, null,
                [new ArchiveEntryInfo { Key = fileName, Size = new FileInfo(tempFile).Length, IsDirectory = false }],
                tempFile, tempDir, false);
        }

        var tar = SharpCompress.Archives.Tar.TarArchive.OpenArchive(tempFile, Options);
        var entries = new List<ArchiveEntryInfo>();
        foreach (var entry in tar.Entries)
        {
            var key = PathSafety.NormalizeKey(entry.Key ?? string.Empty);
            if (string.IsNullOrEmpty(key)) continue;
            entries.Add(new ArchiveEntryInfo
            {
                Key = key,
                Size = entry.Size,
                IsDirectory = entry.IsDirectory,
                ModifiedUtc = entry.LastModifiedTime?.ToUniversalTime()
            });
        }

        var effectiveFormat = isTarContainer ? format : new ArchiveFormatInfo(ArchiveKind.Tar, "TAR", true);
        return ArchiveHandle.Create(archivePath, tempFile, effectiveFormat, tar, entries, null, tempDir, false);
    }

    private static Stream CreateDecompressionStream(Stream input, ArchiveKind kind) => kind switch
    {
        ArchiveKind.Gz or ArchiveKind.TarGz => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true),
        ArchiveKind.Bz2 or ArchiveKind.TarBz2 => BZip2Stream.Create(input, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true, leaveOpen: true),
        ArchiveKind.Xz or ArchiveKind.TarXz => new XZStream(input),
        _ => throw new ArchiveOpenException(Localization.SF("arch.unsupportedCompression", kind))
    };

    private static bool IsTarFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 512) return false;
            var header = new byte[512];
            var read = stream.Read(header, 0, header.Length);
            if (read < 512) return false;
            // POSIX tar stores "ustar" at offset 257.
            return header[257] == 'u' && header[258] == 's' && header[259] == 't' &&
                   header[260] == 'a' && header[261] == 'r';
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Extracts every file under <paramref name="rootPrefix"/> into <paramref name="destinationRoot"/>.</summary>
    public static ExtractionResult ExtractSubtree(
        ArchiveHandle handle,
        string rootPrefix,
        string destinationRoot,
        bool overwrite,
        IProgress<(int Processed, string Current)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ExtractionResult();
        var prefix = PathSafety.NormalizeKey(rootPrefix);
        if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";

        Directory.CreateDirectory(destinationRoot);

        if (handle.IsSingleFile)
        {
            var target = Path.Combine(destinationRoot, Path.GetFileName(handle.Entries[0].Key));
            using (var source = handle.OpenEntryStream(handle.Entries[0].Key))
            using (var destination = new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination, 81920);
            }
            result.Files.Add(Path.GetFileName(target));
            return result;
        }

        var extracted = 0;

        // 7z has no streaming reader in SharpCompress; tar containers are cheap to random-access.
        if (handle.IsTarBased || handle.Format.Kind == ArchiveKind.SevenZip)
            return ExtractByRandomAccess(handle, prefix, destinationRoot, overwrite, progress, cancellationToken);

        try
        {
            using var reader = ReaderFactory.OpenReader(handle.SourcePath, Options);
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory) continue;

                var key = PathSafety.NormalizeKey(entry.Key ?? string.Empty);
                if (key.Length == 0) continue;
                if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = prefix.Length == 0 ? key : key[prefix.Length..];
                if (string.IsNullOrWhiteSpace(relative)) continue;

                var target = PathSafety.SafeCombine(destinationRoot, relative);
                if (target is null)
                {
                    result.SkippedUnsafe.Add(key);
                    continue;
                }

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                if (File.Exists(target) && !overwrite)
                {
                    result.SkippedExisting.Add(relative);
                    continue;
                }

                using (var entryStream = reader.OpenEntryStream())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    entryStream.CopyTo(output, 81920);
                }

                extracted++;
                result.Files.Add(relative);
                progress?.Report((extracted, relative));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;

            // Some formats cannot be streamed (or the stream failed midway): retry with direct entry access.
            if (result.Files.Count == 0)
            {
                var fallback = ExtractByRandomAccess(handle, prefix, destinationRoot, overwrite, progress, cancellationToken);
                if (fallback.Success) return fallback;
            }
        }

        return result;
    }
    private static ExtractionResult ExtractByRandomAccess(
        ArchiveHandle handle,
        string prefix,
        string destinationRoot,
        bool overwrite,
        IProgress<(int Processed, string Current)>? progress,
        CancellationToken cancellationToken)
    {
        var result = new ExtractionResult();
        var extracted = 0;

        try
        {
            foreach (var entry in handle.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;

                var key = PathSafety.NormalizeKey(entry.Key);
                if (key.Length == 0) continue;
                if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = prefix.Length == 0 ? key : key[prefix.Length..];
                if (string.IsNullOrWhiteSpace(relative)) continue;

                var target = PathSafety.SafeCombine(destinationRoot, relative);
                if (target is null)
                {
                    result.SkippedUnsafe.Add(key);
                    continue;
                }

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                if (File.Exists(target) && !overwrite)
                {
                    result.SkippedExisting.Add(relative);
                    continue;
                }

                using (var source = handle.OpenEntryStream(entry.Key))
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(output, 81920);
                }

                extracted++;
                result.Files.Add(relative);
                progress?.Report((extracted, relative));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }
}

public sealed class ExtractionResult
{
    public List<string> Files { get; } = new();
    public List<string> SkippedExisting { get; } = new();
    public List<string> SkippedUnsafe { get; } = new();
    public string? Error { get; set; }
    public bool Success => Error is null;
    public long TotalBytes { get; set; }
}
