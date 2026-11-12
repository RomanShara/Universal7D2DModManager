using System.IO;
using U7D2D.Core.I18n;

namespace U7D2D.App.Infrastructure;

/// <summary>
/// The web part of the interface is embedded into the executable, so the product is distributed as
/// one file. On start the files are unpacked next to the executable, where the WebView looks for them.
/// A version marker keeps later starts from unpacking again.
/// </summary>
internal static class WebAssets
{
    private const string Prefix = "wwwroot/";

    public static void EnsureExtracted()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var assembly = typeof(WebAssets).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "0";
        var marker = Path.Combine(target, ".version");

        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version) return;

            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = name[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                var path = Path.Combine(target, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using var source = assembly.GetManifestResourceStream(name);
                if (source is null) continue;

                using var file = File.Create(path);
                source.CopyTo(file);
            }

            File.WriteAllText(marker, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(Localization.SF("app.webAssetsFailed", target), ex);
        }
    }
}
