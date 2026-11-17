using U7D2D.Core.Logs;
using U7D2D.Core.Models;

namespace U7D2D.Core.Tests;

/// <summary>
/// Regression for the attribute that took the first log analysis down: a HarmonyPatch blob with an
/// array length of 0x7FFFFFFF. Reading it must skip the attribute, keep the other targets and report
/// the skip, instead of allocating gigabytes.
/// </summary>
public sealed class HarmonyReaderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static byte[] CorruptArrayLength()
    {
        var bytes = File.ReadAllBytes(Fixture("TestModHarmony.dll"));
        byte[] pattern = [0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x05, (byte)'A', (byte)'l', (byte)'p', (byte)'h', (byte)'a'];

        var position = bytes.AsSpan().IndexOf(pattern);
        Assert.True(position >= 0, "the array attribute blob was not found in the fixture");

        // Bytes 0..1 are the prolog, bytes 2..5 are the array count. Turn 2 into int.MaxValue.
        bytes[position + 2] = 0xFF;
        bytes[position + 3] = 0xFF;
        bytes[position + 4] = 0xFF;
        bytes[position + 5] = 0x7F;
        return bytes;
    }

    [Fact]
    public void A_huge_array_length_is_skipped_and_the_other_targets_survive()
    {
        var bytes = CorruptArrayLength();

        var targets = HarmonyTargetReader.Read(bytes, out var skipped);

        Assert.Equal(1, skipped);
        Assert.Contains(targets, t => t.MemberName == "SetName");
        Assert.Contains(targets, t => t.MemberName == "LegacyCall");
    }

    [Fact]
    public void A_valid_fixture_is_read_without_skips()
    {
        var targets = HarmonyTargetReader.Read(File.ReadAllBytes(Fixture("TestModHarmony.dll")), out var skipped);

        Assert.Equal(0, skipped);
        Assert.Contains(targets, t => t.MemberName == "SetName");
    }

    [Fact]
    public void The_index_reports_the_assembly_with_the_skipped_attribute()
    {
        using var ws = new TestWorkspace();
        var modFolder = Path.Combine(ws.Mods, "BrokenHarmony");
        Directory.CreateDirectory(modFolder);
        File.WriteAllBytes(Path.Combine(modFolder, "BrokenHarmony.dll"), CorruptArrayLength());

        var index = ModTypeIndex.Build(new[]
        {
            new InstalledMod { FolderName = "BrokenHarmony", FullPath = modFolder, Info = new ModMetadata { Name = "BrokenHarmony" } }
        });

        var skipped = Assert.Single(index.SkippedAttributes);
        Assert.Equal("BrokenHarmony.dll", skipped.Dll);
        Assert.Equal(1, skipped.Skipped);
    }
}
