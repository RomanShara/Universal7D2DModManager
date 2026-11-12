using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;

namespace U7D2D.Core.Nexus;

public sealed class NexusApiResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public int StatusCode { get; init; }
    public string? Error { get; init; }
    public NexusRateLimit RateLimit { get; init; } = new();

    public bool IsAuthError => StatusCode is 401 or 403;
    public bool IsRateLimited => StatusCode == 429;
    public bool IsNotFound => StatusCode == 404;

    /// <summary>Nexus or its CDN failed on their side: the key itself is not the problem.</summary>
    public bool IsServerError => StatusCode >= 500;
}

/// <summary>
/// Thin client over the stable v1 REST API (https://api.nexusmods.com/v1).
/// v2 GraphQL is still marked "work in progress" by Nexus, and v3 REST does not
/// cover the mod/file endpoints a mod manager needs.
///
/// AUP requirements implemented here: apikey header, Application-Name,
/// Application-Version and an identifying User-Agent on every request.
/// Quota is read from X-RL-* headers instead of hardcoding a number: the published
/// limits changed between the Swagger spec and the June 2026 help article.
/// </summary>
public sealed class NexusApiClient : IDisposable
{
    public const string ApplicationName = "Universal7D2DModManager";
    public const string ApplicationVersion = "1.2.2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public NexusApiClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.nexusmods.com/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _ownsClient = true;
    }

    /// <summary>Test seam: supply a fake handler, no network is used.</summary>
    public NexusApiClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.nexusmods.com/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _ownsClient = true;
    }

    public Task<NexusApiResult<NexusUserInfo>> ValidateAsync(string apiKey, CancellationToken ct = default) =>
        SendAsync<NexusUserInfo>(HttpMethod.Get, "v1/users/validate.json", apiKey, ct);

    public Task<NexusApiResult<NexusModInfo>> GetModAsync(
        string apiKey, string gameDomain, int modId, CancellationToken ct = default) =>
        SendAsync<NexusModInfo>(HttpMethod.Get,
            $"v1/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}.json", apiKey, ct);

    public async Task<NexusApiResult<List<NexusModFile>>> GetFilesAsync(
        string apiKey, string gameDomain, int modId, CancellationToken ct = default)
    {
        var raw = await SendAsync<FilesResponse>(HttpMethod.Get,
            $"v1/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files.json", apiKey, ct);

        return new NexusApiResult<List<NexusModFile>>
        {
            Success = raw.Success,
            Data = raw.Data?.Files ?? new List<NexusModFile>(),
            StatusCode = raw.StatusCode,
            Error = raw.Error,
            RateLimit = raw.RateLimit
        };
    }

    public async Task<NexusApiResult<List<NexusMd5Match>>> Md5SearchAsync(
        string apiKey, string gameDomain, string md5Hex, CancellationToken ct = default)
    {
        var path = $"v1/games/{Uri.EscapeDataString(gameDomain)}/mods/md5_search/{md5Hex}.json";
        var raw = await SendAsync<string>(HttpMethod.Get, path, apiKey, ct, readRaw: true);
        if (!raw.Success || string.IsNullOrWhiteSpace(raw.Data))
            return new NexusApiResult<List<NexusMd5Match>>
            {
                Success = raw.Success, StatusCode = raw.StatusCode,
                Error = raw.Error, RateLimit = raw.RateLimit,
                Data = new List<NexusMd5Match>()
            };

        try
        {
            using var document = JsonDocument.Parse(raw.Data);
            var matches = new List<NexusMd5Match>();
            foreach (var node in document.RootElement.EnumerateArray())
            {
                var mod = node.TryGetProperty("mod", out var m) ? m : default;
                var file = node.TryGetProperty("file_details", out var f) ? f : default;

                matches.Add(new NexusMd5Match
                {
                    ModId = GetInt(mod, "mod_id"),
                    ModName = GetString(mod, "name"),
                    ModVersion = GetString(mod, "version"),
                    FileId = GetInt(file, "file_id"),
                    FileName = GetString(file, "file_name")
                });
            }
            return new NexusApiResult<List<NexusMd5Match>>
            {
                Success = true, Data = matches, StatusCode = raw.StatusCode, RateLimit = raw.RateLimit
            };
        }
        catch (JsonException e)
        {
            return new NexusApiResult<List<NexusMd5Match>>
            {
                Success = false, Data = new List<NexusMd5Match>(),
                StatusCode = raw.StatusCode, Error = Localization.SF("nx2.md5Format", e.Message),
                RateLimit = raw.RateLimit
            };
        }
    }

    // ---------------------------------------------------------------- transport

    private async Task<NexusApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, string apiKey, CancellationToken ct, bool readRaw = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return Failure<T>(Localization.S("nx2.keyNotSet"), 0);

        try
        {
            var key = NormalizeKey(apiKey);

            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("apikey", key);
            request.Headers.TryAddWithoutValidation("Application-Name", ApplicationName);
            request.Headers.TryAddWithoutValidation("Application-Version", ApplicationVersion);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation(
                "User-Agent", $"{ApplicationName}/{ApplicationVersion} (Windows_NT 10.0; x64)");

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, ct);

            var rateLimit = ReadRateLimit(response);
            var body = await response.Content.ReadAsStringAsync(ct);

            // 500 and the Cloudflare 52x family are transient on the Nexus side. One quiet retry
            // saves the user from a red toast that has nothing to do with the key.
            if ((int)response.StatusCode >= 500 && (int)response.StatusCode != 501)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);

                using var retry = new HttpRequestMessage(method, path);
                retry.Headers.TryAddWithoutValidation("apikey", key);
                retry.Headers.TryAddWithoutValidation("Application-Name", ApplicationName);
                retry.Headers.TryAddWithoutValidation("Application-Version", ApplicationVersion);
                retry.Headers.TryAddWithoutValidation("Accept", "application/json");
                retry.Headers.TryAddWithoutValidation(
                    "User-Agent", $"{ApplicationName}/{ApplicationVersion} (Windows_NT 10.0; x64)");

                using var second = await _http.SendAsync(
                    retry, HttpCompletionOption.ResponseContentRead, ct);

                rateLimit = ReadRateLimit(second);
                body = await second.Content.ReadAsStringAsync(ct);

                if (!second.IsSuccessStatusCode)
                {
                    return new NexusApiResult<T>
                    {
                        Success = false,
                        StatusCode = (int)second.StatusCode,
                        Error = ErrorText(second.StatusCode),
                        RateLimit = rateLimit
                    };
                }

                var retried = JsonSerializer.Deserialize<T>(body, JsonOptions);
                return new NexusApiResult<T>
                {
                    Success = retried is not null, Data = retried,
                    StatusCode = (int)second.StatusCode,
                    Error = retried is null ? Localization.S("nx2.emptyResponse") : null,
                    RateLimit = rateLimit
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new NexusApiResult<T>
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    Error = ErrorText(response.StatusCode),
                    RateLimit = rateLimit
                };
            }

            if (readRaw && typeof(T) == typeof(string))
                return new NexusApiResult<T>
                {
                    Success = true, Data = (T)(object)body,
                    StatusCode = (int)response.StatusCode, RateLimit = rateLimit
                };

            var data = JsonSerializer.Deserialize<T>(body, JsonOptions);
            return new NexusApiResult<T>
            {
                Success = data is not null, Data = data,
                StatusCode = (int)response.StatusCode,
                Error = data is null ? Localization.S("nx2.emptyResponse") : null,
                RateLimit = rateLimit
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure<T>(Localization.S("nx2.checkCancelled"), 0);
        }
        catch (TaskCanceledException)
        {
            return Failure<T>(Localization.S("nx2.timeout"), 0);
        }
        catch (HttpRequestException e)
        {
            return Failure<T>(Localization.SF("nx2.noConnection", e.Message), 0);
        }
        catch (JsonException e)
        {
            return Failure<T>(Localization.SF("nx2.unexpectedFormat", e.Message), 0);
        }
    }

    /// <summary>
    /// Cleans a pasted key. Copying from the Nexus page often brings a newline, a zero-width space
    /// or surrounding quotes, and the API answers with a server error instead of a clear rejection.
    /// </summary>
    private static string NormalizeKey(string raw)
    {
        var cleaned = raw
            .Replace("\uFEFF", string.Empty)
            .Replace("\u200B", string.Empty)
            .Replace("\u00A0", " ")
            .Trim()
            .Trim('"', '\'', '`');

        if (cleaned.StartsWith("apikey:", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned["apikey:".Length..];

        if (cleaned.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned["Bearer ".Length..];

        return new string(cleaned.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
    }

    private static NexusApiResult<T> Failure<T>(string message, int code) =>
        new() { Success = false, StatusCode = code, Error = message };

    private static string ErrorText(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            Localization.S("nx2.keyInvalid"),
        HttpStatusCode.NotFound =>
            Localization.S("nx2.modNotFound"),
        (HttpStatusCode)429 =>
            Localization.S("nx2.rateLimited"),
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway =>
            Localization.S("nx2.unavailable"),
        // Cloudflare reports 520-527 when it cannot reach or handshake with the Nexus origin.
        (HttpStatusCode)520 or (HttpStatusCode)521 or (HttpStatusCode)522 or (HttpStatusCode)523
            or (HttpStatusCode)524 or (HttpStatusCode)525 or (HttpStatusCode)526 or (HttpStatusCode)527 =>
            Localization.SF("nx2.cdnError", (int)code),
        HttpStatusCode.InternalServerError or HttpStatusCode.NotImplemented or
            HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
            Localization.SF("nx2.serverBusy", (int)code),
        _ => Localization.SF("nx2.httpError", (int)code)
    };

    private static NexusRateLimit ReadRateLimit(HttpResponseMessage response)
    {
        var result = new NexusRateLimit();
        result.HourlyLimit = HeaderInt(response, "X-RL-Hourly-Limit");
        result.HourlyRemaining = HeaderInt(response, "X-RL-Hourly-Remaining");
        result.DailyLimit = HeaderInt(response, "X-RL-Daily-Limit");
        result.DailyRemaining = HeaderInt(response, "X-RL-Daily-Remaining");
        result.HourlyReset = HeaderString(response, "X-RL-Hourly-Reset");
        result.DailyReset = HeaderString(response, "X-RL-Daily-Reset");
        return result;
    }

    private static int? HeaderInt(HttpResponseMessage response, string name)
    {
        var value = HeaderString(response, name);
        return int.TryParse(value, out var number) ? number : null;
    }

    private static string? HeaderString(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number : 0;

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private sealed class FilesResponse
    {
        [JsonPropertyName("files")] public List<NexusModFile> Files { get; set; } = new();
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
