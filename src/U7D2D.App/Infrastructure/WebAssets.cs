using System.IO;
using U7D2D.Core.I18n;

namespace U7D2D.App.Infrastructure;

/// <summary>
/// The web part of the interface is embedded into the executable, so the product is distributed as
/// one file. On start the files are unpacked next to the executable, where the WebView looks for them.
///
/// The marker carries the assembly version and the module version id. The id changes on every build,
/// so a rebuilt executable with the same version still refreshes the interface; the version alone
/// would leave old CSS and JavaScript in place after a hotfix.
/// </summary>
internal static class WebAssets
{
    private const string Prefix = "wwwroot/";

    public static void EnsureExtracted()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var assembly = typeof(WebAssets).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "0";
        var build = assembly.ManifestModule.ModuleVersionId.ToString("N");
        var marker = Path.Combine(target, ".version");
        var markerText = $"{version}|{build}";

        var expected = new List<(string Name, string Path)>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = name[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            expected.Add((name, Path.Combine(target, relative)));
        }

        try
        {
            if (expected.Count == 0)
                throw new IOException(Localization.SF("app.webAssetsFailed", target));

            // A matching marker is not enough: a file removed by an antivirus or a cleaning tool
            // must be restored, not served as a missing stylesheet.
            if (File.Exists(marker) &&
                File.ReadAllText(marker).Trim() == markerText &&
                expected.All(entry => File.Exists(entry.Path)))
                return;

            foreach (var (name, path) in expected)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using var source = assembly.GetManifestResourceStream(name);
                if (source is null) continue;

                // Write next to the target and replace in one step: an interrupted extraction
                // leaves a .tmp file, never a truncated stylesheet or script.
                var temporary = path + ".tmp";
                using (var file = File.Create(temporary)) source.CopyTo(file);

                File.Move(temporary, path, overwrite: true);
            }

            // Written last, so a partial run is retried on the next start.
            File.WriteAllText(marker, markerText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(Localization.SF("app.webAssetsFailed", target), ex);
        }
    }
}
