using System.Security.Cryptography;
using System.Text;

namespace U7D2D.Core.Nexus;

/// <summary>
/// Proof Key for Code Exchange helpers (RFC 7636). The verifier is a high-entropy random string;
/// the challenge is its SHA-256 hash, base64url-encoded without padding. The authorization server
/// only accepts the code exchange from the client that can present the original verifier, so an
/// intercepted authorization code is useless on its own.
/// </summary>
public static class NexusPkce
{
    /// <summary>
    /// 32 random bytes base64url-encoded = 43 characters, the minimum length RFC 7636 allows.
    /// Uses the platform CSPRNG.
    /// </summary>
    public static string CreateVerifier() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>S256 challenge: base64url(SHA-256(ASCII(verifier))).</summary>
    public static string CreateChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>An opaque value echoed back by the server so the callback can be matched to this request.</summary>
    public static string CreateState() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
