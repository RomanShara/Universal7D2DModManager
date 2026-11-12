using System.Text.Json.Serialization;
using U7D2D.Core.I18n;

namespace U7D2D.Core.Models;

// ---------------------------------------------------------------- API payloads (v1 REST, snake_case)

public sealed class NexusUserInfo
{
    [JsonPropertyName("user_id")] public int UserId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("is_premium")] public bool IsPremium { get; set; }
    [JsonPropertyName("is_supporter")] public bool IsSupporter { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("profile_url")] public string? ProfileUrl { get; set; }
}

public sealed class NexusModInfo
{
    [JsonPropertyName("mod_id")] public int ModId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("author")] public string Author { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("updated_timestamp")] public long UpdatedTimestamp { get; set; }
    [JsonPropertyName("domain_name")] public string DomainName { get; set; } = string.Empty;
}

public sealed class NexusModFile
{
    [JsonPropertyName("file_id")] public int FileId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("mod_version")] public string ModVersion { get; set; } = string.Empty;
    [JsonPropertyName("file_name")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("category_id")] public int CategoryId { get; set; }
    [JsonPropertyName("category_name")] public string CategoryName { get; set; } = string.Empty;
    [JsonPropertyName("is_primary")] public bool IsPrimary { get; set; }
    [JsonPropertyName("uploaded_timestamp")] public long UploadedTimestamp { get; set; }
}

public sealed class NexusMd5Match
{
    public int ModId { get; set; }
    public string ModName { get; set; } = string.Empty;
    public string ModVersion { get; set; } = string.Empty;
    public int FileId { get; set; }
    public string FileName { get; set; } = string.Empty;
}

/// <summary>Quota reported by the X-RL-* headers of any Nexus API response.</summary>
public sealed class NexusRateLimit
{
    public int? HourlyLimit { get; set; }
    public int? HourlyRemaining { get; set; }
    public string? HourlyReset { get; set; }
    public int? DailyLimit { get; set; }
    public int? DailyRemaining { get; set; }
    public string? DailyReset { get; set; }

    public bool IsExhausted =>
        (HourlyRemaining.HasValue && HourlyRemaining <= 0) ||
        (DailyRemaining.HasValue && DailyRemaining <= 0);

    public string Summary => DailyRemaining.HasValue && HourlyRemaining.HasValue
        ? Localization.SF("model.quotaRemaining", DailyRemaining, HourlyRemaining)
        : Localization.S("model.quotaUnknown");
}

// ---------------------------------------------------------------- local state

/// <summary>Binding between an installed mod folder and a Nexus mod page.</summary>
public sealed class NexusLink
{
    public string FolderName { get; set; } = string.Empty;
    public int ModId { get; set; }
    public string GameDomain { get; set; } = "7daystodie";

    /// <summary>True when the player typed the id, false when it was detected automatically.</summary>
    public bool IsManual { get; set; }

    /// <summary>Where the id came from: "website", "archive", "md5", "manual".</summary>
    public string Source { get; set; } = string.Empty;

    public string PageUrl => $"https://www.nexusmods.com/{GameDomain}/mods/{ModId}";
    public string FilesUrl => PageUrl + "?tab=files";
    public string ChangelogUrl => PageUrl + "?tab=changelog";
}

public enum NexusCheckStatus
{
    Unknown = 0,
    NotLinked,
    UpToDate,
    UpdateAvailable,
    UncomparableVersion,
    NoFiles,
    ModNotFound,
    ApiError
}

/// <summary>Result of checking one installed mod against Nexus. Cached in SQLite between launches.</summary>
public sealed class NexusModUpdate
{
    public string FolderName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LocalVersion { get; set; } = string.Empty;
    public string RemoteVersion { get; set; } = string.Empty;
    public string RemoteName { get; set; } = string.Empty;
    public int ModId { get; set; }
    public string GameDomain { get; set; } = "7daystodie";
    public string? PageUrl { get; set; }
    public string? FilesUrl { get; set; }
    public string? ChangelogUrl { get; set; }
    public string? LatestFileName { get; set; }
    public string? LatestFileVersion { get; set; }
    public DateTime? RemoteUpdatedUtc { get; set; }
    public NexusCheckStatus Status { get; set; } = NexusCheckStatus.Unknown;
    public string Message { get; set; } = string.Empty;
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;

    public bool IsUpdate => Status == NexusCheckStatus.UpdateAvailable;
}

public sealed class NexusCheckReport
{
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
    public List<NexusModUpdate> Items { get; set; } = new();
    public NexusRateLimit RateLimit { get; set; } = new();

    /// <summary>Global failure: missing/invalid key, network problem, rate limit. No per-mod checks were made.</summary>
    public string? Error { get; set; }

    /// <summary>True when the run stopped early (429 or cancellation).</summary>
    public bool Aborted { get; set; }

    public int UpdatesCount => Items.Count(i => i.IsUpdate);
    public int NotLinkedCount => Items.Count(i => i.Status == NexusCheckStatus.NotLinked);
    public int ErrorCount => Items.Count(i => i.Status is NexusCheckStatus.ApiError or NexusCheckStatus.ModNotFound);
}
