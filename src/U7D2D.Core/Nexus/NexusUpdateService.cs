using System.Security.Cryptography;
using U7D2D.Core.Data;
using U7D2D.Core.I18n;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

namespace U7D2D.Core.Nexus;

/// <summary>
/// Orchestrates an on-demand Nexus update check.
///
/// Policy (agreed with the player): the manager NEVER downloads or installs anything
/// automatically. It only reports "a newer version exists on Nexus" and links to the
/// files/changelog pages. The existing local "Update" button stays the only way to
/// change the game's Mods folder, and it still creates a backup first.
///
/// Checks are started only from the UI button, never on a timer and never at startup.
/// Results are cached in SQLite so a restart shows the previous verdict for free.
/// </summary>
public sealed class NexusUpdateService
{
    private const int Md5LookupLimit = 20;
    private static readonly TimeSpan PoliteDelay = TimeSpan.FromMilliseconds(80);

    private readonly SettingsStore _settings;
    private readonly NexusStore _store;
    private readonly NexusApiClient _client;
    private readonly LogStore _log;
    private readonly CatalogStore _catalog;

    public NexusUpdateService(
        SettingsStore settings, NexusStore store, NexusApiClient client, LogStore log,
        CatalogStore catalog)
    {
        _settings = settings;
        _store = store;
        _client = client;
        _log = log;
        _catalog = catalog;
    }

    public string Domain =>
        NexusModIdResolver.NormalizeDomain(_settings.Load().NexusGameDomain);

    public Dictionary<string, NexusLink> GetLinks() => _store.GetLinks();

    public void SetLink(string folderName, int modId, string? gameDomain = null)
    {
        _store.SetLink(new NexusLink
        {
            FolderName = folderName,
            ModId = modId,
            GameDomain = NexusModIdResolver.NormalizeDomain(gameDomain ?? Domain),
            IsManual = true,
            Source = "manual"
        });
    }

    public void RemoveLink(string folderName) => _store.RemoveLink(folderName);

    public List<NexusModUpdate> GetCachedResults() => _store.LoadResults();

    public void ClearCachedResults() => _store.ClearResults();

    public async Task<NexusApiResult<NexusUserInfo>> ValidateKeyAsync(
        string plainKey, CancellationToken ct = default)
    {
        var result = await _client.ValidateAsync(plainKey, ct);
        _log.Add("nexus",
            result.Success
                ? Localization.SF("nx2.keyVerified", result.Data?.Name ?? Localization.S("nx2.user"))
                : Localization.SF("nx2.keyRejectedLog", result.Error ?? string.Empty),
            result.RateLimit.Summary);
        return result;
    }

    public async Task<NexusCheckReport> CheckAsync(
        IReadOnlyList<InstalledMod> mods,
        string? apiKey = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var report = new NexusCheckReport { CheckedUtc = DateTime.UtcNow };
        var settings = _settings.Load();
        var key = (apiKey ?? NexusKeyProtection.Unprotect(settings.NexusApiKey)).Trim();
        var domain = NexusModIdResolver.NormalizeDomain(settings.NexusGameDomain);

        if (string.IsNullOrWhiteSpace(key))
        {
            report.Error = Localization.S("nx2.keyMissing");
            return report;
        }

        // validate.json is free (does not count towards the hourly quota) and catches
        // a revoked key before we spend requests on every mod.
        var validation = await _client.ValidateAsync(key, ct);
        report.RateLimit = validation.RateLimit;
        if (!validation.Success)
        {
            report.Error = validation.Error;
            return report;
        }

        var links = _store.GetLinks();

        // Mods installed outside the manager have no source archive. The library is the only
        // place left where the original archive can still be found.
        var library = _catalog.LoadAll(ArchiveScanner.Version);
        var md5Used = 0;
        var index = 0;

        foreach (var mod in mods)
        {
            if (ct.IsCancellationRequested) { report.Aborted = true; break; }
            index++;
            progress?.Report(Localization.SF("nx2.checkProgress", index, mods.Count, mod.DisplayTitle));

            var item = new NexusModUpdate
            {
                FolderName = mod.FolderName,
                DisplayName = mod.DisplayTitle,
                LocalVersion = mod.Info.Version ?? string.Empty,
                CheckedUtc = report.CheckedUtc
            };

            var resolved = await ResolveLinkAsync(mod, links, key, domain, md5Used, ct, report, library);
            md5Used = resolved.Md5Used;
            var link = resolved.Link;
            if (link is null)
            {
                item.Status = NexusCheckStatus.NotLinked;
                item.Message = Localization.S("nx2.idNotFound");
                report.Items.Add(item);
                continue;
            }

            item.ModId = link.ModId;
            item.GameDomain = link.GameDomain;
            item.PageUrl = link.PageUrl;
            item.FilesUrl = link.FilesUrl;
            item.ChangelogUrl = link.ChangelogUrl;

            var info = await _client.GetModAsync(key, link.GameDomain, link.ModId, ct);
            MergeRateLimit(report, info.RateLimit);
            if (!info.Success || info.Data is null)
            {
                if (info.IsRateLimited)
                {
                    report.Error = info.Error;
                    report.Aborted = true;
                    break;
                }
                item.Status = info.IsNotFound ? NexusCheckStatus.ModNotFound : NexusCheckStatus.ApiError;
                item.Message = info.Error ?? Localization.S("nx2.requestError");
                report.Items.Add(item);
                await PolitePause(ct);
                continue;
            }

            item.RemoteVersion = info.Data.Version ?? string.Empty;
            item.RemoteName = info.Data.Name;
            if (info.Data.UpdatedTimestamp > 0)
                item.RemoteUpdatedUtc = DateTimeOffset.FromUnixTimeSeconds(info.Data.UpdatedTimestamp).UtcDateTime;

            var files = await _client.GetFilesAsync(key, link.GameDomain, link.ModId, ct);
            MergeRateLimit(report, files.RateLimit);
            if (!files.Success)
            {
                if (files.IsRateLimited)
                {
                    report.Error = files.Error;
                    report.Aborted = true;
                    break;
                }
                item.Status = NexusCheckStatus.NoFiles;
                item.Message = files.Error ?? Localization.S("nx2.filesUnavailable");
                report.Items.Add(item);
                await PolitePause(ct);
                continue;
            }

            var latest = PickLatestFile(files.Data);
            if (latest is not null)
            {
                item.LatestFileName = !string.IsNullOrWhiteSpace(latest.FileName) ? latest.FileName : latest.Name;
                item.LatestFileVersion = !string.IsNullOrWhiteSpace(latest.ModVersion)
                    ? latest.ModVersion : latest.Version;
                if (string.IsNullOrWhiteSpace(item.RemoteVersion))
                    item.RemoteVersion = item.LatestFileVersion ?? string.Empty;
            }

            Classify(item, mod.Info.Version, item.RemoteVersion);
            report.Items.Add(item);
            await PolitePause(ct);
        }

        _store.SaveResults(report.Items);
        progress?.Report(Localization.S("nx2.done"));

        _log.Add("nexus",
            report.Error is null
                ? Localization.SF("nx2.summaryLog",
                    report.Items.Count, report.UpdatesCount, report.NotLinkedCount)
                : Localization.SF("nx2.abortedLog", report.Error),
            report.RateLimit.Summary);

        return report;
    }

    private async Task<(NexusLink? Link, int Md5Used)> ResolveLinkAsync(
        InstalledMod mod,
        Dictionary<string, NexusLink> manualLinks,
        string apiKey,
        string domain,
        int md5Used,
        CancellationToken ct,
        NexusCheckReport report,
        List<ArchiveScanResult> library)
    {
        if (manualLinks.TryGetValue(mod.FolderName, out var manual)) return (manual, md5Used);

        var detected = NexusModIdResolver.Resolve(mod);
        if (detected is not null)
        {
            var auto = new NexusLink
            {
                FolderName = mod.FolderName, ModId = detected.ModId,
                GameDomain = detected.GameDomain, IsManual = false, Source = detected.Source
            };
            // Persist automatic matches too: the UI shows the resolved id from this table,
            // otherwise a successfully checked mod was labelled "not linked".
            _store.SetLink(auto);
            manualLinks[mod.FolderName] = auto;
            return (auto, md5Used);
        }

        // No id in the metadata and no source archive, which is the normal case for mods
        // installed by hand or by another manager. Look for the same mod in the archive library.
        var archivePath = mod.SourceArchive;
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            archivePath = FindArchiveInLibrary(mod, library);

            if (archivePath is not null)
            {
                var byName = NexusModIdResolver.FromArchiveName(Path.GetFileName(archivePath));
                if (byName is not null)
                {
                    var fromLibrary = new NexusLink
                    {
                        FolderName = mod.FolderName, ModId = byName.ModId,
                        GameDomain = byName.GameDomain, IsManual = false, Source = "library"
                    };
                    _store.SetLink(fromLibrary);
                    manualLinks[mod.FolderName] = fromLibrary;
                    return (fromLibrary, md5Used);
                }
            }
        }

        // Last resort: identify the downloaded archive by its MD5. Capped so a library
        // of unlinked mods cannot burn the whole daily quota in one click.
        if (md5Used >= Md5LookupLimit || string.IsNullOrWhiteSpace(archivePath) ||
            !File.Exists(archivePath))
        {
            // Worth a log line: without it the UI only says "not linked" and nobody can tell
            // whether the key, the archive or the quota was the reason.
            _log.Add("nexus", Localization.SF("nx2.noIdLog", mod.FolderName),
                string.IsNullOrWhiteSpace(archivePath) ? "no source archive" : "archive unavailable or quota");
            return (null, md5Used);
        }

        md5Used++;
        string hash;
        try
        {
            await using var stream = File.OpenRead(archivePath);
            hash = Convert.ToHexString(await MD5.HashDataAsync(stream, ct)).ToLowerInvariant();
        }
        catch (IOException) { return (null, md5Used); }
        catch (UnauthorizedAccessException) { return (null, md5Used); }

        var found = await _client.Md5SearchAsync(apiKey, domain, hash, ct);
        MergeRateLimit(report, found.RateLimit);
        var match = found.Data?.FirstOrDefault(m => m.ModId > 0);
        if (match is null)
        {
            _log.Add("nexus", Localization.SF("nx2.noIdLog", mod.FolderName), "hash not found on Nexus");
            return (null, md5Used);
        }

        var link = new NexusLink
        {
            FolderName = mod.FolderName, ModId = match.ModId, GameDomain = domain,
            IsManual = false, Source = "md5"
        };
        _store.SetLink(link);
        manualLinks[mod.FolderName] = link;
        return (link, md5Used);
    }

    /// <summary>
    /// Finds the archive of an installed mod in the scanned library by name, preferring the same
    /// version. Used when the mod was not installed through the manager and has no source archive.
    /// </summary>
    private static string? FindArchiveInLibrary(InstalledMod mod, List<ArchiveScanResult> library)
    {
        var wanted = NormalizeName(mod.Info.Name);
        if (wanted.Length == 0) return null;

        var candidates = library
            .Where(a => a.Success)
            .SelectMany(a => a.Mods)
            .Where(p => NormalizeName(p.Info.Name) == wanted)
            .OrderByDescending(p => string.Equals(
                p.Info.Version?.Trim(), mod.Info.Version?.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p.Info.Version ?? string.Empty)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.ArchivePath) && File.Exists(candidate.ArchivePath))
                return candidate.ArchivePath;
        }

        return null;
    }

    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));

        return builder.ToString();
    }

    private static NexusModFile? PickLatestFile(List<NexusModFile>? files)
    {
        if (files is null || files.Count == 0) return null;
        return files
            .OrderByDescending(f => f.IsPrimary)
            .ThenByDescending(f => f.CategoryId == 1)
            .ThenByDescending(f => f.UploadedTimestamp)
            .First();
    }

    private static void Classify(NexusModUpdate item, string? local, string? remote)
    {
        if (string.IsNullOrWhiteSpace(local))
        {
            item.Status = NexusCheckStatus.UncomparableVersion;
            item.Message = Localization.S("nx2.noLocalVersion");
            return;
        }
        if (string.IsNullOrWhiteSpace(remote))
        {
            item.Status = NexusCheckStatus.NoFiles;
            item.Message = Localization.S("nx2.noRemoteVersion");
            return;
        }

        var relation = ModVersionComparer.Compare(remote, local);
        switch (relation)
        {
            case VersionRelation.LeftNewer:
                item.Status = NexusCheckStatus.UpdateAvailable;
                item.Message = Localization.SF("nx2.updateAvailable", remote, local);
                break;
            case VersionRelation.Inconclusive:
                item.Status = NexusCheckStatus.UncomparableVersion;
                item.Message = Localization.SF("nx2.uncomparable", local, remote);
                break;
            default:
                item.Status = NexusCheckStatus.UpToDate;
                item.Message = Localization.S("nx2.upToDate");
                break;
        }
    }

    private static void MergeRateLimit(NexusCheckReport report, NexusRateLimit? incoming)
    {
        if (incoming is null) return;
        report.RateLimit = incoming;
    }

    private static async Task PolitePause(CancellationToken ct)
    {
        try { await Task.Delay(PoliteDelay, ct); }
        catch (OperationCanceledException) { }
    }
}
