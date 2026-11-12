using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using U7D2D.Core.Archives;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

public sealed class ArchiveScannerTests
{
    [Fact]
    public void Detects_two_mods_inside_one_archive()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("TwoMods.zip",
            ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0")),
            ("ModA/Config/blocks.xml", TestWorkspace.BlocksPatch("a")),
            ("ModB/ModInfo.xml", TestWorkspace.ModernModInfo("Mod B", "2.0")),
            ("ModB/Config/items.xml", "<items/>"));

        var scanner = new ArchiveScanner(ws.Paths);
        var result = scanner.Scan(zip);

        Assert.True(result.Success);
        Assert.Equal(2, result.Mods.Count);
        Assert.Contains(result.Mods, m => m.FolderName == "ModA" && m.Info.Name == "Mod A");
        Assert.Contains(result.Mods, m => m.FolderName == "ModB" && m.Info.Version == "2.0");
        Assert.Contains(result.Warnings, w => w.Contains("Mods found in the archive: 2"));
    }

    [Fact]
    public void Detects_mod_inside_mods_wrapper_folder()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Wrapped.zip",
            ("Mods/Wrapped/ModInfo.xml", TestWorkspace.ModernModInfo("Wrapped Mod", "3.1")));

        var result = new ArchiveScanner(ws.Paths).Scan(zip);

        var mod = Assert.Single(result.Mods);
        Assert.Equal("Wrapped", mod.FolderName);
        Assert.Equal("Mods/Wrapped", mod.RootInArchive);
    }

    [Fact]
    public void Reports_corrupted_archive_without_throwing()
    {
        using var ws = new TestWorkspace();
        var path = Path.Combine(ws.Archives, "Broken.zip");
        File.WriteAllBytes(path, Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray());

        var result = new ArchiveScanner(ws.Paths).Scan(path);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Archive_without_modinfo_is_marked_as_unverified()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("NoModInfo.zip", ("Config/blocks.xml", TestWorkspace.BlocksFull("x")));

        var result = new ArchiveScanner(ws.Paths).Scan(zip);

        var mod = Assert.Single(result.Mods);
        Assert.False(mod.HasModInfo);
        Assert.Contains("ModInfo.xml not found", mod.Warning);
    }

    [Fact]
    public void Tar_gz_archive_is_read_and_named_without_compound_extension()
    {
        using var ws = new TestWorkspace();
        var tarPath = Path.Combine(ws.Archives, "CoolMod.tar.gz");

        using (var file = File.Create(tarPath))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            AddTarEntry(writer, "ModInfo.xml", TestWorkspace.ModernModInfo("Tar Mod", "1.5"));
            AddTarEntry(writer, "Config/blocks.xml", TestWorkspace.BlocksPatch("tarBlock"));
        }

        var result = new ArchiveScanner(ws.Paths).Scan(tarPath);

        Assert.True(result.Success, result.Error);
        var mod = Assert.Single(result.Mods);
        Assert.Equal("CoolMod", mod.FolderName);
        Assert.Equal("Tar Mod", mod.Info.Name);
    }

    [Fact]
    public void Zip_slip_entries_are_not_extracted_outside_target()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Slip.zip",
            ("ModSlip/ModInfo.xml", TestWorkspace.ModernModInfo("Slip", "1.0")),
            ("../evil.txt", "must not escape"));

        var destination = Path.Combine(ws.Root, "extract-target");
        using var handle = ArchiveReader.Open(zip, ws.Paths.TempDirectory);
        var extraction = ArchiveReader.ExtractSubtree(handle, "ModSlip", destination, overwrite: true);

        Assert.True(extraction.Success, extraction.Error);
        Assert.True(File.Exists(Path.Combine(destination, "ModInfo.xml")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(ws.Archives, "evil.txt")));
    }

    [Fact]
    public void Nested_roots_do_not_duplicate_archives_and_keep_most_specific_source()
    {
        using var ws = new TestWorkspace();
        var childRoot = Path.Combine(ws.Archives, "1");
        Directory.CreateDirectory(childRoot);

        var outerZip = ws.WriteZip("outer.zip", ("Outer/ModInfo.xml", TestWorkspace.ModernModInfo("Outer", "1.0")));

        var innerZip = Path.Combine(childRoot, "inner.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(innerZip, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Inner/ModInfo.xml");
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(TestWorkspace.ModernModInfo("Inner", "2.0"));
        }

        var files = ArchiveLocator.EnumerateArchives([ws.Archives, childRoot], recursive: true, maxDepth: 10);

        Assert.Equal(2, files.Count);
        Assert.Equal(Path.GetFullPath(childRoot), files.Single(f => f.Path == Path.GetFullPath(innerZip)).SourceRoot);
        Assert.Equal(Path.GetFullPath(ws.Archives), files.Single(f => f.Path == Path.GetFullPath(outerZip)).SourceRoot);
    }

    [Fact]
    public void Scan_records_source_root_for_result_and_mods()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Sourced.zip", ("Sourced/ModInfo.xml", TestWorkspace.ModernModInfo("Sourced", "1.0")));

        var result = new ArchiveScanner(ws.Paths).Scan(zip, @"D://Mods//1");

        Assert.Equal(@"D://Mods//1", result.SourceRoot);
        Assert.All(result.Mods, m => Assert.Equal(@"D://Mods//1", m.SourceRoot));
    }

    [Fact]
    public void Missing_file_returns_error_instead_of_exception()
    {
        using var ws = new TestWorkspace();
        var result = new ArchiveScanner(ws.Paths).Scan(Path.Combine(ws.Archives, "nope.zip"));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    private static void AddTarEntry(TarWriter writer, string name, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
        };
        writer.WriteEntry(entry);
    }
}
