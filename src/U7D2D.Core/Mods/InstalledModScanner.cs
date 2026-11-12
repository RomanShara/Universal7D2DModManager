using U7D2D.Core.Data;
using U7D2D.Core.I18n;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;

namespace U7D2D.Core.Mods;

/// <summary>Reads the game's Mods directory and describes what is actually installed.</summary>
public sealed class InstalledModScanner
{
    private readonly InstalledStore _store;

    public InstalledModScanner(InstalledStore store) => _store = store;

    public List<InstalledMod> Scan(string modsPath)
    {
        var result = new List<InstalledMod>();
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath)) return result;

        var known = _store.LoadAll();
        var folders = new List<string>();

        try
        {
            folders.AddRange(Directory.GetDirectories(modsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(folder);
            if (name.StartsWith('.')) continue;
            if (name.Equals(".u7d2d-staging", StringComparison.OrdinalIgnoreCase)) continue;

            var modInfoPath = Path.Combine(folder, "ModInfo.xml");
            var actualRoot = folder;

            if (!File.Exists(modInfoPath))
            {
                // Some mods ship one wrapper folder deeper (e.g. Mods/Foo/Foo/ModInfo.xml).
                var nested = Directory.GetDirectories(folder)
                    .Select(d => Path.Combine(d, "ModInfo.xml"))
                    .FirstOrDefault(File.Exists);

                if (nested is not null)
                {
                    modInfoPath = nested;
                    actualRoot = Path.GetDirectoryName(nested)!;
                }
            }

            var hasModInfo = File.Exists(modInfoPath);
            ModMetadata metadata;
            var warnings = new List<string>();

            if (hasModInfo)
            {
                try
                {
                    metadata = ModInfoParser.Parse(File.ReadAllBytes(modInfoPath));
                }
                catch (Exception ex)
                {
                    metadata = ModMetadata.Unknown(name);
                    warnings.Add(Localization.SF("mod.installedModInfoCorrupt", ex.Message));
                }
            }
            else
            {
                metadata = ModMetadata.Unknown(name);
                warnings.Add(Localization.S("mod.modInfoAbsent"));
            }

            known.TryGetValue(name, out var stored);

            var mod = new InstalledMod
            {
                FolderName = name,
                FullPath = folder,
                Info = metadata,
                Content = BuildProfile(folder),
                FileCount = FileOps.CountFiles(folder),
                SizeBytes = FileOps.GetDirectorySize(folder),
                HasModInfo = hasModInfo,
                LastWriteUtc = Directory.GetLastWriteTimeUtc(folder),
                ContentHash = HashUtil.FingerprintDirectory(folder),
                SourceArchive = stored?.SourceArchive,
                InstalledUtc = stored?.InstalledUtc,
                Warnings = warnings
            };

            if (actualRoot != folder)
                mod.Warnings.Add(Localization.SF("mod.modInfoNested", Path.GetFileName(actualRoot)));

            result.Add(mod);
        }

        return result;
    }

    public static ModContentProfile BuildProfile(string folder)
    {
        var profile = new ModContentProfile();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return profile;
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            profile.FileCount++;

            try { profile.TotalBytes += new FileInfo(file).Length; }
            catch (IOException) { }

            var lower = relative.ToLowerInvariant();
            var slash = relative.IndexOf('/');
            if (slash > 0) folders.Add(relative[..slash]);

            if (lower.EndsWith(".xml"))
            {
                if (profile.XmlFiles.Count < 500) profile.XmlFiles.Add(relative);
                if (lower.StartsWith("config/") && profile.ConfigFiles.Count < 500) profile.ConfigFiles.Add(relative);
            }

            if (lower.EndsWith(".dll") && profile.DllFiles.Count < 100) profile.DllFiles.Add(relative);
            if (lower.StartsWith("harmony/")) profile.HasHarmonyFolder = true;
            if (lower.StartsWith("config/xui/")) profile.HasXui = true;
            if (lower.Contains("localization")) profile.HasLocalization = true;
            if (lower.StartsWith("assets/") || lower.EndsWith(".unity3d") || lower.EndsWith(".bundle")) profile.HasAssets = true;
        }

        profile.TopLevelFolders = folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        return profile;
    }
}
