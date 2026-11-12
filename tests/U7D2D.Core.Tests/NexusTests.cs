using System.Net;
using System.Net.Http;
using System.Text;
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
            Assert.Equal("secret", request.Headers.GetValues("apikey").Single());
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
