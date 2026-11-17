using CommunityToolkit.Mvvm.ComponentModel;
using U7D2D.Core.Models;
using U7D2D.Core.Nexus;

namespace U7D2D.App.ViewModels;

/// <summary>
/// Nexus integration for the UI. Checks are strictly on demand - no timers, no
/// startup requests, no downloads, no changes to the game's Mods folder.
///
/// Sign-in uses OAuth 2.0 + PKCE: the user approves the app in the browser and the manager never
/// asks for a personal API key (the Nexus AUP does not allow that).
/// </summary>
public partial class AppViewModel
{
    [ObservableProperty] private string _nexusUserText = Localization.S("nx.keyNotChecked");
    [ObservableProperty] private string _nexusQuotaText = Localization.S("nx.quotaUnknown");
    [ObservableProperty] private string _nexusStatusText = string.Empty;
    [ObservableProperty] private string _nexusLastCheckText = Localization.S("nx.notCheckedYet");
    [ObservableProperty] private bool _isCheckingNexus;
    [ObservableProperty] private bool _isSigningInNexus;
    [ObservableProperty] private bool _isNexusSignedIn;
    [ObservableProperty] private string _nexusAccountText = Localization.S("nx.notSignedIn");
    [ObservableProperty] private int _nexusUpdatesCount;
    [ObservableProperty] private string _nexusClientIdInput = string.Empty;
    [ObservableProperty] private bool _hasNexusClientId;

    /// <summary>Fallback for mods whose id could not be detected: the game's mod list on Nexus.</summary>
    public string NexusGameModsUrl =>
        $"https://www.nexusmods.com/{NexusModIdResolver.NormalizeDomain(Settings.NexusGameDomain)}/mods";

    /// <summary>Restores cached verdicts and link labels after start or a refresh.</summary>
    public void LoadNexusState()
    {
        var links = _manager.GetNexusLinks();
        var results = _manager.GetCachedNexusResults();

        ApplyNexusResults(results, links);
        RefreshNexusAccountState();
    }

    /// <summary>Updates the "signed in as" label from persisted OAuth state.</summary>
    public void RefreshNexusAccountState()
    {
        IsNexusSignedIn = _manager.HasNexusAccount;
        HasNexusClientId = _manager.HasNexusOAuthClientId;

        // Prefill from the saved override only; never overwrite what the user is typing.
        if (string.IsNullOrWhiteSpace(NexusClientIdInput))
            NexusClientIdInput = Settings.NexusOAuthClientId;

        var name = _manager.NexusAccountName;

        NexusAccountText = !IsNexusSignedIn
            ? Localization.S("nx.notSignedIn")
            : string.IsNullOrWhiteSpace(name)
                ? Localization.S("nx.signedIn")
                : Localization.SF("nx.signedInAs", name);
    }

    private void ApplyNexusResults(
        IReadOnlyList<NexusModUpdate> results,
        Dictionary<string, NexusLink>? links = null)
    {
        links ??= _manager.GetNexusLinks();
        var libraryVersions = _manager.GetLibraryVersions();
        var byFolder = results
            .GroupBy(r => r.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var card in InstalledMods)
        {
            NexusModUpdate? verdict = byFolder.TryGetValue(card.Folder, out var cached) ? cached : null;
            // The player may have installed the update since the last Nexus check. A cached
            // "3.5 available" badge must not stay on a mod that is already at 3.5.
            if (verdict is not null)
                RefreshVerdict(verdict, card.Model.Info.Version, VersionsFor(libraryVersions, verdict.ModId));

            card.Nexus = verdict;
            if (links.TryGetValue(card.Folder, out var link))
                card.NexusIdLabel = $"Nexus #{link.ModId}" + (link.IsManual ? Localization.S("nx.manualSuffix") : link.Source == "website" ? Localization.S("nx.websiteSuffix") : string.Empty);
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

    private static void RefreshVerdict(
        NexusModUpdate verdict,
        string? currentLocalVersion,
        IReadOnlyCollection<string> libraryVersions)
    {
        // Older builds cached a harsher "set the ID manually" wording. Show the current
        // explanation even before the next check.
        if (verdict.Status == NexusCheckStatus.NotLinked)
        {
            verdict.Message = Localization.S("nx.idNotFoundMessage");
            return;
        }

        if (verdict.Status is NexusCheckStatus.ModNotFound or NexusCheckStatus.ApiError)
            return;

        if (!string.IsNullOrWhiteSpace(currentLocalVersion))
            verdict.LocalVersion = currentLocalVersion;

        if (verdict.RulesVersion >= NexusUpdateRules.Version)
        {
            // A fresh verdict already follows the file-based rules. Only the local version may
            // have changed since the check (the player installed the update).
            if (verdict.Status is not (NexusCheckStatus.NoFiles or NexusCheckStatus.UncomparableVersion) &&
                !string.IsNullOrWhiteSpace(verdict.RemoteVersion) &&
                !string.IsNullOrWhiteSpace(verdict.LocalVersion))
            {
                var relation = ModVersionComparer.Compare(verdict.LocalVersion, verdict.RemoteVersion);
                if (relation is VersionRelation.Equal or VersionRelation.LeftNewer)
                {
                    verdict.Status = NexusCheckStatus.UpToDate;
                    verdict.LibraryVersion = null;
                }
            }

            verdict.Message = NexusUpdateRules.Describe(verdict);
            return;
        }

        // Entries written by an older build kept the page version in RemoteVersion and the file
        // version in LatestFileVersion. They are recalculated on load: the page version is often
        // the game version and must not survive as "update available".
        var page = verdict.RemoteVersion;
        var file = verdict.LatestFileVersion ?? string.Empty;
        var result = NexusUpdateRules.Decide(verdict.LocalVersion, page, file, libraryVersions);

        verdict.PageVersion = page;
        verdict.Status = result.Status;
        verdict.RemoteVersion = result.AvailableVersion;
        verdict.LibraryVersion = result.LibraryVersion;
        verdict.RulesVersion = NexusUpdateRules.Version;
        verdict.Message = NexusUpdateRules.Describe(verdict);
    }

    private static IReadOnlyCollection<string> VersionsFor(
        IReadOnlyDictionary<int, List<string>> map, int modId) =>
        map.TryGetValue(modId, out var versions) ? versions : Array.Empty<string>();

    /// <summary>
    /// Starts the OAuth 2.0 + PKCE sign-in. The browser opens on the Nexus authorization page and
    /// the manager waits for the loopback callback. No API key is requested at any point.
    /// </summary>
    public async Task SignInNexusAsync()
    {
        if (IsSigningInNexus) return;

        IsSigningInNexus = true;
        NexusStatusText = Localization.S("nx.oauthOpeningBrowser");
        try
        {
            var result = await _manager.SignInToNexusAsync(url => _shell.OpenUrl(url));

            if (!result.Success)
            {
                if (result.Cancelled)
                    _toasts.Info(Localization.S("nx.oauthCancelledTitle"), Localization.S("nx.oauthCancelledText"));
                else
                    _toasts.Error(Localization.S("nx.oauthFailedTitle"),
                        result.Error ?? Localization.S("nx.unknownError"));
                return;
            }

            // Confirm the account and record its display name for the settings card.
            var validation = await _manager.ValidateNexusAccountAsync();
            RefreshNexusAccountState();

            if (validation.Success && validation.Data is not null)
            {
                NexusUserText = Localization.SF("nx.userLine", validation.Data.Name,
                    validation.Data.IsPremium ? Localization.S("nx.yes") : Localization.S("nx.no"));
                NexusQuotaText = validation.RateLimit.Summary;
                _toasts.Success(Localization.S("nx.oauthSuccessTitle"),
                    Localization.SF("nx.oauthSuccessText", validation.Data.Name));
            }
            else
            {
                _toasts.Success(Localization.S("nx.oauthSuccessTitle"), Localization.S("nx.signedIn"));
            }
        }
        catch (Exception e)
        {
            _toasts.Error(Localization.S("nx.oauthFailedTitle"), e.Message);
        }
        finally
        {
            IsSigningInNexus = false;
            NexusStatusText = string.Empty;
        }
    }

    /// <summary>
    /// Saves the OAuth client id issued by Nexus so sign-in works without rebuilding the app.
    /// The id is public; it only identifies the application to the authorization server.
    /// </summary>
    public void SaveNexusClientId()
    {
        var id = NexusClientIdInput?.Trim() ?? string.Empty;
        _manager.SetNexusOAuthClientId(id);
        Settings.NexusOAuthClientId = id;
        RefreshNexusAccountState();

        _toasts.Success(Localization.S("nx.clientIdSavedTitle"),
            string.IsNullOrWhiteSpace(id)
                ? Localization.S("nx.clientIdClearedText")
                : Localization.S("nx.clientIdSavedText"));
    }

    /// <summary>Clears stored tokens. Nexus links stay usable, only account-bound checks stop.</summary>
    public void SignOutNexus()
    {
        _manager.SignOutOfNexus();
        RefreshNexusAccountState();
        NexusUserText = Localization.S("nx.keyNotChecked");
        NexusQuotaText = Localization.S("nx.quotaUnknown");
        _toasts.Info(Localization.S("nx.signedOutTitle"), Localization.S("nx.signedOutText"));
    }

    public async Task CheckNexusUpdatesAsync()
    {
        if (IsCheckingNexus) return;
        if (!_manager.HasNexusAccount)
        {
            _toasts.Warning(Localization.S("nx.signInNeededTitle"), Localization.S("nx.signInNeededHint"));
            return;
        }

        IsCheckingNexus = true;
        NexusStatusText = Localization.S("nx.connecting");
        try
        {
            var progress = new Progress<string>(text => NexusStatusText = text);
            var report = await _manager.CheckNexusUpdatesAsync(progress);
            NexusQuotaText = report.RateLimit.Summary;
            RefreshNexusAccountState();

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
