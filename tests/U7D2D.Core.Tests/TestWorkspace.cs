using System.IO.Compression;
using System.Text;
using U7D2D.Core.Infrastructure;

namespace U7D2D.Core.Tests;

/// <summary>Shared helpers: isolated temp workspaces and real archives built on the fly.</summary>
public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "u7d2d-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Path.Combine(Root, "app"));
        Game = Path.Combine(Root, "game");
        Mods = Path.Combine(Game, "Mods");
        Archives = Path.Combine(Root, "archives");
        Backups = Path.Combine(Root, "backups");
        Directory.CreateDirectory(Mods);
        Directory.CreateDirectory(Archives);
        Directory.CreateDirectory(Backups);
        File.WriteAllText(Path.Combine(Game, "version.txt"), "1.0 (b333)");
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public string Game { get; }
    public string Mods { get; }
    public string Archives { get; }
    public string Backups { get; }

    public string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public string WriteZip(string fileName, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(Archives, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entryPath, content) in files)
        {
            var entry = zip.CreateEntry(entryPath.Replace('\\', '/'));
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }

        return path;
    }

    public string WriteBinaryZip(string fileName, params (string Path, byte[] Content)[] files)
    {
        var path = Path.Combine(Archives, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entryPath, content) in files)
        {
            var entry = zip.CreateEntry(entryPath.Replace('\\', '/'));
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }

        return path;
    }

    public static string ModernModInfo(string name, string version, string? author = "Tester", string? dependency = null)
    {
        var dependencyXml = dependency is null
            ? string.Empty
            : $"""
                 <Dependencies>
                   <Dependency name="{dependency}" optional="false"/>
                 </Dependencies>
               """;

        return $"""
            <?xml version="1.0" encoding="UTF-8" ?>
            <ModInfo>
              <Name value="{name}"/>
              <DisplayName value="{name}"/>
              <Description value="Test mod"/>
              <Author value="{author}"/>
              <Version value="{version}"/>
              <GameVersion value="1.0"/>
              {dependencyXml}
            </ModInfo>
            """;
    }

    public static string LegacyModInfo(string name, string version) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <xml>
          <ModInfo>
            <Name>{name}</Name>
            <Author>Legacy</Author>
            <Version>{version}</Version>
          </ModInfo>
        </xml>
        """;

    public static string BlocksPatch(string blockName) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <blocks>
          <append xpath="/blocks">
            <block name="{blockName}">
              <property name="Stability" value="100"/>
            </block>
          </append>
        </blocks>
        """;

    public static string BlocksFull(string blockName) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <blocks>
          <block name="{blockName}">
            <property name="Stability" value="50"/>
          </block>
        </blocks>
        """;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}
