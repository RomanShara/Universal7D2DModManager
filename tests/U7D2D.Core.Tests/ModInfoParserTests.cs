using System.Text;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

public sealed class ModInfoParserTests
{
    [Fact]
    public void Parses_modern_value_attribute_format()
    {
        var xml = TestWorkspace.ModernModInfo("Cool Mod", "1.2.3", "Author X", "Required Mod");

        var metadata = ModInfoParser.Parse(xml);

        Assert.Equal("Cool Mod", metadata.Name);
        Assert.Equal("1.2.3", metadata.Version);
        Assert.Equal("Author X", metadata.Author);
        Assert.Equal("1.0", metadata.DeclaredGameVersion);
        Assert.Equal(ModInfoFormat.ValueAttribute, metadata.Format);
        var dependency = Assert.Single(metadata.Dependencies);
        Assert.Equal("Required Mod", dependency.Name);
        Assert.False(dependency.Optional);
    }

    [Fact]
    public void Parses_legacy_text_format()
    {
        var metadata = ModInfoParser.Parse(TestWorkspace.LegacyModInfo("Old Mod", "Alpha 16.4"));

        Assert.Equal("Old Mod", metadata.Name);
        Assert.Equal("Alpha 16.4", metadata.Version);
        Assert.Equal(ModInfoFormat.LegacyText, metadata.Format);
    }

    [Fact]
    public void Parses_utf16_with_bom()
    {
        var xml = TestWorkspace.ModernModInfo("Юникод Мод", "2.0");
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();

        var metadata = ModInfoParser.Parse(bytes);

        Assert.Equal("Юникод Мод", metadata.Name);
    }

    [Fact]
    public void Rejects_malformed_xml_with_error_message()
    {
        var ok = ModInfoParser.TryParse(Encoding.UTF8.GetBytes("<ModInfo><Name value=\"X\">"), out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_document_without_any_mod_fields()
    {
        var ok = ModInfoParser.TryParse(Encoding.UTF8.GetBytes("<root><Something value=\"X\"/></root>"), out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parses_v1_xml_root_without_modinfo_element()
    {
        // Real 7 Days to Die V1.0+ layout: <xml> root, fields are direct children.
        const string xml = """
            <?xml version="1.0" encoding="UTF-8" ?>
            <xml>
            	<Name value="TFP_Harmony" />
            	<DisplayName value="Harmony Wrapper" />
            	<Description value="Sets up basic HarmonyX configuration" />
            	<Author value="The Fun Pimps LLC" />
            	<Version value="1.1.0.4" />
            	<Website value="" />
            	<SkipWithAntiCheat value="true" />
            </xml>
            """;

        var metadata = ModInfoParser.Parse(xml);

        Assert.Equal("TFP_Harmony", metadata.Name);
        Assert.Equal("Harmony Wrapper", metadata.DisplayName);
        Assert.Equal("1.1.0.4", metadata.Version);
        Assert.Equal("The Fun Pimps LLC", metadata.Author);
        Assert.True(metadata.SkipWithAntiCheat);
        Assert.Equal(ModInfoFormat.ValueAttribute, metadata.Format);
    }

    [Fact]
    public void Parses_v1_xml_root_with_leading_license_comment()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>

            <!--/*Copyright 2023 Some Author
            Licensed under the Apache License, Version 2.0
            */-->

            <xml>
            	<Name value="Quartz" />
            	<Version value="10.0.0" />
            	<Author value="Laydor" />
            </xml>
            """;

        var metadata = ModInfoParser.Parse(xml);

        Assert.Equal("Quartz", metadata.Name);
        Assert.Equal("10.0.0", metadata.Version);
    }

    [Fact]
    public void Parses_legacy_xml_root_with_text_fields()
    {
        const string xml = """
            <xml>
              <Name>Ancient Mod</Name>
              <Version>Alpha 16.4</Version>
            </xml>
            """;

        var metadata = ModInfoParser.Parse(xml);

        Assert.Equal("Ancient Mod", metadata.Name);
        Assert.Equal("Alpha 16.4", metadata.Version);
        Assert.Equal(ModInfoFormat.LegacyText, metadata.Format);
    }

    [Fact]
    public void Optional_dependency_is_flagged()
    {
        const string xml = """
            <ModInfo>
              <Name value="A"/>
              <Version value="1"/>
              <Dependencies>
                <Dependency name="B" optional="true"/>
              </Dependencies>
            </ModInfo>
            """;

        var metadata = ModInfoParser.Parse(xml);

        Assert.True(metadata.Dependencies.Single().Optional);
    }
}
