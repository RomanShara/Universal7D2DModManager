using U7D2D.Core.I18n;

namespace U7D2D.Core.Archives;

public enum ArchiveKind
{
    Unknown = 0,
    Zip,
    SevenZip,
    Rar,
    Tar,
    TarGz,
    TarBz2,
    TarXz,
    Gz,
    Bz2,
    Xz
}

public sealed record ArchiveFormatInfo(ArchiveKind Kind, string Label, bool IsSupported)
{
    public static readonly ArchiveFormatInfo Unknown = new(ArchiveKind.Unknown, Localization.S("arch.unknown"), false);
}

public static class ArchiveFormats
{
    private static readonly string[] Supported =
    [
        ".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tbz",
        ".tar.xz", ".txz", ".gz", ".bz2", ".xz", ".zipx", ".lzma", ".zst"
    ];

    public static IReadOnlyList<string> SupportedExtensions => Supported;

    public static ArchiveFormatInfo Detect(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();

        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
            return new(ArchiveKind.TarGz, "TAR.GZ", true);
        if (name.EndsWith(".tar.bz2", StringComparison.Ordinal) || name.EndsWith(".tbz2", StringComparison.Ordinal) || name.EndsWith(".tbz", StringComparison.Ordinal))
            return new(ArchiveKind.TarBz2, "TAR.BZ2", true);
        if (name.EndsWith(".tar.xz", StringComparison.Ordinal) || name.EndsWith(".txz", StringComparison.Ordinal))
            return new(ArchiveKind.TarXz, "TAR.XZ", true);
        if (name.EndsWith(".tar", StringComparison.Ordinal))
            return new(ArchiveKind.Tar, "TAR", true);
        if (name.EndsWith(".zip", StringComparison.Ordinal) || name.EndsWith(".zipx", StringComparison.Ordinal))
            return new(ArchiveKind.Zip, "ZIP", true);
        if (name.EndsWith(".7z", StringComparison.Ordinal))
            return new(ArchiveKind.SevenZip, "7Z", true);
        if (name.EndsWith(".rar", StringComparison.Ordinal))
            return new(ArchiveKind.Rar, "RAR", true);
        if (name.EndsWith(".gz", StringComparison.Ordinal))
            return new(ArchiveKind.Gz, "GZ", true);
        if (name.EndsWith(".bz2", StringComparison.Ordinal))
            return new(ArchiveKind.Bz2, "BZ2", true);
        if (name.EndsWith(".xz", StringComparison.Ordinal))
            return new(ArchiveKind.Xz, "XZ", true);

        return ArchiveFormatInfo.Unknown;
    }

    public static bool IsSupportedArchive(string path) => Detect(path).IsSupported;
}
