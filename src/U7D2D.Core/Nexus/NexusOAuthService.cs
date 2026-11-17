using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using U7D2D.Core.Data;
using U7D2D.Core.I18n;

namespace U7D2D.Core.Nexus;

/// <summary>Outcome of an interactive sign-in attempt.</summary>
public sealed class NexusOAuthResult
{
    public bool Success { get; init; }

    /// <summary>The user closed the browser tab or denied access. Not an error worth a red toast.</summary>
    public bool Cancelled { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Drives the OAuth 2.0 Authorization Code + PKCE flow for Nexus Mods and keeps the resulting
/// tokens.
///
/// Flow: build the authorize URL with a PKCE challenge, open the browser, listen on a loopback
/// address for the redirect, exchange the code (with the original verifier) for an access and
/// refresh token, and store both DPAPI-protected. Access tokens are short-lived; when one expires
/// the refresh token is used silently. A failed refresh means the user revoked access, so the
/// tokens are cleared and the UI is asked to sign in again.
///
/// The service is UI-agnostic: the caller supplies how to open a URL, which keeps Core testable
/// and free of platform dependencies.
/// </summary>
public sealed class NexusOAuthService
{
    private readonly SettingsStore _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public NexusOAuthService(SettingsStore settings)
        : this(settings, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, ownsClient: true)
    {
    }

    /// <summary>Test seam: a fake handler means no network is used.</summary>
    public NexusOAuthService(SettingsStore settings, HttpClient http, bool ownsClient = false)
    {
        _settings = settings;
        _http = http;
        _ownsClient = ownsClient;
    }

    /// <summary>True when a token (access or refresh) is stored, regardless of its age.</summary>
    public bool IsSignedIn
    {
        get
        {
            var s = _settings.Load();
            return !string.IsNullOrWhiteSpace(s.NexusRefreshToken) ||
                   !string.IsNullOrWhiteSpace(s.NexusAccessToken);
        }
    }

    public string? AccountName => _settings.Load().NexusAccountName;

    /// <summary>Effective OAuth client id: saved override, then environment, then build default.</summary>
    public string ClientId => NexusOAuthOptions.ResolveClientId(_settings.Load().NexusOAuthClientId);

    /// <summary>True once a real client id is available, so sign-in can actually start.</summary>
    public bool IsConfigured => NexusOAuthOptions.IsUsable(ClientId);

    public void SignOut()
    {
        var s = _settings.Load();
        s.NexusAccessToken = string.Empty;
        s.NexusRefreshToken = string.Empty;
        s.NexusTokenExpiresUtc = null;
        s.NexusAccountName = string.Empty;
        _settings.Save(s);
    }

    /// <summary>Records the display name after a successful account validation.</summary>
    public void SetAccountName(string? name)
    {
        var s = _settings.Load();
        s.NexusAccountName = name ?? string.Empty;
        _settings.Save(s);
    }

    /// <summary>
    /// Runs the interactive sign-in. <paramref name="openUrl"/> opens the authorization page in the
    /// user's browser (the UI passes the shell service). Blocks until the callback arrives, the
    /// user cancels, or <see cref="NexusOAuthOptions.SignInTimeout"/> elapses.
    /// </summary>
    public async Task<NexusOAuthResult> SignInAsync(
        Action<string> openUrl, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new NexusOAuthResult { Success = false, Error = Localization.S("nx.oauthNotConfigured") };

        var verifier = NexusPkce.CreateVerifier();
        var challenge = NexusPkce.CreateChallenge(verifier);
        var state = NexusPkce.CreateState();

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, NexusOAuthOptions.CallbackPort);
            listener.Start();
        }
        catch (SocketException)
        {
            return new NexusOAuthResult { Success = false, Error = Localization.S("nx.oauthPortBusy") };
        }

        using (listener)
        {
            try
            {
                openUrl(BuildAuthorizeUrl(challenge, state));
            }
            catch (Exception e)
            {
                return new NexusOAuthResult { Success = false, Error = e.Message };
            }

            var (code, error, cancelled) = await WaitForCodeAsync(listener, state, ct);
            if (cancelled) return new NexusOAuthResult { Success = false, Cancelled = true };
            if (error is not null) return new NexusOAuthResult { Success = false, Error = error };
            if (code is null) return new NexusOAuthResult { Success = false, Cancelled = true };

            var tokens = await ExchangeCodeAsync(code, verifier, ct);
            if (tokens is null)
                return new NexusOAuthResult { Success = false, Error = Localization.S("nx.oauthTokenFailed") };

            SaveTokens(tokens);
            return new NexusOAuthResult { Success = true };
        }
    }

    /// <summary>
    /// Returns a usable access token, refreshing it when it has expired. Returns null when the user
    /// is not signed in or the refresh token was rejected (access revoked); in the latter case the
    /// stored tokens are cleared so the UI can prompt for a fresh sign-in.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var s = _settings.Load();
        var access = NexusKeyProtection.Unprotect(s.NexusAccessToken);
        var refresh = NexusKeyProtection.Unprotect(s.NexusRefreshToken);

        if (string.IsNullOrWhiteSpace(access) && string.IsNullOrWhiteSpace(refresh))
            return null;

        // Refresh a minute early so a request never races the expiry boundary.
        if (!string.IsNullOrWhiteSpace(access) &&
            s.NexusTokenExpiresUtc is { } expiry &&
            DateTime.UtcNow < expiry.AddMinutes(-1))
            return access;

        if (string.IsNullOrWhiteSpace(refresh))
            return string.IsNullOrWhiteSpace(access) ? null : access;

        var refreshed = await RefreshAsync(refresh, ct);
        if (refreshed is null)
        {
            SignOut();
            return null;
        }

        SaveTokens(refreshed);
        return refreshed.AccessToken;
    }

    // ---------------------------------------------------------------- internals

    private string BuildAuthorizeUrl(string challenge, string state)
    {
        var query = new StringBuilder();
        void Add(string key, string value)
        {
            if (query.Length > 0) query.Append('&');
            query.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        Add("client_id", ClientId);
        Add("response_type", "code");
        Add("scope", NexusOAuthOptions.Scope);
        Add("redirect_uri", NexusOAuthOptions.RedirectUri);
        Add("state", state);
        Add("code_challenge_method", "S256");
        Add("code_challenge", challenge);

        return NexusOAuthOptions.AuthorizeEndpoint + "?" + query;
    }

    private async Task<(string? Code, string? Error, bool Cancelled)> WaitForCodeAsync(
        TcpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(NexusOAuthOptions.SignInTimeout);

        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the user or the timeout elapsed. Either way there is no code.
                return (null, null, true);
            }
            catch (SocketException)
            {
                return (null, null, true);
            }

            using (client)
            {
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token);
                var target = FirstLineTarget(request);

                if (target is null || !target.StartsWith("/callback", StringComparison.OrdinalIgnoreCase))
                {
                    // Browsers often probe /favicon.ico first. Answer politely and keep waiting.
                    await WriteResponseAsync(stream, ResponsePage(Localization.S("nx.oauthPageNotFound")), 404);
                    continue;
                }

                var query = ParseQuery(target);

                if (query.TryGetValue("error", out var oauthError))
                {
                    await WriteResponseAsync(stream, ResponsePage(Localization.S("nx.oauthPageDenied")), 200);
                    return (null, Localization.SF("nx.oauthDenied", oauthError), false);
                }

                if (!query.TryGetValue("state", out var returnedState) ||
                    !string.Equals(returnedState, expectedState, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, ResponsePage(Localization.S("nx.oauthPageBadState")), 400);
                    return (null, Localization.S("nx.oauthStateMismatch"), false);
                }

                if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                {
                    await WriteResponseAsync(stream, ResponsePage(Localization.S("nx.oauthPageNoCode")), 400);
                    return (null, Localization.S("nx.oauthNoCode"), false);
                }

                await WriteResponseAsync(stream, ResponsePage(Localization.S("nx.oauthPageDone")), 200);
                return (code, null, false);
            }
        }
    }

    private async Task<TokenResponse?> ExchangeCodeAsync(
        string code, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = NexusOAuthOptions.RedirectUri,
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier
        };

        return await PostTokenAsync(form, ct);
    }

    private async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken
        };

        return await PostTokenAsync(form, ct);
    }

    private async Task<TokenResponse?> PostTokenAsync(
        Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(NexusOAuthOptions.TokenEndpoint, content, ct);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct);
            var tokens = JsonSerializer.Deserialize<TokenResponse>(body);
            return string.IsNullOrWhiteSpace(tokens?.AccessToken) ? null : tokens;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private void SaveTokens(TokenResponse tokens)
    {
        var s = _settings.Load();
        s.NexusAccessToken = NexusKeyProtection.Protect(tokens.AccessToken);

        // A refresh response may omit the refresh token; keep the existing one in that case.
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
            s.NexusRefreshToken = NexusKeyProtection.Protect(tokens.RefreshToken);

        s.NexusTokenExpiresUtc = DateTime.UtcNow.AddSeconds(
            tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);

        _settings.Save(s);
    }

    /// <summary>Reads the request headers (up to the blank line) so the request line can be parsed.</summary>
    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();

        while (builder.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }

        return builder.ToString();
    }

    /// <summary>Extracts the path-and-query from "GET /callback?code=... HTTP/1.1".</summary>
    private static string? FirstLineTarget(string request)
    {
        var lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
        var line = lineEnd >= 0 ? request[..lineEnd] : request;
        var parts = line.Split(' ');
        return parts.Length >= 2 && parts[0] is "GET" or "POST" ? parts[1] : null;
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var question = target.IndexOf('?');
        if (question < 0) return result;

        var query = target[(question + 1)..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;

            var key = Uri.UnescapeDataString(pair[..equals].Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private static string ResponsePage(string message)
    {
        var encoded = WebUtility.HtmlEncode(message);
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
               "<title>Universal 7D2D Mod Manager</title>" +
               "<style>body{font-family:system-ui,sans-serif;background:#16181d;color:#e7e9ee;" +
               "display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
               ".box{max-width:420px;text-align:center;padding:28px;border:1px solid #2b2f38;" +
               "border-radius:12px}</style></head><body><div class=\"box\">" +
               "<h2>Universal 7D2D Mod Manager</h2>" +
               $"<p>{encoded}</p></div></body></html>";
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string html, int status)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
        var header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string TokenType { get; set; } = string.Empty;
    }
}
