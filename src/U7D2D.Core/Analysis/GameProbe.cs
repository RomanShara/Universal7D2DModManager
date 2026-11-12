using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Analysis;

/// <summary>Inspects a game installation: executable, managed assembly, Mods folder and version markers.</summary>
public sealed class GameProbe
{
    private const int AppId = 251570; // 7 Days to Die (Steam)

    public GameInstallInfo Inspect(string? gamePath, string? versionOverride = null)
    {
        var info = new GameInstallInfo { GamePath = gamePath ?? string.Empty };

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            info.Problems.Add(Localization.S("probe.pathNotSet"));
            return info;
        }

        if (!Directory.Exists(gamePath))
        {
            info.Problems.Add(Localization.SF("probe.folderMissing", gamePath));
            return info;
        }

        var root = new DirectoryInfo(gamePath);

        var exe = root.GetFiles("7DaysToDie.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (exe is not null) info.Checks.Add(Localization.SF("probe.exeFound", exe.Name));
        else info.Problems.Add(Localization.S("probe.exeNotFound"));

        var dataDir = Path.Combine(gamePath, "7DaysToDie_Data");
        if (Directory.Exists(dataDir)) info.Checks.Add(Localization.S("probe.dataFound"));

        var managed = Path.Combine(dataDir, "Managed", "Assembly-CSharp.dll");
        if (File.Exists(managed))
        {
            info.ManagedAssemblyPath = managed;
            info.Checks.Add(Localization.S("probe.managedFound"));
        }
        else
        {
            info.Problems.Add(Localization.S("probe.managedNotFound"));
        }

        var mods = Path.Combine(gamePath, "Mods");
        info.HasModsFolder = Directory.Exists(mods);
        info.ModsPath = info.HasModsFolder ? mods : null;
        if (info.HasModsFolder) info.Checks.Add(Localization.S("probe.modsFound"));

        var (version, source) = DetectVersion(gamePath, root);
        if (!string.IsNullOrWhiteSpace(versionOverride))
        {
            info.VersionString = versionOverride.Trim();
            info.VersionSource = Localization.S("probe.sourceManual");
        }
        else if (version is not null)
        {
            info.VersionString = version;
            info.VersionSource = source;
        }
        else
        {
            info.Problems.Add(Localization.S("probe.versionUnknown"));
        }

        if (info.VersionString is not null)
            info.Checks.Add(Localization.SF("probe.versionLine", info.VersionString, info.VersionSource ?? string.Empty));

        info.IsValid = exe is not null || info.ManagedAssemblyPath is not null;
        return info;
    }

    private static (string? Version, string? Source) DetectVersion(string gamePath, DirectoryInfo root)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(gamePath, "version.txt"),
                     Path.Combine(gamePath, "7DaysToDie_Data", "version.txt"),
                     Path.Combine(gamePath, "Data", "version.txt"),
                     Path.Combine(gamePath, "7DaysToDie_Data", "StreamingAssets", "version.txt")
                 })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var text = File.ReadAllText(candidate).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return (text.Split('\n')[0].Trim(), "version.txt");
            }
            catch (IOException)
            {
            }
        }

        // Steam manifest (steamapps/appmanifest_251570.acf) — search a few levels up.
        var directory = root;
        for (var i = 0; i < 4 && directory is not null; i++)
        {
            var manifest = Path.Combine(directory.FullName, $"appmanifest_{AppId}.acf");
            if (File.Exists(manifest))
            {
                try
                {
                    var content = File.ReadAllText(manifest);
                    var build = ExtractAcfValue(content, "buildid");
                    var name = ExtractAcfValue(content, "name");
                    if (!string.IsNullOrWhiteSpace(build))
                        return ($"build {build}" + (string.IsNullOrWhiteSpace(name) ? string.Empty : $" · {name}"), "Steam appmanifest");
                }
                catch (IOException)
                {
                }
            }

            var steamApps = Path.Combine(directory.FullName, "steamapps", $"appmanifest_{AppId}.acf");
            if (File.Exists(steamApps))
            {
                try
                {
                    var content = File.ReadAllText(steamApps);
                    var build = ExtractAcfValue(content, "buildid");
                    if (!string.IsNullOrWhiteSpace(build)) return ($"build {build}", "Steam appmanifest");
                }
                catch (IOException)
                {
                }
            }

            directory = directory.Parent;
        }

        // GOG ships goggame-<id>.info next to the executable with the store version inside.
        var gogVersion = ReadGogVersion(gamePath);
        if (gogVersion is not null) return (gogVersion, "GOG goggame.info");

        // Epic Games Store keeps a manifest under .egstore with AppVersion.
        var epicVersion = ReadEpicVersion(gamePath);
        if (epicVersion is not null) return (epicVersion, "Epic Games manifest");

        // The game keeps its release numbers as constants in Assembly-CSharp, which is the only
        // machine-readable source in repacks that ship no version.txt and no store manifest.
        var (constantsVersion, constantsSource) = ReadConstantsVersion(gamePath);
        if (constantsVersion is not null) return (constantsVersion, constantsSource);

        // Fall back to the managed assembly version — it is a real build marker, not a game version string.
        var managed = Path.Combine(gamePath, "7DaysToDie_Data", "Managed", "Assembly-CSharp.dll");
        if (File.Exists(managed))
        {
            try
            {
                using var stream = File.OpenRead(managed);
                using var pe = new PEReader(stream);
                if (pe.HasMetadata)
                {
                    var reader = pe.GetMetadataReader();
                    if (reader.IsAssembly)
                    {
                        var assemblyVersion = reader.GetAssemblyDefinition().Version;
                        // Repacked builds often carry 0.0.0.0 — reporting that as a game version would be misleading.
                        if (assemblyVersion is not null && assemblyVersion != new Version(0, 0, 0, 0) && assemblyVersion != new Version(0, 0, 0))
                            return (Localization.SF("probe.assemblyVersion", assemblyVersion),
                                Localization.S("probe.sourceAssemblyVersion"));
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Reads Constants.cVersionMajor/Minor/Build from Assembly-CSharp metadata. The game stores the
    /// minor part in tenths (40 means 4.0), which is why it is normalised before being reported.
    /// </summary>
    private static (string? Version, string? Source) ReadConstantsVersion(string gamePath)
    {
        var managed = Path.Combine(gamePath, "7DaysToDie_Data", "Managed", "Assembly-CSharp.dll");
        if (!File.Exists(managed)) return (null, null);

        try
        {
            using var stream = File.OpenRead(managed);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return (null, null);

            var reader = pe.GetMetadataReader();
            var values = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if (!reader.GetString(type.Name).Equals("Constants", StringComparison.Ordinal)) continue;

                foreach (var fieldHandle in type.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    var name = reader.GetString(field.Name);
                    if (!name.StartsWith("cVersion", StringComparison.Ordinal)) continue;

                    var constantHandle = field.GetDefaultValue();
                    if (constantHandle.IsNil) continue;

                    var constant = reader.GetConstant(constantHandle);
                    if (constant.TypeCode != ConstantTypeCode.Int32) continue;

                    values[name] = reader.GetBlobReader(constant.Value).ReadInt32();
                }
            }

            if (!values.TryGetValue("cVersionMajor", out var major)) return (null, null);
            if (!values.TryGetValue("cVersionMinor", out var minor)) return (null, null);
            values.TryGetValue("cVersionBuild", out var build);

            var version = minor >= 10 && minor % 10 == 0
                ? $"{major}.{minor / 10}.0"
                : $"{major}.{minor}.{build}";

            var source = build > 0
                ? Localization.SF("probe.sourceConstantsBuild", build)
                : Localization.S("probe.sourceConstants");
            return (version, source);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>Reads the version GOG records in goggame-*.info next to the executable.</summary>
    private static string? ReadGogVersion(string gamePath)
    {
        try
        {
            foreach (var info in Directory.EnumerateFiles(gamePath, "goggame-*.info", SearchOption.TopDirectoryOnly))
            {
                var text = File.ReadAllText(info);
                var version = ExtractJsonValue(text, "version");
                if (!string.IsNullOrWhiteSpace(version)) return version.Trim();
            }
        }
        catch (Exception)
        {
            // No GOG metadata, try the next source.
        }

        return null;
    }

    /// <summary>Reads AppVersion from the Epic Games Store manifest inside .egstore.</summary>
    private static string? ReadEpicVersion(string gamePath)
    {
        try
        {
            var store = Path.Combine(gamePath, ".egstore");
            if (!Directory.Exists(store)) return null;

            foreach (var item in Directory.EnumerateFiles(store, "*.item", SearchOption.TopDirectoryOnly))
            {
                var text = File.ReadAllText(item);
                var version = ExtractJsonValue(text, "AppVersion");
                if (!string.IsNullOrWhiteSpace(version)) return version.Trim();
            }
        }
        catch (Exception)
        {
            // No Epic metadata, try the next source.
        }

        return null;
    }

    /// <summary>Pulls a string value out of flat JSON without pulling in a JSON dependency.</summary>
    private static string? ExtractJsonValue(string json, string key)
    {
        var index = json.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var colon = json.IndexOf(':', index);
        if (colon < 0) return null;

        var start = json.IndexOf('"', colon);
        if (start < 0) return null;

        var end = json.IndexOf('"', start + 1);
        return end < 0 ? null : json[(start + 1)..end];
    }

    private static string? ExtractAcfValue(string content, string key)
    {
        var marker = $"\"{key}\"";
        var index = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var rest = content[(index + marker.Length)..];
        var parts = rest.Split('"', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var value = part.Trim();
            if (value.Length == 0 || value == "\t\t" || value.All(char.IsWhiteSpace)) continue;
            return value;
        }
        return null;
    }
}
