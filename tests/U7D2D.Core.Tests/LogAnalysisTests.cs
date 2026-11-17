using System.IO;
using U7D2D.Core.I18n;
using U7D2D.Core.Logs;
using U7D2D.Core.Models;
using Xunit;

namespace U7D2D.Core.Tests;

public sealed class LogAnalysisTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Harmony_reader_finds_patch_targets_without_resolving_them()
    {
        var bytes = File.ReadAllBytes(Fixture("TestModHarmony.dll"));
        var targets = HarmonyTargetReader.Read(bytes);

        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.False(string.IsNullOrWhiteSpace(t.DeclaringType)));
    }

    [Fact]
    public void Index_resolves_a_type_defined_in_a_mod_dll()
    {
        var workspace = new TestWorkspace();
        var modFolder = Path.Combine(workspace.Root, "SomeMod");
        Directory.CreateDirectory(modFolder);
        File.Copy(Fixture("TestMod.dll"), Path.Combine(modFolder, "TestMod.dll"), overwrite: true);

        var index = ModTypeIndex.BuildFromFolder(workspace.Root);

        Assert.True(index.TypeCount > 0);
        Assert.Equal(1, index.ModCount);
    }

    [Fact]
    public void Parser_reads_exception_frames()
    {
        var lines = new[]
        {
            "2026-11-12T23:04:29 0.322 WRN [Platform] something harmless",
            "NullReferenceException: Object reference not set to an instance of an object",
            "  at WireManager.Cleanup () [0x00000] in <b181c0063dca421180fe9efc360eeb10>:0 ",
            "  at GameManager.SaveAndCleanupWorld () [0x005ff] in <b181c0063dca421180fe9efc360eeb10>:0 ",
            "2026-11-12T23:04:30 1.000 INF [World] next line"
        };

        var entries = GameLogParser.Parse(lines, out var count);
        var exception = entries.Single(e => e.ExceptionType is not null);

        Assert.Equal(5, count);
        Assert.Equal("NullReferenceException", exception.ExceptionType);
        Assert.Equal(2, exception.Frames.Count);
        Assert.Equal("WireManager", exception.Frames[0].TypeName);
        Assert.Equal("Cleanup", exception.Frames[0].MemberName);
        Assert.Equal("GameManager", exception.Frames[1].TypeName);
    }

    [Fact]
    public void Analyzer_blames_the_mod_that_defines_the_top_frame()
    {
        var workspace = new TestWorkspace();
        var modFolder = Path.Combine(workspace.Root, "SomeMod");
        Directory.CreateDirectory(modFolder);
        File.Copy(Fixture("TestMod.dll"), Path.Combine(modFolder, "TestMod.dll"), overwrite: true);

        var index = ModTypeIndex.BuildFromFolder(workspace.Root);
        var typeName = FirstTypeOf(index);
        Assert.False(string.IsNullOrEmpty(typeName));

        var lines = new[]
        {
            "NullReferenceException: Object reference not set to an instance of an object",
            $"  at {typeName}.DoSomething () [0x00000] in <hash>:0 "
        };

        var entries = GameLogParser.Parse(lines, out _);
        var report = new LogAnalyzer().Analyze(entries, index);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(LogConfidence.High, finding.Confidence);
        Assert.Equal("SomeMod", finding.Primary!.ModFolder);
    }

    [Fact]
    public void Analyzer_does_not_blame_a_mod_when_only_game_code_is_involved()
    {
        var workspace = new TestWorkspace();
        var modFolder = Path.Combine(workspace.Root, "SomeMod");
        Directory.CreateDirectory(modFolder);
        File.Copy(Fixture("TestMod.dll"), Path.Combine(modFolder, "TestMod.dll"), overwrite: true);

        var index = ModTypeIndex.BuildFromFolder(workspace.Root);

        var lines = new[]
        {
            "NullReferenceException: Object reference not set to an instance of an object",
            "  at WireManager.Cleanup () [0x00000] in <hash>:0 ",
            "  at GameManager.SaveAndCleanupWorld () [0x005ff] in <hash>:0 "
        };

        var entries = GameLogParser.Parse(lines, out _);
        var report = new LogAnalyzer().Analyze(entries, index);
        var finding = Assert.Single(report.Findings);

        Assert.Empty(finding.Suspects);
        Assert.Equal(LogConfidence.None, finding.Confidence);
    }

    [Theory]
    [InlineData("Loading localization from mod: CasesMod")]
    [InlineData("Trying to load from folder: '00000-Gears'")]
    public void Blames_a_mod_named_in_the_message(string message)
    {
        var workspace = new TestWorkspace();
        var folder = message.Contains("Gears") ? "00000-Gears" : "Zzzzzzzzzzzzzzzz_CasesMod";
        Directory.CreateDirectory(Path.Combine(workspace.Root, folder));

        var index = ModTypeIndex.BuildFromFolder(workspace.Root);

        var lines = new[] { $"2026-11-12T23:04:59 29.911 WRN [MODS] {message}" };
        var entries = GameLogParser.Parse(lines, out _);

        // Warnings are kept only when they look like failures, so build the entry directly.
        var entry = new LogEntry
        {
            Severity = LogSeverity.Warning,
            Tag = "MODS",
            Message = message + " failed"
        };

        var report = new LogAnalyzer().Analyze(new[] { entry }, index);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(LogConfidence.High, finding.Confidence);
        Assert.Equal(folder, finding.Primary!.ModFolder);
    }

    [Fact]
    public void Comparison_finds_new_and_removed_mods_and_the_gap_before_the_error()
    {
        var previous = new[]
        {
            "2026-11-12T20:00:00 1.0 INF [MODS] Loading localization from mod: OldMod",
            "2026-11-12T20:00:01 1.0 INF [MODS] Loading localization from mod: CasesMod"
        };

        var current = new[]
        {
            "2026-11-12T23:04:00 1.0 INF [MODS] Loading localization from mod: CasesMod",
            "2026-11-12T23:04:30 2.0 INF [MODS]   Trying to load from folder: '00000-Gears'",
            "2026-11-12T23:07:30 5.0 ERR Something failed"
        };

        var prev = LogComparison.Scan(previous);
        var cur = LogComparison.Scan(current);

        var comparison = LogComparison.Build(
            cur.Mods, prev.Mods, cur.FirstError, new[] { "CasesMod", "00000-Gears", "NotLoadedMod" });

        Assert.True(comparison.HasPreviousSession);

        // Loaded now, not before.
        Assert.Equal("00000-Gears", Assert.Single(comparison.NewInCurrent).Name);

        // Loaded before, not now.
        Assert.Equal("OldMod", Assert.Single(comparison.MissingInCurrent));

        // Three sources stay separate: the folder is current state, not history.
        Assert.Equal(3, comparison.InstalledNow.Count);
        Assert.Equal("NotLoadedMod", Assert.Single(comparison.InstalledNotLoaded));
        Assert.Empty(comparison.LoadedNotInstalled);

        var minutes = LogComparison.MinutesBefore(comparison.NewInCurrent[0], comparison.FirstError);
        Assert.Equal(3, minutes);
    }

    [Fact]
    public void Saved_settings_warning_is_a_grouped_notice_without_a_culprit()
    {
        var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "00000-Gears"));
        Directory.CreateDirectory(Path.Combine(workspace.Root, "TargetAnalyzer"));

        var index = ModTypeIndex.BuildFromFolder(workspace.Root);

        var entries = new[] { "BoostResistance", "BoostHealth", "VariantShell" }.Select(name =>
            new LogEntry
            {
                Severity = LogSeverity.Warning,
                Tag = "Gears",
                Message = $"[SavedModSettingsFromXml] Mod Dir2104_TargetAnalyzer : Setting {name} not found, " +
                          "unable to load the current mod setting's current value, using default value"
            });

        var report = new LogAnalyzer().Analyze(entries, index);
        var finding = Assert.Single(report.Findings);

        Assert.True(finding.IsNotice);
        Assert.Equal(LogConfidence.None, finding.Confidence);
        Assert.Empty(finding.Suspects);
        Assert.Equal(3, finding.Occurrences);
        Assert.Equal(3, finding.Details.Count);
        Assert.NotEmpty(finding.Suggestions);
    }

    [Fact]
    public void Repeated_warnings_that_differ_only_by_coordinates_become_one_finding()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        var entries = new[] { "183, 59, 944", "179, 53, 967", "117, 68, 929" }.Select(pos =>
            new LogEntry
            {
                Severity = LogSeverity.Warning,
                Tag = "MultiBlockManager",
                Message = "[Alignment] SetTerrainAlignmentDirty failed; no terrain-aligned block " +
                          $"has been registered at the specified world position: {pos}"
            });

        var finding = Assert.Single(new LogAnalyzer().Analyze(entries, index).Findings);
        Assert.Equal(3, finding.Occurrences);
    }

    [Fact]
    public void An_assembly_inside_a_mod_folder_counts_as_installed()
    {
        var workspace = new TestWorkspace();
        var host = Path.Combine(workspace.Root, "0-Host");
        Directory.CreateDirectory(host);
        File.Copy(Fixture("TestMod.dll"), Path.Combine(host, "1-XNPCVoiceControl.dll"), overwrite: true);

        var index = ModTypeIndex.BuildFromFolder(workspace.Root);

        Assert.Equal("0-Host", index.AssemblyFolders["1-XNPCVoiceControl"]);

        var entry = new LogEntry
        {
            Severity = LogSeverity.Error,
            Tag = "",
            Message = "No action class 'NPCNoVoiceTag, 1-XNPCVoiceControl found!"
        };

        var finding = Assert.Single(new LogAnalyzer().Analyze(new[] { entry }, index).Findings);

        // The assembly is installed, so the report talks about a missing class. It does not turn
        // that into an accusation against the mod.
        Assert.Empty(finding.Suspects);
        Assert.Equal(LogConfidence.None, finding.Confidence);
        Assert.Contains(finding.Suggestions, x => x.Key == "log.hintMissingActionClass");
        Assert.DoesNotContain(finding.Suggestions, x => x.Key == "log.hintModNotInstalled");
    }

    [Fact]
    public void Folder_name_with_an_apostrophe_is_read_in_full()
    {
        var lines = new[]
        {
            "2026-11-13T05:11:52 4.682 INF [MODS]   Trying to load from folder: 'Vayben's Mega Cities for v3.3'",
            "2026-11-13T05:12:20 32.181 INF Loading localization from mod: VaybensMegaCities"
        };

        var scanned = LogComparison.Scan(lines);

        Assert.Contains("Vayben's Mega Cities for v3.3", scanned.Mods.Keys);
        Assert.DoesNotContain("Vayben", scanned.Mods.Keys);
    }

    [Fact]
    public void An_alias_marks_its_folder_as_loaded()
    {
        var current = new Dictionary<string, DateTime?> { ["Dir2104_TargetAnalyzer"] = null };

        var comparison = LogComparison.Build(
            current, null, null,
            new[] { "TargetAnalyzer" },
            new Dictionary<string, string> { ["Dir2104_TargetAnalyzer"] = "TargetAnalyzer" });

        Assert.Empty(comparison.LoadedNotInstalled);
        Assert.Empty(comparison.InstalledNotLoaded);
    }

    [Fact]
    public void Controller_rule_talks_about_the_controller_not_a_missing_mod()
    {
        var workspace = new TestWorkspace();
        var host = Path.Combine(workspace.Root, "0-XNPCCore");
        Directory.CreateDirectory(host);
        File.Copy(Fixture("TestMod.dll"), Path.Combine(host, "1-XNPCVoiceControl.dll"), overwrite: true);

        var index = ModTypeIndex.BuildFromFolder(workspace.Root);
        var entry = new LogEntry
        {
            Severity = LogSeverity.Error,
            Tag = "XUi",
            Message = "Controller 'XUiC_NpcIngredientEntry, 1-XNPCVoiceControl' not found, " +
                      "using base XUiController --- hierarchy: windows -> window (windowNpcCraftInfo)"
        };

        var finding = Assert.Single(new LogAnalyzer().Analyze(new[] { entry }, index).Findings);

        Assert.Equal("log.titleControllerMissing", finding.TitleLoc!.Key);
        Assert.Empty(finding.Suspects);
        Assert.Contains(finding.Suggestions, x => x.Key == "log.hintControllerMissing");
        Assert.DoesNotContain(finding.Suggestions, x => x.Key == "log.hintModNotInstalled");
    }

    [Fact]
    public void Harmony_type_and_method_are_separate_and_never_name_a_culprit()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        var entries = new[]
        {
            new LogEntry
            {
                Severity = LogSeverity.Warning, Tag = "MODS",
                Message = "[Harmony] AccessTools.TypeByName: Could not find type named Gravebound.Vault.UI.VaultUiBootstrap"
            },
            new LogEntry
            {
                Severity = LogSeverity.Warning, Tag = "MODS",
                Message = "[Harmony] AccessTools.DeclaredMethod: Could not find method for type EntityHuman and name GetMoveSpeedPanic and parameters"
            }
        };

        var report = new LogAnalyzer().Analyze(entries, index);

        Assert.Equal(2, report.Findings.Count);
        Assert.Contains(report.Findings, f => f.TitleLoc?.Key == "log.titleMissingType");
        Assert.Contains(report.Findings, f => f.TitleLoc?.Key == "log.titleMissingMethod");
        Assert.All(report.Findings, f => Assert.Empty(f.Suspects));
    }

    [Fact]
    public void General_advice_is_shown_once_not_repeated_on_every_card()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        var entries = new[] { "first", "second", "third" }.Select(word =>
            new LogEntry
            {
                Severity = LogSeverity.Warning,
                Tag = "Generic",
                Message = $"{word} failed"
            });

        var report = new LogAnalyzer().Analyze(entries, index);

        Assert.Equal(2, report.Notes.Count);
        Assert.Equal(3, report.Findings.Count);
        Assert.All(report.Findings, f =>
            Assert.DoesNotContain(f.Suggestions, x => x.Key == "log.notesNoCulprit"));
    }

    [Fact]
    public void Repeated_props_warning_is_one_scenario_with_first_and_last_time()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);
        var start = new DateTime(2026, 11, 13, 5, 19, 17);

        var entries = Enumerable.Range(0, 5).Select(i => new LogEntry
        {
            Severity = LogSeverity.Warning,
            Tag = "PROPS",
            Time = start.AddMinutes(i * 3),
            Message = "ActionTarget implicitly converted to Vector3, but failed: ActionTarget"
        });

        var finding = Assert.Single(new LogAnalyzer().Analyze(entries, index).Findings);

        Assert.Equal("log.titlePropsConversion", finding.TitleLoc!.Key);
        Assert.Equal(5, finding.Occurrences);
        Assert.Equal(start, finding.FirstSeen);
        Assert.Equal(start.AddMinutes(12), finding.LastSeen);
    }

    [Fact]
    public void Controllers_from_one_assembly_share_a_card_and_list_their_names()
    {
        var workspace = new TestWorkspace();
        var host = Path.Combine(workspace.Root, "0-XNPCCore");
        Directory.CreateDirectory(host);
        File.Copy(Fixture("TestMod.dll"), Path.Combine(host, "1-XNPCVoiceControl.dll"), overwrite: true);

        var index = ModTypeIndex.BuildFromFolder(workspace.Root);
        var entries = new[] { "XUiC_First", "XUiC_Second", "XUiC_Third" }.Select(name =>
            new LogEntry
            {
                Severity = LogSeverity.Error,
                Tag = "XUi",
                Message = $"Controller '{name}, 1-XNPCVoiceControl' not found, using base XUiController"
            });

        var report = new LogAnalyzer().Analyze(entries, index);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(3, finding.Occurrences);
        Assert.Equal(3, finding.Details.Count);
        Assert.Equal(3, finding.DistinctValues);
        Assert.Contains(finding.Suggestions, x => x.Key == "log.hintControllerPath");
        Assert.Empty(finding.Suspects);
    }

    [Fact]
    public void Xml_patch_failures_group_by_mod_and_do_not_accuse_anyone()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        var entries = new[] { "progression.xml", "items.xml", "blocks.xml" }.Select(file =>
            new LogEntry
            {
                Severity = LogSeverity.Warning,
                Tag = "",
                Message = $"XML patch for \"{file}\" from mod \"OPMods\" did not apply: <append xpath=\"/items\"  (line 50 at pos 5)"
            });

        var finding = Assert.Single(new LogAnalyzer().Analyze(entries, index).Findings);

        Assert.Equal("log.titleXmlPatchMod", finding.TitleLoc!.Key);
        Assert.Equal(3, finding.Occurrences);
        Assert.Equal(3, finding.Details.Count);
        Assert.Empty(finding.Suspects);
    }

    [Fact]
    public void A_bare_mod_name_in_a_message_is_not_a_culprit()
    {
        var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "OPMods"));
        var index = ModTypeIndex.BuildFromFolder(workspace.Root);

        var entry = new LogEntry
        {
            Severity = LogSeverity.Warning,
            Tag = "",
            Message = "Something failed while OPMods was loading its config"
        };

        var finding = Assert.Single(new LogAnalyzer().Analyze(new[] { entry }, index).Findings);
        Assert.Empty(finding.Suspects);
        Assert.Equal(LogConfidence.None, finding.Confidence);
    }

    [Fact]
    public void Wrapper_frames_without_a_name_are_not_reported()
    {
        var lines = new[]
        {
            "NullReferenceException: Object reference not set to an instance of an object",
            "  at (wrapper dynamic-method) DialogFromXml.DMD<BaseDialogRequirement DialogFromXml:ParseRequirement(System.Xml.Linq.XElement)>(System.Xml.Linq.XElement)",
            "  at (wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition.SyncProxy<BaseDialogRequirement DialogFromXml:ParseRequirement(System.Xml.Linq.XElement)>(System.Xml.Linq.XElement)",
            "  at DialogFromXml.ParseNode (System.Xml.Linq.XElement root) [0x0001e] in <abc123>:0 "
        };

        var entries = GameLogParser.Parse(lines, out _);
        var entry = Assert.Single(entries);

        Assert.All(entry.Frames, f => Assert.False(string.IsNullOrWhiteSpace(f.ToString())));
        Assert.Contains(entry.Frames, f => f.ToString().Contains("DialogFromXml.ParseNode"));
    }

    [Fact]
    public void Two_lookup_paths_add_one_shared_note()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        var entries = new[]
        {
            new LogEntry
            {
                Severity = LogSeverity.Error, Tag = "XUi",
                Message = "Controller 'XUiC_First, 1-XNPCVoiceControl' not found, using base XUiController"
            },
            new LogEntry
            {
                Severity = LogSeverity.Warning, Tag = "",
                Message = "Type:NoActiveChat, 1-XNPCVoiceControl was missing when we looked it up via Type.GetType()"
            }
        };

        var report = new LogAnalyzer().Analyze(entries, index);

        Assert.Contains(report.Notes, n => n.Key == "log.notesLookupPaths");
    }

    [Fact]
    public void Xml_patch_card_counts_distinct_targets()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        var entries = new[] { "gunA", "gunB", "gunB" }.Select(name =>
            new LogEntry
            {
                Severity = LogSeverity.Warning, Tag = "",
                Message = $"XML patch for \"items.xml\" from mod \"OPMods\" did not apply: <append xpath=\"/items/item[@name='{name}']\"  (line 7 at pos 3)"
            });

        var finding = Assert.Single(new LogAnalyzer().Analyze(entries, index).Findings);

        Assert.Equal(3, finding.Occurrences);
        Assert.Equal(2, finding.DistinctValues);
        Assert.Equal(2, finding.Details.Count);
    }

    [Fact]
    public void Exc_line_keeps_its_stack_and_is_not_stolen_by_the_wrapper_line()
    {
        var lines = new[]
        {
            "2026-11-12T23:09:31 302.807 ERR XML loader: Loading and parsing 'dialogs' failed",
            "2026-11-12T23:09:31 302.811 EXC No action class 'NPCNoVoiceTag, 1-XNPCVoiceControl found!",
            "  at (wrapper dynamic-method) DialogFromXml.DMD<System.Void DialogFromXml:ParseResponse(Dialog, System.Xml.Linq.XElement)>(Dialog,System.Xml.Linq.XElement)",
            "  at DialogFromXml.ParseNode (System.Xml.Linq.XElement root) [0x0001e] in <b181c0063dca421180fe9efc360eeb10>:0 ",
            "  at DialogFromXml.CreateDialogs (XmlFile xmlFile) [0x0001b] in <b181c0063dca421180fe9efc360eeb10>:0 ",
            "UnityEngine.StackTraceUtility:ExtractStringFromException(Object)"
        };

        var entries = GameLogParser.Parse(lines, out _);

        var exc = entries.Single(e => e.Message.Contains("No action class"));
        Assert.Contains(exc.Frames, f => f.ToString().Contains("DialogFromXml.ParseNode"));
        Assert.Contains(exc.Frames, f => f.ToString().Contains("DialogFromXml.CreateDialogs"));

        // The wrapper line must not become its own entry claiming these frames.
        var wrapper = entries.FirstOrDefault(e => e.Message.Contains("ExtractStringFromException"));
        Assert.True(wrapper is null || wrapper.Frames.Count == 0);
    }

    [Fact]
    public void Xml_patch_with_direct_name_attribute_extracts_the_node()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        // OPMods uses <passive_effect name="LootProb" ...> with no xpath attribute.
        var entries = new[] { "rifleSkill", "ammo762mm", "riflesCSM", "riflesCSM" }.Select(tag =>
            new LogEntry
            {
                Severity = LogSeverity.Warning, Tag = "",
                Message = $"XML patch for \"progression.xml\" from mod \"OPMods\" did not apply: <passive_effect name=\"LootProb\" operation=\"perc_add\" level=\"1,5\" value=\"2,10\" tags=\"{tag}\"  (line 50 at pos 5)"
            });

        var finding = Assert.Single(new LogAnalyzer().Analyze(entries, index).Findings);

        Assert.Equal(4, finding.Occurrences);
        Assert.Equal(1, finding.DistinctValues);   // one node, four attempts
        Assert.Contains("progression.xml [LootProb]", finding.Details);
    }

    [Fact]
    public void Action_class_card_alone_triggers_the_shared_lookup_note()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        var entries = new[]
        {
            new LogEntry
            {
                Severity = LogSeverity.Error, Tag = "",
                Message = "No action class 'NPCNoVoiceTag, 1-XNPCVoiceControl found!"
            }
        };

        var report = new LogAnalyzer().Analyze(entries, index);

        Assert.Contains(report.Notes, n => n.Key == "log.notesLookupPaths");
        Assert.Contains(report.Findings[0].Suggestions, x => x.Key == "log.hintActionClassPath");
    }

    [Fact]
    public void Report_states_how_many_findings_were_dropped()
    {
        var index = ModTypeIndex.BuildFromFolder(new TestWorkspace().Root);

        // Digits in the message would be folded into "#" by the grouping rules, so the part that
        // has to differ is letters only.
        var entries = Enumerable.Range(0, 130).Select(i => new LogEntry
        {
            Severity = LogSeverity.Warning,
            Tag = "TEST",
            Message = $"Distinct warning {(char)('A' + i / 26)}{(char)('a' + i % 26)} failed"
        });

        var report = new LogAnalyzer().Analyze(entries, index);

        Assert.Equal(120, report.Findings.Count);
        Assert.Equal(130, report.TotalFindings);
        Assert.True(report.Truncated);

        var text = LogReportWriter.Build(report);
        Assert.Contains(Localization.SF("log.reportTruncated", 120, 130), text);
        Assert.Contains("120", text);
        Assert.Contains("130", text);
    }

    [Fact]
    public void Err_line_keeps_the_frames_that_follow_it()
    {
        var lines = new[]
        {
            "2026-11-12T23:09:31 302.807 ERR [XUi] Window setup failed",
            "  at SomeMod.Window.Update () [0x00000] in <hash>:0 ",
            "  at GameManager.Tick () [0x00100] in <hash>:0 ",
            "2026-11-12T23:09:32 303.000 INF [World] next line"
        };

        var entries = GameLogParser.Parse(lines, out _);
        var err = Assert.Single(entries.Where(e => e.Severity == LogSeverity.Error));

        Assert.Equal("XUi", err.Tag);
        Assert.Equal("Window setup failed", err.Message);
        Assert.Equal(2, err.Frames.Count);
        Assert.Equal("SomeMod.Window", err.Frames[0].TypeName);
        Assert.Equal("Update", err.Frames[0].MemberName);
    }

    [Fact]
    public void Err_before_an_exception_header_does_not_swallow_its_stack()
    {
        var lines = new[]
        {
            "2026-11-12T23:09:31 302.807 ERR [XUi] Loading dialogs failed",
            "NullReferenceException: Object reference not set to an instance of an object",
            "  at WireManager.Cleanup () [0x00000] in <hash>:0 "
        };

        var entries = GameLogParser.Parse(lines, out _);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Loading dialogs failed", entries[0].Message);
        Assert.Empty(entries[0].Frames);
        Assert.Null(entries[0].ExceptionType);

        Assert.Equal("NullReferenceException", entries[1].ExceptionType);
        Assert.Single(entries[1].Frames);
    }

    [Fact]
    public void Exc_line_takes_the_exception_type_from_the_message()
    {
        var lines = new[]
        {
            "2026-11-12T23:09:31 302.811 EXC NullReferenceException: Object reference not set to an instance of an object",
            "  at WireManager.Cleanup () [0x00000] in <hash>:0 "
        };

        var entry = Assert.Single(GameLogParser.Parse(lines, out _));

        Assert.Equal("NullReferenceException", entry.ExceptionType);
        Assert.Equal("Object reference not set to an instance of an object", entry.Message);
        Assert.True(LogAnalyzer.IsRelevant(entry));

        // The card is titled with the type, and the type is not repeated in the message.
        var report = new LogAnalyzer().Analyze(new[] { entry },
            ModTypeIndex.BuildFromFolder(new TestWorkspace().Root));
        Assert.Equal("NullReferenceException", Assert.Single(report.Findings).DisplayTitle);
    }

    [Fact]
    public void Exc_lines_with_the_same_type_and_message_group_together()
    {
        var lines = new[]
        {
            "2026-11-12T23:09:31 302.811 EXC NullReferenceException: Object reference not set to an instance of an object",
            "2026-11-12T23:09:32 302.900 EXC NullReferenceException: Object reference not set to an instance of an object"
        };

        var entries = GameLogParser.Parse(lines, out _);
        var report = new LogAnalyzer().Analyze(entries,
            ModTypeIndex.BuildFromFolder(new TestWorkspace().Root));
        var finding = Assert.Single(report.Findings);

        Assert.Equal("NullReferenceException", finding.ExceptionType);
        Assert.Equal(2, finding.Occurrences);
    }

    [Fact]
    public void Exc_line_without_a_type_keeps_a_null_type()
    {
        var lines = new[]
        {
            "2026-11-12T23:09:31 302.811 EXC No action class 'NPCNoVoiceTag, 1-XNPCVoiceControl found!"
        };

        var entry = Assert.Single(GameLogParser.Parse(lines, out _));

        Assert.Null(entry.ExceptionType);
        Assert.Equal("No action class 'NPCNoVoiceTag, 1-XNPCVoiceControl found!", entry.Message);
        Assert.True(LogAnalyzer.IsRelevant(entry));
    }

    [Fact]
    public void Dll_cache_supports_parallel_reads_and_writes()
    {
        using var workspace = new TestWorkspace();
        var cache = DllCache.Load(Path.Combine(workspace.Root, "log-index.json"));
        var stamp = new DateTime(2026, 11, 13, 5, 0, 0, DateTimeKind.Utc);

        cache.Set("first.dll", 1, stamp, new List<string> { "First.Type" },
            new List<HarmonyTargetReader.Target>());

        var failures = 0;

        Parallel.For(0, 400, i =>
        {
            try
            {
                if (i % 2 == 0)
                {
                    cache.Set($"mod{i}.dll", i, stamp, new List<string> { $"Mod{i}.Type" },
                        new List<HarmonyTargetReader.Target>());
                }
                else
                {
                    cache.TryGet("first.dll", 1, stamp, out _, out _);
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.Equal(0, failures);
    }

    [Fact]
    public void Scan_state_produces_the_same_result_as_scan()
    {
        var lines = new[]
        {
            "2026-11-12T20:00:00 1.0 INF [MODS] Loading localization from mod: OldMod",
            "2026-11-12T23:04:00 1.0 INF [MODS] Loading localization from mod: CasesMod",
            "2026-11-12T23:04:30 2.0 INF [MODS]   Trying to load from folder: '00000-Gears'",
            "2026-11-12T23:07:30 5.0 ERR Something failed"
        };

        var scanned = LogComparison.Scan(lines);

        var state = new LogComparison.ScanState();
        foreach (var line in lines) state.Observe(line);
        var built = state.Build();

        Assert.Equal(scanned.Start, built.Start);
        Assert.Equal(scanned.FirstError, built.FirstError);
        Assert.Equal(scanned.Mods.Count, built.Mods.Count);

        foreach (var (name, seen) in scanned.Mods)
        {
            Assert.True(built.Mods.TryGetValue(name, out var other), $"missing {name}");
            Assert.Equal(seen, other);
        }
    }

    /// <summary>Takes any type the index knows about, so the test does not depend on a fixture's names.</summary>
    private static string FirstTypeOf(ModTypeIndex index)
    {
        foreach (var candidate in new[]
                 {
                     "TestMod.ExampleBehaviour", "TestMod.TestBehaviour", "TestMod.ModInit",
                     "TestMod.Patch", "TestMod.Entry", "TestMod.TestMod"
                 })
        {
            if (index.Resolve(candidate).Count > 0) return candidate;
        }

        return string.Empty;
    }
}
