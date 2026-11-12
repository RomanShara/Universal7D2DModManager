using CommunityToolkit.Mvvm.ComponentModel;
using U7D2D.Core.Models;
using U7D2D.Core.Nexus;

namespace U7D2D.App.ViewModels;

/// <summary>
/// Nexus integration for the UI. Checks are strictly on demand — no timers, no
/// startup requests, no downloads, no changes to the game's Mods folder.
/// </summary>
public partial class AppViewModel
{
    [ObservableProperty] private string _nexusApiKeyInput = string.Empty;
    [ObservableProperty] private string _nexusUserText = Localization.S("nx.keyNotChecked");
    [ObservableProperty] private string _nexusQuotaText = Localization.S("nx.quotaUnknown");
    [ObservableProperty] private string _nexusStatusText = string.Empty;
    [ObservableProperty] private string _nexusLastCheckText = Localization.S("nx.notCheckedYet");
    [ObservableProperty] private bool _isCheckingNexus;
    [ObservableProperty] private bool _isValidatingNexusKey;
    [ObservableProperty] private int _nexusUpdatesCount;

    /// <summary>Fallback for mods whose id could not be detected: the game's mod list on Nexus.</summary>
    public string NexusGameModsUrl =>
        $"https://www.nexusmods.com/{NexusModIdResolver.NormalizeDomain(Settings.NexusGameDomain)}/mods";

    /// <summary>Restores cached verdicts and link labels after start or a refresh.</summary>
    public void LoadNexusState()
    {
        var links = _manager.GetNexusLinks();
        var results = _manager.GetCachedNexusResults();

        ApplyNexusResults(results, links);
    }

    private void ApplyNexusResults(
        IReadOnlyList<NexusModUpdate> results,
        Dictionary<string, NexusLink>? links = null)
    {
        links ??= _manager.GetNexusLinks();
        var byFolder = results
            .GroupBy(r => r.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var card in InstalledMods)
        {
            NexusModUpdate? verdict = byFolder.TryGetValue(card.Folder, out var cached) ? cached : null;
            // The player may have installed the update since the last Nexus check. A cached
            // "3.5 available" badge must not stay on a mod that is already at 3.5.
            if (verdict is not null)
                RefreshVerdict(verdict, card.Model.Info.Version);

            card.Nexus = verdict;
            if (links.TryGetValue(card.Folder, out var link))
                card.NexusIdLabel = $"Nexus #{link.ModId}" + (link.IsManual ? Localization.S("nx.manualSuffix") : string.Empty);
            else if (verdict?.ModId > 0)
                card.NexusIdLabel = $"Nexus #{verdict.ModId}" + Localization.S("nx.autoSuffix");
            else
                card.NexusIdLabel = Localization.S("nx.notLinked");
        }

        NexusUpdatesCount = InstalledMods.Count(c => c.HasNexusUpdate);
        var last = results.MaxBy(r => r.CheckedUtc)?.CheckedUtc;
        NexusLastCheckText = last is null
            ? Localization.S("nx.notCheckedYet")
            : Localization.SF("nx.lastCheck", last.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
    }

    private static void RefreshVerdict(NexusModUpdate verdict, string? currentLocalVersion)
    {
        // Older builds cached a harsher "set the ID manually" wording. Show the current
        // explanation even before the next check.
        if (verdict.Status == NexusCheckStatus.NotLinked)
        {
            verdict.Message = Localization.S("nx.idNotFoundMessage");
            return;
        }

        if (string.IsNullOrWhiteSpace(currentLocalVersion) ||
            string.IsNullOrWhiteSpace(verdict.RemoteVersion) ||
            verdict.Status is NexusCheckStatus.NotLinked or NexusCheckStatus.ModNotFound or NexusCheckStatus.ApiError)
            return;

        verdict.LocalVersion = currentLocalVersion;
        switch (ModVersionComparer.Compare(verdict.RemoteVersion, currentLocalVersion))
        {
            case VersionRelation.LeftNewer:
                verdict.Status = NexusCheckStatus.UpdateAvailable;
                verdict.Message = Localization.SF("nx.updateAvailable", verdict.RemoteVersion, currentLocalVersion);
                break;
            case VersionRelation.Inconclusive:
                verdict.Status = NexusCheckStatus.UncomparableVersion;
                verdict.Message = Localization.SF("nx.uncomparable", currentLocalVersion, verdict.RemoteVersion);
                break;
            default:
                verdict.Status = NexusCheckStatus.UpToDate;
                verdict.Message = Localization.S("nx.upToDate");
                break;
        }
    }

    public async Task ValidateNexusKeyAsync()
    {
        if (IsValidatingNexusKey) return;
        var key = NexusApiKeyInput?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            _toasts.Warning(Localization.S("nx.keyMissingTitle"), Localization.S("nx.keyMissingHint"));
            return;
        }

        IsValidatingNexusKey = true;
        NexusStatusText = Localization.S("nx.checkingKey");
        try
        {
            var result = await _manager.ValidateNexusKeyAsync(key);
            NexusQuotaText = result.RateLimit.Summary;

            if (!result.Success || result.Data is null)
            {
                // A 500 or a Cloudflare 52x means Nexus failed, not the key. Saying "key rejected"
                // there sends the user to regenerate a key that was fine all along.
                var serverSide = result.IsServerError;

                NexusUserText = serverSide
                    ? Localization.S("nx.serverBusy")
                    : Localization.S("nx.keyRejected");

                _toasts.Error(
                    serverSide ? Localization.S("nx.serverBusyTitle") : Localization.S("nx.keyRejectedTitle"),
                    result.Error ?? Localization.S("nx.unknownError"));
                return;
            }

            NexusUserText = Localization.SF("nx.userLine", result.Data.Name,
                result.Data.IsPremium ? Localization.S("nx.yes") : Localization.S("nx.no"));
            _toasts.Success(Localization.S("nx.keyWorksTitle"),
                Localization.SF("nx.keyWorksText", result.Data.Name, result.RateLimit.Summary));

            // Persist immediately so the key survives even if the player does not press Save.
            Settings.NexusApiKey = NexusKeyProtection.Protect(key);
            _manager.SaveSettings(Settings);
        }
        catch (Exception e)
        {
            _toasts.Error(Localization.S("nx.keyCheckError"), e.Message);
        }
        finally
        {
            IsValidatingNexusKey = false;
            NexusStatusText = string.Empty;
        }
    }

    public async Task CheckNexusUpdatesAsync()
    {
        if (IsCheckingNexus) return;
        if (string.IsNullOrWhiteSpace(NexusApiKeyInput))
        {
            _toasts.Warning(Localization.S("nx.keyNeededTitle"), Localization.S("nx.keyNeededHint"));
            return;
        }

        IsCheckingNexus = true;
        NexusStatusText = Localization.S("nx.connecting");
        try
        {
            // The facade reads the persisted (encrypted) key, not the text box, so a freshly
            // pasted key must be stored before the check even if the player skipped Save.
            Settings.NexusApiKey = NexusKeyProtection.Protect(NexusApiKeyInput.Trim());
            _manager.SaveSettings(Settings);

            var progress = new Progress<string>(text => NexusStatusText = text);
            var report = await _manager.CheckNexusUpdatesAsync(progress);
            NexusQuotaText = report.RateLimit.Summary;

            if (report.Error is not null)
            {
                _toasts.Error(Localization.S("nx.checkFailedTitle"), report.Error);
                return;
            }

            ApplyNexusResults(report.Items);

            var summary = Localization.SF("nx.summary", report.UpdatesCount, report.NotLinkedCount, report.ErrorCount);
            if (report.Aborted)
                _toasts.Warning(Localization.S("nx.checkAborted"), summary + ". " + report.RateLimit.Summary);
            else if (report.UpdatesCount > 0)
                _toasts.Success(Localization.S("nx.updatesFound"), summary);
            else
                _toasts.Info(Localization.S("nx.checkDone"), summary);
        }
        catch (Exception e)
        {
            _toasts.Error(Localization.S("nx.checkError"), e.Message);
        }
        finally
        {
            IsCheckingNexus = false;
            NexusStatusText = string.Empty;
        }
    }

    public void OpenNexusUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)) _shell.OpenUrl(url);
    }

    /// <summary>Sets (or clears, with an empty value) the manual Nexus id for a mod.</summary>
    public void SaveNexusLink(string folderName, string input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            _manager.RemoveNexusLink(folderName);
            _toasts.Info(Localization.S("nx.linkRemoved"), folderName);
        }
        else
        {
            var link = NexusModIdResolver.ParseManual(folderName, text);
            if (link is null)
            {
                _toasts.Warning(Localization.S("nx.idNotRecognized"), Localization.S("nx.idNotRecognizedHint"));
                return;
            }
            _manager.SetNexusLink(link.FolderName, link.ModId, link.GameDomain);
            _toasts.Success(Localization.S("nx.linkSaved"), Localization.SF("nx.linkSavedText", folderName, link.ModId));
        }

        var card = InstalledMods.FirstOrDefault(c => c.Folder == folderName);
        if (card is not null)
        {
            card.Nexus = null; // force a fresh verdict on the next check
            card.NexusIdLabel = text.Length == 0 ? Localization.S("nx.notLinked") : Localization.S("nx.linkedManual");
        }
    }

    public void ClearNexusResults()
    {
        _manager.ClearNexusResults();
        foreach (var card in InstalledMods) card.Nexus = null;
        NexusUpdatesCount = 0;
        NexusLastCheckText = Localization.S("nx.resultsCleared");
        _toasts.Info(Localization.S("nx.resultsClearedTitle"));
    }
}
