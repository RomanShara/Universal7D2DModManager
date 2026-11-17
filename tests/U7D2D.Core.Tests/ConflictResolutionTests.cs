using System.Xml.Linq;
using System.Xml.XPath;
using U7D2D.Core.Analysis;
using U7D2D.Core.Data;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

/// <summary>
/// The patch written for a resolution has to be a real 7 Days to Die patch mod: the game applies
/// xpath commands only inside &lt;configs&gt;, and a folder that cannot work in game must not be left
/// behind. ModInfo.xml has to be in the modern layout the manager itself parses.
/// </summary>
public sealed class ConflictResolutionTests
{
    private const string DamageXPath = "/items/item[@name='weapon_A']/property[@name='Damage']/@value";

    private const string VanillaItems = """
        <items>
          <item name="weapon_A">
            <property name="Damage" value="50"/>
          </item>
        </items>
        """;

    private static void WriteVanilla(TestWorkspace ws, string? content = null)
    {
        var dir = Path.Combine(ws.Game, "Data", "Config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "items.xml"), content ?? VanillaItems);
    }

    private static void CreateMod(TestWorkspace ws, string folder, string content)
    {
        var root = Path.Combine(ws.Mods, folder);
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        File.WriteAllText(Path.Combine(root, "ModInfo.xml"),
            TestWorkspace.ModernModInfo(folder, "1.0", "Tester"));
        File.WriteAllText(Path.Combine(root, "Config", "items.xml"), content);
    }

    private static ConflictReport Analyze(TestWorkspace ws)
    {
        var db = new Database(ws.Paths);
        db.Initialize();
        var mods = new InstalledModScanner(new InstalledStore(db)).Scan(ws.Mods);
        return new ConflictAnalyzer().AnalyzeInstalled(mods, new VanillaConfigResolver(ws.Game));
    }

    /// <summary>Conflict as the analyzer reports it, built by hand so a test can pick any xpath.</summary>
    private static ConflictEntry Conflict(string xpath, params (string Mod, string Value)[] writers)
    {
        var entry = new ConflictEntry
        {
            Kind = ConflictKind.ConflictingValue,
            Severity = FindingSeverity.Medium,
            Subject = "weapon_A / property Damage",
            Detail = "test",
            Confidence = 0.95
        };

        foreach (var (mod, value) in writers)
        {
            entry.Participants.Add(new ConflictParticipant
            {
                Mod = mod,
                File = "Config/items.xml",
                Operation = "set",
                XPath = xpath,
                OriginalValue = "50",
                NewValue = value
            });
        }

        return entry;
    }

    private static ConflictResolution Choice(string value) =>
        new() { Title = value, Description = "test", Value = value, CanExport = true };

    [Fact]
    public void Patch_file_is_a_configs_container_with_one_set_command()
    {
        using var ws = new TestWorkspace();

        var conflict = Conflict(DamageXPath, ("AAA_Mod", "100"), ("BBB_Mod", "200"));
        var result = new ConflictResolutionService().CreateMergedPatch(conflict, Choice("200"), ws.Mods);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.FolderPath);
        Assert.True(File.Exists(Path.Combine(result.FolderPath!, "ModInfo.xml")));

        var patchFile = Path.Combine(result.FolderPath!, "Config", "items.xml");
        Assert.True(File.Exists(patchFile));

        var document = XDocument.Load(patchFile);
        Assert.NotNull(document.Root);
        Assert.Equal("configs", document.Root!.Name.LocalName);

        var command = Assert.Single(document.Root!.Elements());
        Assert.Equal("set", command.Name.LocalName);
        Assert.Equal(DamageXPath, command.Attribute("xpath")!.Value);
        Assert.Equal("200", command.Value);

        // The project model reads a file with any other root as a wholesale replacement, which is
        // exactly what went wrong before: the patch would have replaced the whole config file.
        var analysis = ConfigFileAnalysis.Parse("Config/items.xml", File.ReadAllText(patchFile));
        Assert.True(analysis.Parsed, analysis.Error);
        Assert.False(analysis.FullReplacement);
        Assert.Equal(1, analysis.XPathOperations);
    }

    [Fact]
    public void Merged_patch_changes_the_value_in_a_copy_of_the_game_config()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        CreateMod(ws, "AAA_Mod", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <set xpath="/items/item[@name='weapon_A']/property[@name='Damage']/@value">100</set>
            </items>
            """);
        CreateMod(ws, "BBB_Mod", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <set xpath="/items/item[@name='weapon_A']/property[@name='Damage']/@value">200</set>
            </items>
            """);

        var report = Analyze(ws);
        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);

        var service = new ConflictResolutionService();
        var choice = service.Suggest(conflict).First(o => o.Value == "200");

        var result = service.CreateMergedPatch(conflict, choice, ws.Mods, gamePath: ws.Game);

        Assert.True(result.Success, result.Error);
        Assert.Contains(Localization.S("res.patchVerified"), result.Warnings);

        var patchFile = Path.Combine(result.FolderPath!, "Config", "items.xml");
        var analysis = ConfigFileAnalysis.Parse("Config/items.xml", File.ReadAllText(patchFile));
        Assert.True(analysis.Parsed, analysis.Error);

        var copy = new VanillaConfigResolver(ws.Game).CreateWorkingCopy("Config/items.xml");
        Assert.NotNull(copy);

        var simulation = ConfigPatchSimulator.Run(
            "Config/items.xml",
            copy,
            analysis.Patches.Select(p => ("ZZZ_MergedPatch", p)));

        Assert.All(simulation.Outcomes, o => Assert.Equal(PatchStatus.Applied, o.Status));

        var value = copy!.XPathSelectElement("/items/item[@name='weapon_A']/property[@name='Damage']")
            ?.Attribute("value")?.Value;
        Assert.Equal("200", value);
    }

    [Fact]
    public void ModInfo_of_the_patch_is_read_as_a_modern_mod()
    {
        using var ws = new TestWorkspace();

        var conflict = Conflict(DamageXPath, ("AAA_Mod", "100"), ("BBB_Mod", "200"));
        var result = new ConflictResolutionService().CreateMergedPatch(conflict, Choice("200"), ws.Mods);

        Assert.True(result.Success, result.Error);

        var metadata = ModInfoParser.Parse(File.ReadAllBytes(Path.Combine(result.FolderPath!, "ModInfo.xml")));

        Assert.Equal(ModInfoFormat.ValueAttribute, metadata.Format);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Name));
        Assert.Equal(Path.GetFileName(result.FolderPath!), metadata.Name);
        Assert.Equal("Universal 7 Days to Die Mod Manager", metadata.Author);
        Assert.Contains("200", metadata.Description);
    }

    [Fact]
    public void Patch_is_removed_when_the_game_config_has_no_target()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws, """
            <items>
              <item name="weapon_B">
                <property name="Damage" value="50"/>
              </item>
            </items>
            """);

        var conflict = Conflict(DamageXPath, ("AAA_Mod", "100"), ("BBB_Mod", "200"));
        var result = new ConflictResolutionService()
            .CreateMergedPatch(conflict, Choice("200"), ws.Mods, gamePath: ws.Game);

        Assert.False(result.Success);
        Assert.Null(result.FolderPath);
        Assert.NotNull(result.Error);
        Assert.Contains("weapon_A", result.Error ?? string.Empty);

        // A patch that cannot apply would only leave a mod that does nothing.
        Assert.Empty(Directory.GetDirectories(ws.Mods));
    }

    [Fact]
    public void Patch_without_a_game_path_is_reported_as_unverified()
    {
        using var ws = new TestWorkspace();

        var conflict = Conflict(DamageXPath, ("AAA_Mod", "100"), ("BBB_Mod", "200"));
        var result = new ConflictResolutionService().CreateMergedPatch(conflict, Choice("200"), ws.Mods);

        Assert.True(result.Success, result.Error);
        Assert.Contains(Localization.S("res.patchUnverified"), result.Warnings);
        Assert.DoesNotContain(Localization.S("res.patchVerified"), result.Warnings);
        Assert.NotNull(result.FolderPath);
    }

    [Fact]
    public void Two_patches_made_in_the_same_second_get_separate_folders()
    {
        using var ws = new TestWorkspace();

        var service = new ConflictResolutionService();
        var conflict = Conflict(DamageXPath, ("AAA_Mod", "100"), ("BBB_Mod", "200"));

        var first = service.CreateMergedPatch(conflict, Choice("100"), ws.Mods);
        var second = service.CreateMergedPatch(conflict, Choice("200"), ws.Mods);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.NotEqual(first.FolderPath, second.FolderPath);
        Assert.True(Directory.Exists(first.FolderPath!));
        Assert.True(Directory.Exists(second.FolderPath!));
    }

    [Fact]
    public void Markup_characters_in_the_value_and_the_xpath_do_not_break_the_patch()
    {
        using var ws = new TestWorkspace();

        const string xpath = "/items/item[@name='A&B']/property[@name='Damage']/@value";
        const string value = "R&D <test>";

        var conflict = Conflict(xpath, ("AAA_Mod", "100"), ("BBB_Mod", value));
        var result = new ConflictResolutionService().CreateMergedPatch(conflict, Choice(value), ws.Mods);

        Assert.True(result.Success, result.Error);

        var patchFile = Path.Combine(result.FolderPath!, "Config", "items.xml");
        var document = XDocument.Load(patchFile);
        var command = Assert.Single(document.Root!.Elements());

        Assert.Equal(xpath, command.Attribute("xpath")!.Value);
        Assert.Equal(value, command.Value);

        var metadata = ModInfoParser.Parse(File.ReadAllBytes(Path.Combine(result.FolderPath!, "ModInfo.xml")));
        Assert.Contains(value, metadata.Description);
    }
}
