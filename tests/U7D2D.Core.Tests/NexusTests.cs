using System.Net;
using System.Net.Http;
using System.Text;
using U7D2D.Core.Data;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;
using U7D2D.Core.Nexus;

namespace U7D2D.Core.Tests;

public sealed class NexusTests
{
    // ---------------------------------------------------------------- versions

    [Theory]
    [InlineData("1.2", "1.1", VersionRelation.LeftNewer)]
    [InlineData("1.1", "1.2", VersionRelation.RightNewer)]
    [InlineData("1.0", "1.0.0", VersionRelation.Equal)]
    [InlineData("v2", "2.0", VersionRelation.Equal)]
    [InlineData("3.4 (b333)", "3.4", VersionRelation.Equal)]
    [InlineData("1.0b", "1.0", VersionRelation.Inconclusive)]
    [InlineData("unknown", "1.0", VersionRelation.Inconclusive)]
    [InlineData("", "1.0", VersionRelation.Inconclusive)]
    public void Version_comparison_never_guesses(string left, string right, VersionRelation expected) =>
        Assert.Equal(expected, ModVersionComparer.Compare(left, right));

    [Fact]
    public void Only_unambiguously_newer_versions_flag_an_update()
    {
        Assert.True(ModVersionComparer.IsNewer("3.5", "3.4"));
        Assert.False(ModVersionComparer.IsNewer("3.4", "3.5"));
        Assert.False(ModVersionComparer.IsNewer("1.0b", "1.0"));
        Assert.True(ModVersionComparer.IsInconclusive("1.0b", "1.0"));
    }

    // ---------------------------------------------------------------- id detection

    [Theory]
    [InlineData("https://www.nexusmods.com/7daystodie/mods/12194?tab=description", 12194, "7daystodie")]
    [InlineData("https://www.nexusmods.com/cyberpunk2077/mods/123", 123, "cyberpunk2077")]
    public void Website_yields_mod_id_and_domain(string url, int id, string domain)
    {
        var found = NexusModIdResolver.FromWebsite(url);
        Assert.NotNull(found);
        Assert.Equal(id, found.ModId);
        Assert.Equal(domain, found.GameDomain);
    }

    [Theory]
    // Real filenames from the player's archive library
    [InlineData("Onsoku 12249 3.4 2026-11-10T03-48Z EjnRsi6xY.zip", 12249)]
    [InlineData("SingularityPerkV5-4111-5-1704885579.rar", 4111)]
    [InlineData("SomeMod-13102-2.zip", 13102)]
    public void Nexus_download_filenames_yield_mod_id(string name, int id) =>
        Assert.Equal(id, NexusModIdResolver.FromArchiveName(name)?.ModId);

    [Fact]
    public void Website_wins_over_archive_name()
    {
        var mod = new InstalledMod
        {
            FolderName = "CaseMod",
            Info = new ModMetadata { Website = "https://www.nexusmods.com/7daystodie/mods/13102" },
            SourceArchive = @"D:\mods\Other-99999-1-1700000000.zip"
        };

        var found = NexusModIdResolver.Resolve(mod);

        Assert.NotNull(found);
        Assert.Equal(13102, found.ModId);
        Assert.Equal("website", found.Source);
    }

    [Fact]
    public void Manual_binding_accepts_a_number_a_url_or_a_reset()
    {
        Assert.Equal(12194, NexusModIdResolver.ParseManual("mod", "12194")?.ModId);
        var url = NexusModIdResolver.ParseManual("mod", "https://www.nexusmods.com/7daystodie/mods/12194");
        Assert.Equal(12194, url?.ModId);
        Assert.True(url?.IsManual);
        Assert.Null(NexusModIdResolver.ParseManual("mod", ""));
        Assert.Null(NexusModIdResolver.ParseManual("mod", "not an id"));
    }

    // ---------------------------------------------------------------- key protection

    [Fact]
    public void Api_key_roundtrips_and_never_stays_plain_when_dpapi_is_available()
    {
        const string key = "personal-nexus-key-1234567890";
        var stored = NexusKeyProtection.Protect(key);

        Assert.DoesNotContain(key, stored);
        Assert.Equal(key, NexusKeyProtection.Unprotect(stored));
        Assert.Equal(string.Empty, NexusKeyProtection.Unprotect(null));
        Assert.Equal(string.Empty, NexusKeyProtection.Protect(string.Empty));
    }

    // ---------------------------------------------------------------- API client

    [Fact]
    public async Task Client_sends_required_aup_headers_and_reads_quota()
    {
        var handler = new StubHandler(request =>
        {
            // AUP: OAuth bearer token, never a personal API key.
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("apikey"));
            Assert.Equal(NexusApiClient.ApplicationName,
                request.Headers.GetValues("Application-Name").Single());
            Assert.Equal(NexusApiClient.ApplicationVersion,
                request.Headers.GetValues("Application-Version").Single());
            // User-Agent is parsed as several values: "Product/1.2.0" + "(Windows_NT 10.0; x64)".
            Assert.Contains(NexusApiClient.ApplicationName,
                string.Join(' ', request.Headers.GetValues("User-Agent")));

            return Json(HttpStatusCode.OK, """{"user_id":7,"name":"Tester","is_premium":false}""",
                new Dictionary<string, string>
                {
                    ["X-RL-Daily-Remaining"] = "2488",
                    ["X-RL-Hourly-Remaining"] = "96"
                });
        });

        using var client = new NexusApiClient(handler);
        var result = await client.ValidateAsync("secret");

        Assert.True(result.Success);
        Assert.Equal("Tester", result.Data?.Name);
        Assert.Equal(2488, result.RateLimit.DailyRemaining);
        Assert.Equal(96, result.RateLimit.HourlyRemaining);
    }

    [Fact]
    public async Task Rate_limit_response_is_reported_and_not_retried()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return Json((HttpStatusCode)429, "{}");
        });

        using var client = new NexusApiClient(handler);
        var result = await client.GetModAsync("secret", "7daystodie", 12194);

        Assert.False(result.Success);
        Assert.True(result.IsRateLimited);
        Assert.Contains("limit", result.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Files_response_is_unwrapped()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """
            {"files":[{"file_id":42,"name":"Main","mod_version":"2.0","file_name":"mod-2.0.zip",
                       "category_id":1,"is_primary":true,"uploaded_timestamp":1700000000}]}
            """));

        using var client = new NexusApiClient(handler);
        var result = await client.GetFilesAsync("secret", "7daystodie", 12194);

        Assert.True(result.Success);
        var file = Assert.Single(result.Data!);
        Assert.Equal(42, file.FileId);
        Assert.Equal("2.0", file.ModVersion);
        Assert.True(file.IsPrimary);
    }

    [Fact]
    public async Task Missing_key_never_produces_a_request()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return Json(HttpStatusCode.OK, "{}"); });

        using var client = new NexusApiClient(handler);
        var result = await client.ValidateAsync("");

        Assert.False(result.Success);
        Assert.Contains("API key", result.Error);
        Assert.Equal(0, calls);
    }

    // ---------------------------------------------------------------- posters

    [Fact]
    public async Task Mod_response_keeps_the_poster_address()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"mod_id":8099,"name":"0-XNPPCore","version":"3.4.05","picture_url":"https://staticdelivery.nexusmods.com/mods/105/images/8099/8099-1.jpeg"}"""));

        using var client = new NexusApiClient(handler);
        var result = await client.GetModAsync("secret", "7daystodie", 8099);

        Assert.Equal("https://staticdelivery.nexusmods.com/mods/105/images/8099/8099-1.jpeg",
            result.Data?.PictureUrl);
    }

    [Theory]
    [InlineData("https://staticdelivery.nexusmods.com/mods/105/images/8099/8099-1.jpeg")]
    [InlineData("https://www.nexusmods.com/7daystodie/mods/8099")]
    public void Poster_accepts_nexus_https_addresses(string url) =>
        Assert.Equal(url, NexusPoster.Normalize(url));

    [Theory]
    [InlineData("http://staticdelivery.nexusmods.com/mods/105/images/8099/8099-1.jpeg")]
    [InlineData("https://nexusmods.com.evil.test/x.jpeg")]
    [InlineData("https://evil.test/x.jpeg")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/x.jpeg")]
    [InlineData("")]
    [InlineData(null)]
    public void Poster_rejects_anything_else(string? url) =>
        Assert.Null(NexusPoster.Normalize(url));

    [Fact]
    public void Poster_address_survives_the_cache_roundtrip()
    {
        var item = new NexusModUpdate
        {
            FolderName = "0-XNPPCore",
            ModId = 8099,
            RemoteVersion = "3.4.05",
            Status = NexusCheckStatus.UpdateAvailable,
            PictureUrl = "https://staticdelivery.nexusmods.com/mods/105/images/8099/8099-1.jpeg"
        };

        var copy = JsonConfig.Deserialize<NexusModUpdate>(JsonConfig.Serialize(item));

        Assert.Equal(item.PictureUrl, copy?.PictureUrl);
    }

    // ---------------------------------------------------------------- OAuth 2.0 + PKCE

    [Fact]
    public void Pkce_challenge_is_base64url_sha256_of_the_verifier()
    {
        var verifier = NexusPkce.CreateVerifier();

        // RFC 7636: 43-128 characters from the unreserved set.
        Assert.Equal(43, verifier.Length);
        Assert.DoesNotContain('=', verifier);
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);

        var challenge = NexusPkce.CreateChallenge(verifier);
        Assert.Equal(43, challenge.Length);
        Assert.DoesNotContain('=', challenge);

        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var expected = Convert.ToBase64String(hash).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');

        Assert.Equal(expected, challenge);
        Assert.Equal(challenge, NexusPkce.CreateChallenge(verifier));
    }

    [Fact]
    public void Pkce_verifiers_are_not_repeated()
    {
        Assert.NotEqual(NexusPkce.CreateVerifier(), NexusPkce.CreateVerifier());
    }

    [Fact]
    public void Stored_client_id_overrides_the_build_default()
    {
        Assert.Equal("my-client", NexusOAuthOptions.ResolveClientId("my-client"));
        Assert.True(NexusOAuthOptions.IsUsable("my-client"));

        // The shipped placeholder and an empty value are not usable.
        Assert.False(NexusOAuthOptions.IsUsable(NexusOAuthOptions.DefaultClientId));
        Assert.False(NexusOAuthOptions.IsUsable(""));
        Assert.False(NexusOAuthOptions.IsUsable(null));
    }

    [Fact]
    public void Service_reports_configured_when_a_client_id_is_saved()
    {
        using var ws = new TestWorkspace();
        var store = NewSettingsStore(ws);
        var settings = store.Load();
        settings.NexusOAuthClientId = "my-client";
        store.Save(settings);

        using var http = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("no network expected")));
        var oauth = new NexusOAuthService(store, http);

        Assert.True(oauth.IsConfigured);
        Assert.Equal("my-client", oauth.ClientId);
    }

    [Fact]
    public async Task Sign_in_without_a_registered_client_id_fails_cleanly()
    {
        // If a real client id is supplied through the environment, this path is not the one under test.
        if (NexusOAuthOptions.IsConfigured) return;

        using var ws = new TestWorkspace();
        var store = NewSettingsStore(ws);
        using var http = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("no network expected")));
        var oauth = new NexusOAuthService(store, http);

        var opened = false;
        var result = await oauth.SignInAsync(_ => opened = true);

        Assert.False(result.Success);
        Assert.False(opened); // the browser must not open when the client id is unknown
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_silently()
    {
        using var ws = new TestWorkspace();
        var store = NewSettingsStore(ws);
        var settings = store.Load();
        settings.NexusAccessToken = NexusKeyProtection.Protect("old-token");
        settings.NexusRefreshToken = NexusKeyProtection.Protect("refresh-token");
        settings.NexusTokenExpiresUtc = DateTime.UtcNow.AddMinutes(-10);
        store.Save(settings);

        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("grant_type=refresh_token", request.Content!.ReadAsStringAsync().Result);
            return Json(HttpStatusCode.OK,
                """{"access_token":"new-token","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer"}""");
        });

        using var http = new HttpClient(handler);
        var oauth = new NexusOAuthService(store, http);

        var token = await oauth.GetAccessTokenAsync();

        Assert.Equal("new-token", token);
        Assert.Equal("new-token", NexusKeyProtection.Unprotect(store.Load().NexusAccessToken));
    }

    [Fact]
    public async Task Valid_access_token_is_used_without_a_network_call()
    {
        using var ws = new TestWorkspace();
        var store = NewSettingsStore(ws);
        var settings = store.Load();
        settings.NexusAccessToken = NexusKeyProtection.Protect("good-token");
        settings.NexusRefreshToken = NexusKeyProtection.Protect("refresh-token");
        settings.NexusTokenExpiresUtc = DateTime.UtcNow.AddHours(1);
        store.Save(settings);

        var handler = new StubHandler(_ =>
            throw new InvalidOperationException("no network call expected for a valid token"));

        using var http = new HttpClient(handler);
        var oauth = new NexusOAuthService(store, http);

        Assert.Equal("good-token", await oauth.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Rejected_refresh_clears_the_session()
    {
        using var ws = new TestWorkspace();
        var store = NewSettingsStore(ws);
        var settings = store.Load();
        settings.NexusAccessToken = NexusKeyProtection.Protect("old-token");
        settings.NexusRefreshToken = NexusKeyProtection.Protect("refresh-token");
        settings.NexusTokenExpiresUtc = DateTime.UtcNow.AddMinutes(-10);
        store.Save(settings);

        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("") });

        using var http = new HttpClient(handler);
        var oauth = new NexusOAuthService(store, http);

        Assert.Null(await oauth.GetAccessTokenAsync());
        Assert.False(oauth.IsSignedIn);
    }

    private static SettingsStore NewSettingsStore(TestWorkspace ws)
    {
        var database = new Database(ws.Paths);
        database.Initialize();
        return new SettingsStore(database);
    }

    private static HttpResponseMessage Json(
        HttpStatusCode code, string body, Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (headers is not null)
            foreach (var (name, value) in headers)
                response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
