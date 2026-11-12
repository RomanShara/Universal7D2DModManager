using System.Text;
using U7D2D.Core.Analysis;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

public sealed class AnalysisTests
{
    private static GameInstallInfo Game(TestWorkspace ws) => new()
    {
        IsValid = true,
        GamePath = ws.Game,
        VersionString = "1.0 (b333)",
        VersionSource = "version.txt",
        ManagedAssemblyPath = ws.FixturePath("Assembly-CSharp.dll")
    };

    [Fact]
    public void Game_api_index_contains_real_types_and_members()
    {
        using var ws = new TestWorkspace();
        var index = GameApiIndex.Load(ws.FixturePath("Assembly-CSharp.dll"));

        Assert.NotNull(index);
        Assert.True(index!.HasType("GameCode.Block"));
        Assert.True(index.HasMember("GameCode.Block", "GetBlock"));
        Assert.False(index.HasMember("GameCode.Block", "LegacyCall"));
        Assert.True(index.TypeCount > 0);
    }

    [Fact]
    public void Assembly_inspector_detects_references_to_removed_game_api()
    {
        using var ws = new TestWorkspace();
        var index = GameApiIndex.Load(ws.FixturePath("Assembly-CSharp.dll"));
        var bytes = File.ReadAllBytes(ws.FixturePath("TestMod.dll"));

        var analysis = AssemblyInspector.Analyze(bytes, "TestMod.dll", index);

        Assert.True(analysis.Loadable, analysis.Error);
        Assert.True(analysis.ReferencesGameAssembly);
        Assert.Equal(".NET Standard", analysis.RuntimeFamily);
        Assert.Contains("GameCode.Block", analysis.GameTypesReferenced);
        Assert.Contains(analysis.MissingGameMembers, m => m.Contains("LegacyCall"));
        Assert.Empty(analysis.MissingGameTypes);
    }

    [Fact]
    public void Analyzer_without_game_returns_unknown_and_zero_confidence()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("ModA.zip", ("ModA/ModInfo.xml", TestWorkspace.ModernModInfo("Mod A", "1.0")));
        var package = new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single();

        var report = new CompatibilityAnalyzer(ws.Paths).Analyze(
            package, new AppSettings { GamePath = string.Empty }, new GameInstallInfo { Problems = { "нет пути" } }, []);

        Assert.Equal(CompatibilityVerdict.Unknown, report.Verdict);
        Assert.Equal(0, report.Confidence);
        Assert.Contains(report.Findings, f => f.Severity == FindingSeverity.High);
    }

    [Fact]
    public void Analyzer_flags_missing_game_api_as_likely_incompatible()
    {
        using var ws = new TestWorkspace();
        var dll = File.ReadAllBytes(ws.FixturePath("TestMod.dll"));

        var zip = ws.WriteBinaryZip("ModA.zip",
            ("ModA/ModInfo.xml", Encoding.UTF8.GetBytes(TestWorkspace.ModernModInfo("Mod A", "1.0"))),
            ("ModA/Harmony/TestMod.dll", dll));

        var package = new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single();
        var settings = new AppSettings { GamePath = ws.Game, ModsPath = ws.Mods };

        var report = new CompatibilityAnalyzer(ws.Paths).Analyze(package, settings, Game(ws), []);

        Assert.Equal(CompatibilityVerdict.LikelyIncompatible, report.Verdict);
        Assert.True(report.Confidence >= 0.78);
        Assert.Contains(report.Findings, f =>
            f.Severity == FindingSeverity.Critical && f.Evidence.Contains("LegacyCall"));
        Assert.Contains(report.NotVerified, n => n.Contains("launch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Xml_only_mod_is_likely_compatible_but_not_verified()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("XmlOnly.zip",
            ("XmlOnly/ModInfo.xml", TestWorkspace.ModernModInfo("XML Only", "1.0")),
            ("XmlOnly/Config/blocks.xml", TestWorkspace.BlocksPatch("x")));

        var package = new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single();
        var settings = new AppSettings { GamePath = ws.Game, ModsPath = ws.Mods };

        var report = new CompatibilityAnalyzer(ws.Paths).Analyze(package, settings, Game(ws), []);

        Assert.Equal(CompatibilityVerdict.LikelyCompatible, report.Verdict);
        Assert.InRange(report.Confidence, 0.5, 0.7);
        Assert.Contains(report.Findings, f => f.Title.Contains("XPath"));
    }

    [Fact]
    public void Legacy_modinfo_alone_is_not_treated_as_incompatible()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("Legacy.zip",
            ("Legacy/ModInfo.xml", TestWorkspace.LegacyModInfo("Legacy Mod", "Alpha 16.4")),
            ("Legacy/Config/items.xml", "<items/>"));

        var package = new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single();
        var settings = new AppSettings { GamePath = ws.Game, ModsPath = ws.Mods };

        var report = new CompatibilityAnalyzer(ws.Paths).Analyze(package, settings, Game(ws), []);

        Assert.NotEqual(CompatibilityVerdict.Incompatible, report.Verdict);
        Assert.NotEqual(CompatibilityVerdict.LikelyIncompatible, report.Verdict);
        Assert.Contains(report.Findings, f => f.Title.Contains("Old ModInfo.xml format"));
        Assert.Contains(report.Findings, f => f.Severity == FindingSeverity.Low);
    }

    [Fact]
    public void Missing_required_dependency_is_reported()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteZip("NeedsDep.zip",
            ("NeedsDep/ModInfo.xml", TestWorkspace.ModernModInfo("Needs Dep", "1.0", dependency: "Other Mod")));

        var package = new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single();
        var settings = new AppSettings { GamePath = ws.Game, ModsPath = ws.Mods };

        var report = new CompatibilityAnalyzer(ws.Paths).Analyze(package, settings, Game(ws), []);

        Assert.Contains(report.Findings, f =>
            f.Category == "Dependencies" && f.Severity == FindingSeverity.High && f.Title.Contains("Other Mod"));
    }

    [Fact]
    public void Assembly_inspector_checks_harmony_patch_targets()
    {
        using var ws = new TestWorkspace();
        var index = GameApiIndex.Load(ws.FixturePath("Assembly-CSharp.dll"));
        var bytes = File.ReadAllBytes(ws.FixturePath("TestModHarmony.dll"));

        var analysis = AssemblyInspector.Analyze(bytes, "TestModHarmony.dll", index);

        Assert.True(analysis.Loadable, analysis.Error);
        Assert.Contains(analysis.HarmonyPatchTargets, t => t.DeclaringType == "GameCode.Block" && t.MemberName == "SetName");
        Assert.Contains(analysis.HarmonyPatchTargets, t => t.DeclaringType == "GameCode.Block" && t.MemberName == "LegacyCall");

        // SetName still exists in this build, LegacyCall was removed: only the latter is reported.
        Assert.DoesNotContain("GameCode.Block.SetName", analysis.MissingHarmonyTargets);
        Assert.Contains("GameCode.Block.LegacyCall", analysis.MissingHarmonyTargets);
    }

    [Fact]
    public void Analyzer_reports_removed_harmony_patch_target()
    {
        using var ws = new TestWorkspace();
        var zip = ws.WriteBinaryZip("PatchMod.zip",
            ("PatchMod/ModInfo.xml", Encoding.UTF8.GetBytes(TestWorkspace.ModernModInfo("Patch Mod", "1.0"))),
            ("PatchMod/TestModHarmony.dll", File.ReadAllBytes(ws.FixturePath("TestModHarmony.dll"))));

        var package = new ArchiveScanner(ws.Paths).Scan(zip).Mods.Single();
        var settings = new AppSettings { GamePath = ws.Game, ModsPath = ws.Mods };

        var report = new CompatibilityAnalyzer(ws.Paths).Analyze(package, settings, Game(ws), []);

        var finding = Assert.Single(report.Findings, f => f.Title.Contains("Harmony patches on missing methods"));
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Contains("LegacyCall", finding.Evidence);
        Assert.DoesNotContain("SetName", finding.Evidence);
        Assert.Equal(CompatibilityVerdict.Risky, report.Verdict);
    }

    [Fact]
    public void Conflict_analyzer_detects_shared_config_and_xpath_targets()
    {
        using var ws = new TestWorkspace();
        CreateMod(ws, "ModOne", "1.0", ("Config/blocks.xml", TestWorkspace.BlocksPatch("shared")));
        CreateMod(ws, "ModTwo", "1.0", ("Config/blocks.xml", TestWorkspace.BlocksFull("shared")));

        var db = new Data.Database(ws.Paths);
        db.Initialize();
        var mods = new InstalledModScanner(new Data.InstalledStore(db)).Scan(ws.Mods);

        var report = new ConflictAnalyzer().AnalyzeInstalled(mods);

        Assert.Contains(report.Conflicts, c => c.Kind == ConflictKind.SameConfigFile && c.Subject.Contains("blocks.xml"));
    }

    private static void CreateMod(TestWorkspace ws, string folder, string version, params (string Path, string Content)[] files)
    {
        var root = Path.Combine(ws.Mods, folder);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ModInfo.xml"), TestWorkspace.ModernModInfo(folder, version));

        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }
}
