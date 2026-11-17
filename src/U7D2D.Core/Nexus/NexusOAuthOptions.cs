namespace U7D2D.Core.Nexus;

/// <summary>
/// Nexus Mods OAuth 2.0 (Authorization Code + PKCE) settings.
///
/// AUP compliance: the Nexus API Acceptable Use Policy does not allow applications to ask users
/// for a personal API key. The manager therefore never asks for one: the player signs in through
/// the browser, Nexus returns a short-lived access token to a local loopback address, and the app
/// refreshes it with the refresh token it was given. No secret is typed by the user.
///
/// Before shipping, the application must be registered with Nexus (support@nexusmods.com) and the
/// issued client id placed in <see cref="DefaultClientId"/>. The redirect URI below must be
/// registered exactly as written. The client id is public, not a secret, so it is safe to ship.
/// </summary>
public static class NexusOAuthOptions
{
    /// <summary>
    /// OAuth client id issued by Nexus Mods. Replace after the application is approved.
    /// During testing it can be overridden with the U7D2D_NEXUS_CLIENT_ID environment variable
    /// without a rebuild.
    /// </summary>
    public const string DefaultClientId = "REPLACE_WITH_REGISTERED_CLIENT_ID";

    /// <summary>
    /// Loopback callback the browser is redirected to. Must match the value registered with Nexus
    /// character for character, including the port and the trailing path.
    /// </summary>
    public const string RedirectUri = "http://127.0.0.1:53377/callback";

    public const string AuthorizeEndpoint = "https://users.nexusmods.com/oauth/authorize";
    public const string TokenEndpoint = "https://users.nexusmods.com/oauth/token";

    /// <summary>
    /// Empty scope: the manager only needs the signed-in user's identity and public mod data.
    /// </summary>
    public const string Scope = "";

    /// <summary>How long the user has to finish signing in before the listener gives up.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    public static string ClientId
    {
        get
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("U7D2D_NEXUS_CLIENT_ID");
            return string.IsNullOrWhiteSpace(fromEnvironment)
                ? DefaultClientId
                : fromEnvironment.Trim();
        }
    }

    /// <summary>True once a real client id is in place (the placeholder means "not registered yet").</summary>
    public static bool IsConfigured => IsUsable(ClientId);

    /// <summary>
    /// Resolves the client id in priority order: an id saved in the app settings (so the user can
    /// paste the one Nexus issued without a rebuild), then the environment variable, then the
    /// build-time default.
    /// </summary>
    public static string ResolveClientId(string? storedOverride)
    {
        if (IsUsable(storedOverride)) return storedOverride!.Trim();
        return ClientId;
    }

    /// <summary>A value is usable when it is present and is not the shipped placeholder.</summary>
    public static bool IsUsable(string? clientId) =>
        !string.IsNullOrWhiteSpace(clientId) &&
        !clientId.Trim().Equals(DefaultClientId, StringComparison.OrdinalIgnoreCase);

    public static int CallbackPort => new Uri(RedirectUri).Port;
}
