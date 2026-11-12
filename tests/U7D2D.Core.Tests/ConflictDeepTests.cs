using U7D2D.Core.Analysis;
using U7D2D.Core.Data;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

/// <summary>
/// Node-level conflict analysis: different nodes must not be reported, the same node with
/// different values must be, and each participant must carry its own xpath and values.
/// </summary>
public sealed class ConflictDeepTests
{
    private static string SetPatch(string xpath, string value) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <items>
          <set xpath="{xpath}">{value}</set>
        </items>
        """;

    private static string AppendPatch(string xpath, params string[] nodes) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <items>
          <append xpath="{xpath}">
            {string.Join("\n    ", nodes)}
          </append>
        </items>
        """;

    private static void CreateMod(TestWorkspace ws, string folder, params (string Path, string Content)[] files)
    {
        var root = Path.Combine(ws.Mods, folder);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ModInfo.xml"), TestWorkspace.ModernModInfo(folder, "1.0"));

        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private static ConflictReport Analyze(TestWorkspace ws, VanillaConfigResolver? resolver = null)
    {
        var db = new Database(ws.Paths);
        db.Initialize();
        var mods = new InstalledModScanner(new InstalledStore(db)).Scan(ws.Mods);
        return new ConflictAnalyzer().AnalyzeInstalled(mods, resolver);
    }

    private static void WriteVanillaItems(TestWorkspace ws, string content)
    {
        var dir = Path.Combine(ws.Game, "Data", "Config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "items.xml"), content);
    }

    private static void CreateModWithInfo(TestWorkspace ws, string folder, string name, string displayName, string version)
    {
        var root = Path.Combine(ws.Mods, folder);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ModInfo.xml"), $"""
            <?xml version="1.0" encoding="UTF-8" ?>
            <xml>
              <Name value="{name}" />
              <DisplayName value="{displayName}" />
              <Author value="Tester" />
              <Version value="{version}" />
            </xml>
            """);
    }

    [Fact]
    public void Different_mods_sharing_a_display_name_are_not_duplicates()
    {
        using var ws = new TestWorkspace();

        // Real case: two different mods that both call themselves "Vanilla".
        CreateModWithInfo(ws, "Zzzzxcpv_Classic_All_In_One", "Zzzzxcp_CP_Classic_All_In_One", "Vanilla", "3.3.3.3");
        CreateModWithInfo(ws, "Zzzzxcpv_Classic_Traders", "Zzzzxcpv_Classic_Traders", "Vanilla", "3.1.3.2");

        var report = Analyze(ws);

        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.DuplicateModName);
    }

    [Fact]
    public void Same_mod_in_two_folders_is_reported_by_technical_name()
    {
        using var ws = new TestWorkspace();
        CreateModWithInfo(ws, "SomeMod", "Real_Mod_Name", "Nice Label", "1.0");
        CreateModWithInfo(ws, "SomeMod_copy", "Real_Mod_Name", "Other Label", "1.0");

        var report = Analyze(ws);

        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.DuplicateModName);
        Assert.Equal(FindingSeverity.Critical, conflict.Severity);
        Assert.Equal("Real_Mod_Name", conflict.Subject);
        Assert.Contains("SomeMod", conflict.ModA + " " + conflict.ModB);
        Assert.Contains("SomeMod_copy", conflict.ModA + " " + conflict.ModB);
    }

    [Fact]
    public void Conditional_patch_is_not_reported_as_missing_target()
    {
        using var ws = new TestWorkspace();
        WriteVanillaItems(ws, "<items><item name=\"weapon_A\"/></items>");

        CreateMod(ws, "ModA", ("Config/items.xml", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <conditional>
                <if cond="mod_loaded('SomeOtherMod')">
                  <append xpath="/items/item[@name='NotInstalled']"/>
                </if>
              </conditional>
            </items>
            """));

        var report = Analyze(ws, new VanillaConfigResolver(ws.Game));

        // The game skips it too, so it must not be reported as a broken patch.
        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.PatchTargetMissing);
    }

    [Fact]
    public void Write_inside_an_existing_node_to_an_absent_child_is_a_version_mismatch()
    {
        using var ws = new TestWorkspace();
        WriteVanillaItems(ws, """
            <items>
              <item name="weapon_A">
                <property name="Damage" value="50"/>
              </item>
            </items>
            """);

        CreateMod(ws, "ModA", ("Config/items.xml",
            SetPatch("/items/item[@name='weapon_A']/property[@name='Ghost']/@value", "1")));

        var report = Analyze(ws, new VanillaConfigResolver(ws.Game));

        var finding = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.PatchTargetMissing);
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Contains("another game version", finding.Detail);
        Assert.Contains("path changed", finding.Participants[0].Note);
    }

    [Fact]
    public void Append_to_an_absent_named_node_is_reported_as_low()
    {
        using var ws = new TestWorkspace();
        WriteVanillaItems(ws, "<items><item name=\"weapon_A\"/></items>");

        CreateMod(ws, "ModA", ("Config/items.xml",
            AppendPatch("/items/item[@name='CompanionVehicleNotInstalled']", "<property name=\"x\" value=\"1\"/>")));

        var report = Analyze(ws, new VanillaConfigResolver(ws.Game));

        var finding = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.PatchTargetMissing);
        Assert.Equal(FindingSeverity.Low, finding.Severity);
        Assert.Contains("target missing", finding.Participants[0].Note);
    }

    [Fact]
    public void Removing_a_node_that_does_not_exist_is_harmless()
    {
        using var ws = new TestWorkspace();
        WriteVanillaItems(ws, """
            <items>
              <item name="weapon_A">
                <property name="Damage" value="50"/>
              </item>
            </items>
            """);

        CreateMod(ws, "ModA", ("Config/items.xml", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <remove xpath="/items/item[@name='weapon_A']/property[@name='Gone']"/>
            </items>
            """));

        var report = Analyze(ws, new VanillaConfigResolver(ws.Game));

        var finding = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.PatchTargetMissing);
        Assert.Equal(FindingSeverity.Low, finding.Severity);
        Assert.Contains("nothing to remove", finding.Participants[0].Note);
    }

    [Fact]
    public void Different_attributes_of_one_element_are_an_overlap()
    {
        using var ws = new TestWorkspace();
        WriteVanillaItems(ws, "<items><item name=\"weapon_A\" damage=\"10\" range=\"5\"/></items>");

        CreateMod(ws, "ModA", ("Config/items.xml", SetPatch("/items/item[@name='weapon_A']/@damage", "20")));
        CreateMod(ws, "ModB", ("Config/items.xml", SetPatch("/items/item[@name='weapon_A']/@range", "8")));

        var report = Analyze(ws, new VanillaConfigResolver(ws.Game));

        var overlap = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.SameNodeOverlap);
        Assert.Equal(ConflictRelation.Overlap, overlap.Relation);
        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);
    }

    [Fact]
    public void Conflict_kinds_map_to_the_four_relations()
    {
        Assert.Equal(ConflictRelation.Overlap, new ConflictEntry { Kind = ConflictKind.SameNodeOverlap }.Relation);
        Assert.Equal(ConflictRelation.Potential, new ConflictEntry { Kind = ConflictKind.ConflictingValue }.Relation);
        Assert.Equal(ConflictRelation.Conflict, new ConflictEntry { Kind = ConflictKind.DuplicateNode }.Relation);
        Assert.Equal(ConflictRelation.Conflict, new ConflictEntry { Kind = ConflictKind.RemoveVsModify }.Relation);
    }

    [Fact]
    public void Different_nodes_in_one_file_are_not_a_conflict()
    {
        using var ws = new TestWorkspace();
        CreateMod(ws, "ModA", ("Config/items.xml", AppendPatch("/items", "<item name=\"weapon_A\"/>")));
        CreateMod(ws, "ModB", ("Config/items.xml", AppendPatch("/items", "<item name=\"weapon_B\"/>")));

        var report = Analyze(ws);

        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.SameConfigFile);
        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);
        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.DuplicateNode);
        Assert.Contains(report.Notes, n => n.Contains("different nodes"));
    }

    [Fact]
    public void Same_attribute_with_different_values_is_a_confirmed_contradiction()
    {
        using var ws = new TestWorkspace();
        const string xpath = "/items/item[@name='weapon_A']/property[@name='Damage']/@value";

        CreateMod(ws, "ModA", ("Config/items.xml", SetPatch(xpath, "100")));
        CreateMod(ws, "ModB", ("Config/items.xml", SetPatch(xpath, "200")));

        var report = Analyze(ws);

        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);
        Assert.Equal(FindingSeverity.Medium, conflict.Severity);
        Assert.EndsWith("/@value", conflict.Subject);

        Assert.Equal(2, conflict.Participants.Count);
        Assert.Contains(conflict.Participants, p => p.Mod == "ModA" && p.NewValue == "100");
        Assert.Contains(conflict.Participants, p => p.Mod == "ModB" && p.NewValue == "200");
        Assert.All(conflict.Participants, p => Assert.Equal(xpath, p.XPath));
        Assert.All(conflict.Participants, p => Assert.Equal("set", p.Operation));
    }

    [Fact]
    public void Same_value_from_both_mods_is_reported_as_duplication_not_conflict()
    {
        using var ws = new TestWorkspace();
        const string xpath = "/items/item[@name='weapon_A']/property[@name='Damage']/@value";

        CreateMod(ws, "ModA", ("Config/items.xml", SetPatch(xpath, "100")));
        CreateMod(ws, "ModB", ("Config/items.xml", SetPatch(xpath, "100")));

        var report = Analyze(ws);

        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.RedundantWrite);
        Assert.Equal(FindingSeverity.Low, conflict.Severity);
        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);
    }

    [Fact]
    public void Original_value_is_taken_from_the_installed_game_config()
    {
        using var ws = new TestWorkspace();
        WriteVanillaItems(ws, """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <item name="weapon_A">
                <property name="Damage" value="50"/>
              </item>
            </items>
            """);

        const string xpath = "/items/item[@name='weapon_A']/property[@name='Damage']/@value";
        CreateMod(ws, "ModA", ("Config/items.xml", SetPatch(xpath, "100")));
        CreateMod(ws, "ModB", ("Config/items.xml", SetPatch(xpath, "200")));

        var resolver = new VanillaConfigResolver(ws.Game);
        Assert.True(resolver.IsAvailable);

        var report = Analyze(ws, resolver);
        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);

        Assert.All(conflict.Participants, p =>
        {
            Assert.Equal("50", p.OriginalValue);
            Assert.True(p.HasOriginalValue);
        });
    }

    [Fact]
    public void Two_mods_adding_a_node_with_the_same_name_are_reported()
    {
        using var ws = new TestWorkspace();
        CreateMod(ws, "ModA", ("Config/items.xml", AppendPatch("/items", "<item name=\"spherePickUpNPC\"/>")));
        CreateMod(ws, "ModB", ("Config/items.xml", AppendPatch("/items", "<item name=\"spherePickUpNPC\"/>")));

        var report = Analyze(ws);

        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.DuplicateNode);
        Assert.Contains("spherePickUpNPC", conflict.Subject);
        Assert.Equal(2, conflict.Participants.Count);
    }

    [Fact]
    public void Nameless_appended_nodes_are_not_treated_as_duplicates()
    {
        using var ws = new TestWorkspace();
        CreateMod(ws, "ModA", ("Config/blocks.xml", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <blocks>
              <append xpath="/blocks/block[@name='terrStone']">
                <drop event="Harvest" name="resourceCoal"/>
              </append>
            </blocks>
            """));
        CreateMod(ws, "ModB", ("Config/blocks.xml", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <blocks>
              <append xpath="/blocks/block[@name='terrStone']">
                <drop event="Harvest" name="resourceCoal"/>
              </append>
            </blocks>
            """));

        var report = Analyze(ws);

        // "drop" may repeat by design; only named unique collections take part in duplicate detection.
        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.DuplicateNode);
    }

    [Fact]
    public void Remove_against_write_is_reported_as_a_contradiction()
    {
        using var ws = new TestWorkspace();
        const string xpath = "/items/item[@name='weapon_A']/property[@name='Damage']";

        CreateMod(ws, "ModA", ("Config/items.xml", $"""
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <remove xpath="{xpath}"/>
            </items>
            """));
        CreateMod(ws, "ModB", ("Config/items.xml", SetPatch(xpath, "200")));

        var report = Analyze(ws);

        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.RemoveVsModify);
        Assert.Equal(FindingSeverity.High, conflict.Severity);
    }
}
