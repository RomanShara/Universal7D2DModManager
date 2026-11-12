using U7D2D.Core.Data;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Tests;

public sealed class ModConfigTests
{
    private const string CasesConfig = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <CasesMod>
          <General enabled="true" debug="false" animDuration="4.5" stripCells="31" />
          <Scanner enabled="true" share="50">
            <Category name="weapon" weight="10" />
            <Category name="tool" weight="8" />
          </Scanner>
        </CasesMod>
        """;

    private static InstalledMod CreateMod(TestWorkspace ws, string folder, params (string Name, string Content)[] files)
    {
        var root = Path.Combine(ws.Mods, folder);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ModInfo.xml"), TestWorkspace.ModernModInfo(folder, "1.0"));

        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(root, name), content);

        return new InstalledMod
        {
            FolderName = folder,
            FullPath = root,
            Info = ModMetadata.Unknown(folder),
            HasModInfo = true
        };
    }

    [Fact]
    public void Finds_config_files_and_ignores_modinfo_and_schemas()
    {
        using var ws = new TestWorkspace();
        var mod = CreateMod(ws, "Cases",
            ("CasesModConfig.xml", CasesConfig),
            ("ModSettings.xml", "<ModSettings version=\"2\"><Global /></ModSettings>"),
            ("README.txt", "text"));

        var files = new ModConfigService(ws.Paths).ListConfigs(mod);

        var file = Assert.Single(files);
        Assert.Equal("CasesModConfig.xml", file.DisplayName);
        Assert.Equal(ConfigFormat.Xml, file.Format);
    }

    [Fact]
    public void Reads_xml_attributes_with_groups_and_kinds()
    {
        using var ws = new TestWorkspace();
        var mod = CreateMod(ws, "Cases", ("CasesModConfig.xml", CasesConfig));
        var service = new ModConfigService(ws.Paths);

        var file = service.ListConfigs(mod).Single();
        var document = service.Read(file);

        Assert.Null(document.Error);
        Assert.NotEmpty(document.Fields);

        var enabled = document.Fields.Single(f => f.Key.EndsWith("/General@enabled"));
        Assert.Equal("General", enabled.Group);
        Assert.Equal("true", enabled.Value);
        Assert.Equal(ConfigValueKind.Bool, enabled.Kind);

        var duration = document.Fields.Single(f => f.Key.EndsWith("/General@animDuration"));
        Assert.Equal(ConfigValueKind.Number, duration.Kind);

        // Repeated elements keep stable indices.
        Assert.Contains(document.Fields, f => f.Key.EndsWith("/Category@weight"));
        Assert.Contains(document.Fields, f => f.Key.EndsWith("/Category[1]@weight"));
    }

    [Fact]
    public void Saves_xml_changes_and_creates_backup()
    {
        using var ws = new TestWorkspace();
        var mod = CreateMod(ws, "Cases", ("CasesModConfig.xml", CasesConfig));
        var service = new ModConfigService(ws.Paths);
        var file = service.ListConfigs(mod).Single();
        var document = service.Read(file);

        var enabled = document.Fields.Single(f => f.Key.EndsWith("/General@enabled"));
        var duration = document.Fields.Single(f => f.Key.EndsWith("/General@animDuration"));

        var result = service.Save(file, mod.FolderName,
        [
            new ConfigFieldChange { Key = enabled.Key, Value = "false", Kind = ConfigValueKind.Bool },
            new ConfigFieldChange { Key = duration.Key, Value = "3.25", Kind = ConfigValueKind.Number }
        ]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.Changed);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));

        var text = File.ReadAllText(file.FullPath);
        Assert.Contains("enabled=\"false\"", text);
        Assert.Contains("animDuration=\"3.25\"", text);
        Assert.Contains("<Scanner enabled=\"true\" share=\"50\">", text);

        // Re-reading returns the new values.
        var again = service.Read(service.ListConfigs(mod).Single());
        Assert.Equal("false", again.Fields.Single(f => f.Key.EndsWith("/General@enabled")).Value);
    }

    [Fact]
    public void Rejects_invalid_boolean_value()
    {
        using var ws = new TestWorkspace();
        var mod = CreateMod(ws, "Cases", ("CasesModConfig.xml", CasesConfig));
        var service = new ModConfigService(ws.Paths);
        var file = service.ListConfigs(mod).Single();
        var enabled = service.Read(file).Fields.Single(f => f.Key.EndsWith("/General@enabled"));

        var result = service.Save(file, mod.FolderName,
        [
            new ConfigFieldChange { Key = enabled.Key, Value = "maybe", Kind = ConfigValueKind.Bool }
        ]);

        Assert.False(result.Success);
        Assert.Contains("true", result.Error);
        Assert.Contains("enabled=\"true\"", File.ReadAllText(file.FullPath));
    }

    [Fact]
    public void Reads_and_writes_json_config()
    {
        using var ws = new TestWorkspace();
        const string json = """
            {
              "General": { "enabled": true, "speed": 1.5 },
              "Tags": [ "a", "b" ]
            }
            """;

        var mod = CreateMod(ws, "Vault", ("VaultConfig.json", json));
        var service = new ModConfigService(ws.Paths);
        var file = service.ListConfigs(mod).Single();

        Assert.Equal(ConfigFormat.Json, file.Format);

        var document = service.Read(file);
        var speed = document.Fields.Single(f => f.Key == "$.General.speed");
        Assert.Equal("1.5", speed.Value);

        var result = service.Save(file, mod.FolderName,
        [
            new ConfigFieldChange { Key = speed.Key, Value = "2.75", Kind = ConfigValueKind.Number }
        ]);

        Assert.True(result.Success, result.Error);
        Assert.Contains("2.75", File.ReadAllText(file.FullPath));
    }

    [Fact]
    public void Mod_without_configs_returns_empty_list()
    {
        using var ws = new TestWorkspace();
        var mod = CreateMod(ws, "Plain");

        var files = new ModConfigService(ws.Paths).ListConfigs(mod);

        Assert.Empty(files);
    }
}
