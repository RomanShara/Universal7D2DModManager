using U7D2D.Core.Analysis;
using U7D2D.Core.Data;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

/// <summary>
/// Wholesale file replacement must be judged by content, not by the operation type:
/// an identical copy changes nothing, a copy that already contains the change keeps it,
/// and only a copy that dropped it is a real conflict.
/// </summary>
public sealed class ConflictReplacementTests
{
    private const string VanillaItems = """
        <items>
          <item name="weapon_A">
            <property name="Damage" value="50"/>
          </item>
        </items>
        """;

    private const string PatchFile = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <items>
          <set xpath="/items/item[@name='weapon_A']/property[@name='Damage']/@value">20</set>
        </items>
        """;

    private static void WriteVanilla(TestWorkspace ws)
    {
        var dir = Path.Combine(ws.Game, "Data", "Config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "items.xml"), VanillaItems);
    }

    private static void CreateMod(TestWorkspace ws, string folder, string content, string? dependency = null)
    {
        var root = Path.Combine(ws.Mods, folder);
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        File.WriteAllText(Path.Combine(root, "ModInfo.xml"),
            TestWorkspace.ModernModInfo(folder, "1.0", "Tester", dependency));
        File.WriteAllText(Path.Combine(root, "Config", "items.xml"), content);
    }

    private static ConflictReport Analyze(TestWorkspace ws)
    {
        var db = new Database(ws.Paths);
        db.Initialize();
        var mods = new InstalledModScanner(new InstalledStore(db)).Scan(ws.Mods);
        return new ConflictAnalyzer().AnalyzeInstalled(mods, new VanillaConfigResolver(ws.Game));
    }

    [Fact]
    public void Replacement_identical_to_the_game_file_is_not_a_conflict()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        CreateMod(ws, "AAA_Patch", PatchFile);
        CreateMod(ws, "ZZZ_Replace", VanillaItems);

        var report = Analyze(ws);

        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.SameConfigFile);
        Assert.Contains(report.Notes, n => n.Contains("copy of the original game file"));
    }

    [Fact]
    public void Replacement_that_already_contains_the_other_change_keeps_it()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        CreateMod(ws, "AAA_Patch", PatchFile);
        CreateMod(ws, "ZZZ_Replace", """
            <items>
              <item name="weapon_A">
                <property name="Damage" value="20"/>
              </item>
            </items>
            """);

        var report = Analyze(ws);

        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.SameConfigFile);
        Assert.Contains(report.Notes, n => n.Contains("already present"));
    }

    [Fact]
    public void Replacement_that_drops_the_other_change_is_a_real_conflict()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        CreateMod(ws, "AAA_Patch", PatchFile);

        // Differs from the game file (extra item) but has the old Damage value: the patch is lost.
        CreateMod(ws, "ZZZ_Replace", """
            <items>
              <item name="weapon_A">
                <property name="Damage" value="50"/>
              </item>
              <item name="weapon_B"/>
            </items>
            """);

        var report = Analyze(ws);

        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.SameConfigFile);
        Assert.Equal(FindingSeverity.High, conflict.Severity);
        Assert.Contains("AAA_Patch", conflict.Detail);
        Assert.Contains(conflict.Participants, p => p.XPath.Contains("Damage"));
    }

    private static void CreateModFile(TestWorkspace ws, string folder, string relativePath, string content)
    {
        var root = Path.Combine(ws.Mods, folder);
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var modInfo = Path.Combine(root, "ModInfo.xml");
        if (!File.Exists(modInfo))
            File.WriteAllText(modInfo, TestWorkspace.ModernModInfo(folder, "1.0", "Tester", null));

        File.WriteAllText(full, content);
    }

    [Fact]
    public void Window_reference_inside_a_group_is_not_a_definition()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        // Real case: Alter_Autominers lists craftingInfoPanel in its own window_group.
        CreateModFile(ws, "AAA_Group", "Config/XUi_InGame/xui.xml", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <configs>
              <append xpath="/xui">
                <window_group name="workstation_oilPumpjack" controller="XUiC_WorkstationWindowGroup">
                  <window name="craftingInfoPanel"/>
                </window_group>
              </append>
            </configs>
            """);

        // Another mod defines that window.
        CreateModFile(ws, "BBB_Window", "Config/XUi_InGame/windows.xml", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <configs>
              <append xpath="/windows">
                <window name="craftingInfoPanel" controller="XUiC_CraftingInfoWindow"/>
              </append>
            </configs>
            """);

        var report = Analyze(ws);

        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.SameXuiWindow);
    }

    [Fact]
    public void Two_mods_defining_the_same_window_are_reported()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        const string definition = """
            <?xml version="1.0" encoding="UTF-8" ?>
            <configs>
              <append xpath="/windows">
                <window name="playerModOptionsWindow" controller="XUiC_ModOptions"/>
              </append>
            </configs>
            """;

        CreateModFile(ws, "AAA_Window", "Config/XUi_InGame/windows.xml", definition);
        CreateModFile(ws, "BBB_Window", "Config/XUi_InGame/windows.xml", definition);

        var report = Analyze(ws);

        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.SameXuiWindow);
        Assert.Equal("playerModOptionsWindow", conflict.Subject);
    }

    [Fact]
    public void Empty_configs_file_is_not_a_replacement()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        CreateMod(ws, "AAA_Patch", PatchFile);

        // Real case: a mod ships an empty <configs></configs> and was treated as replacing the file.
        CreateMod(ws, "ZZZ_Empty", """
            <?xml version="1.0" encoding="utf-8"?>
            <configs>
            </configs>
            """);

        var report = Analyze(ws);

        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.SameConfigFile);
    }

    [Fact]
    public void Dependent_mod_loading_after_its_dependency_is_not_a_conflict()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        CreateMod(ws, "AAA_Core", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <append xpath="/items"><item name="sharedItem"/></append>
            </items>
            """);

        // Loads after AAA_Core and builds on it: overriding the node is the intended layering.
        CreateMod(ws, "BBB_Addon", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <append xpath="/items"><item name="sharedItem"/></append>
            </items>
            """, dependency: "AAA_Core");

        var report = Analyze(ws);

        Assert.DoesNotContain(report.Conflicts, c => c.Kind == ConflictKind.DuplicateNode);
        Assert.Contains(report.Notes, n => n.Contains("by design"));
    }

    [Fact]
    public void Dependent_mod_loading_before_its_dependency_is_a_real_conflict()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        // The dependent mod sorts first, so the mod it needs will overwrite its changes.
        CreateMod(ws, "AAA_Addon", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <append xpath="/items"><item name="sharedItem"/></append>
            </items>
            """, dependency: "ZZZ_Core");

        CreateMod(ws, "ZZZ_Core", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <append xpath="/items"><item name="sharedItem"/></append>
            </items>
            """);

        var report = Analyze(ws);

        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.DuplicateNode);
        Assert.Equal(FindingSeverity.High, conflict.Severity);
        Assert.Contains("loads earlier", conflict.Detail);
    }

    [Fact]
    public void Load_order_follows_folder_names()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        CreateMod(ws, "CCC_Mod", PatchFile);
        CreateMod(ws, "AAA_Mod", PatchFile);
        CreateMod(ws, "BBB_Mod", PatchFile);

        var report = Analyze(ws);

        Assert.Equal(new[] { "AAA_Mod", "BBB_Mod", "CCC_Mod" }, report.LoadOrder.ToArray());
    }

    [Fact]
    public void Winner_of_a_value_conflict_is_the_last_mod_by_folder_name()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        CreateMod(ws, "AAA_Mod", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <set xpath="/items/item[@name='weapon_A']/property[@name='Damage']/@value">100</set>
            </items>
            """);
        CreateMod(ws, "ZZZ_Mod", """
            <?xml version="1.0" encoding="UTF-8" ?>
            <items>
              <set xpath="/items/item[@name='weapon_A']/property[@name='Damage']/@value">200</set>
            </items>
            """);

        var report = Analyze(ws);
        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);

        Assert.Equal("ZZZ_Mod", conflict.Winner);
        Assert.Equal("200", conflict.FinalValue);
        Assert.Contains("ZZZ_Mod", conflict.Detail);
    }

    [Fact]
    public void Suggestions_merge_mods_that_write_the_same_value()
    {
        using var ws = new TestWorkspace();
        WriteVanilla(ws);

        var dir = Path.Combine(ws.Game, "Data", "Config");
        File.WriteAllText(Path.Combine(dir, "items.xml"), """
            <items>
              <item name="weapon_A" cols="9"/>
            </items>
            """);

        foreach (var (folder, value) in new[] { ("AAA_Mod", "8"), ("BBB_Mod", "8"), ("CCC_Mod", "10"), ("DDD_Mod", "10") })
        {
            CreateMod(ws, folder, $"""
                <?xml version="1.0" encoding="UTF-8" ?>
                <items>
                  <set xpath="/items/item[@name='weapon_A']/@cols">{value}</set>
                </items>
                """);
        }

        var report = Analyze(ws);
        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);
        Assert.Equal(4, conflict.Participants.Count);

        var options = new ConflictResolutionService().Suggest(conflict);

        // Two distinct mod values plus the game value: four mods must not produce four buttons.
        Assert.Equal(3, options.Count);
        Assert.Contains(options, o => o.Value == "8" && o.Description.Contains("AAA_Mod") && o.Description.Contains("BBB_Mod"));
        Assert.Contains(options, o => o.Value == "10" && o.Description.Contains("CCC_Mod"));
        Assert.Contains(options, o => o.Value == "9");
    }

    [Fact]
    public void Suggestions_offer_each_value_and_the_original()
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

        var options = new ConflictResolutionService().Suggest(conflict);

        Assert.Contains(options, o => o.Value == "100" && o.CanExport);
        Assert.Contains(options, o => o.Value == "200" && o.CanExport);
        Assert.Contains(options, o => o.Value == "50" && o.CanExport);
    }

    [Fact]
    public void Merged_patch_is_written_as_a_separate_mod_and_originals_stay_untouched()
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

        var originalPath = Path.Combine(ws.Mods, "AAA_Mod", "Config", "items.xml");
        var originalBefore = File.ReadAllText(originalPath);

        var report = Analyze(ws);
        var conflict = Assert.Single(report.Conflicts, c => c.Kind == ConflictKind.ConflictingValue);
        var choice = new ConflictResolutionService().Suggest(conflict).First(o => o.Value == "200");

        var result = new ConflictResolutionService().CreateMergedPatch(conflict, choice, ws.Mods);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.FolderPath);
        Assert.True(Directory.Exists(result.FolderPath!));
        Assert.True(File.Exists(Path.Combine(result.FolderPath!, "ModInfo.xml")));

        var patchFile = Path.Combine(result.FolderPath!, "Config", "items.xml");
        Assert.True(File.Exists(patchFile));
        Assert.Contains(">200<", File.ReadAllText(patchFile));

        Assert.Equal(originalBefore, File.ReadAllText(originalPath));
    }
}
