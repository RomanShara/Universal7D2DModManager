using System;

namespace U7D2D.Core.Nexus;

/// <summary>
/// Poster addresses come from the Nexus API (`picture_url`, the primary screenshot of the mod).
/// The address ends up in the interface, so only a plain https URL on a nexusmods.com host is
/// accepted; anything else falls back to the placeholder instead of rendering an arbitrary URL.
/// </summary>
public static class NexusPoster
{
    private const int MaxLength = 512;

    public static string? Normalize(string? url)
    {
        var text = url?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return null;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;

        var host = uri.Host;
        var nexusHost = host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
                        host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase);

        return nexusHost ? uri.AbsoluteUri : null;
    }
}
