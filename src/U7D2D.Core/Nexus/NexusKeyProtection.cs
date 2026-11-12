using System.Security.Cryptography;
using System.Text;

namespace U7D2D.Core.Nexus;

/// <summary>
/// Keeps the personal Nexus API key out of plain text in manager.db.
/// Uses Windows DPAPI scoped to the current Windows account, so the value cannot be
/// read by another user or by copying the database to another machine.
/// Non-Windows hosts (e.g. CI test runners) fall back to a reversible marker.
/// </summary>
public static class NexusKeyProtection
{
    private const string ProtectedPrefix = "dpapi1:";
    private const string PlainPrefix = "plain:";

    // Fixed additional entropy: not a secret, just makes the blob specific to this app.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("U7D2D.NexusApiKey.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;

        if (!OperatingSystem.IsWindows())
            return PlainPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));

        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(bytes);
        }
        catch (CryptographicException)
        {
            return PlainPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return string.Empty;

        // Legacy / non-DPAPI values written by older builds or non-Windows hosts.
        if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal) &&
            !stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return stored;

        var isProtected = stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal);
        var payload = stored.Substring((isProtected ? ProtectedPrefix : PlainPrefix).Length);

        try
        {
            var bytes = Convert.FromBase64String(payload);
            if (!OperatingSystem.IsWindows() || !isProtected)
                return Encoding.UTF8.GetString(bytes);

            var clear = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            // A key encrypted by a different Windows account cannot be recovered.
            return string.Empty;
        }
    }

    public static bool IsProtected(string? stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal);
}
