using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Walks every already-downloaded theme and judges it against today's matching rules
/// (<see cref="ThemeMatch.RejectionReason"/>). Deliberately zero network calls: judging
/// what's already on disk is free, and only finding a replacement (a later task) costs
/// API quota.
///
/// Mirrors <see cref="SyncService"/>'s concurrency shape exactly (SyncService.cs:7-48) —
/// this codebase has been bitten by ad-hoc concurrency before, so a second background
/// worker gets the same settled answer rather than a new one: a <see cref="SemaphoreSlim"/>
/// guarding one scan at a time, volatile counters a concurrent <see cref="GetStatus"/> can
/// read without tearing, and a <see cref="Current"/> handle so a caller can await the
/// outcome instead of only learning that a scan was launched.
/// </summary>
public class ThemeRecheckService(Database db, ILogger<ThemeRecheckService> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _inProgress;
    private volatile bool _finished;
    private volatile int  _scanned;
    private volatile int  _flagged;
    private volatile string _error = "";

    // Handle on the running scan so a caller can await the outcome instead of only
    // learning that one was launched. Completed by default, so awaiting before the
    // first scan returns immediately rather than hanging (SyncService.cs:19).
    private volatile Task _current = Task.CompletedTask;

    public bool InProgress => _inProgress;
    public Task Current    => _current;

    public async Task<bool> StartAsync()
    {
        if (_inProgress) return false;
        if (!await _lock.WaitAsync(0)) return false;

        _inProgress = true;
        _finished   = false;
        _error      = "";
        _scanned    = 0;
        _flagged    = 0;

        _current = Task.Run(RunAsync).ContinueWith(_ => _lock.Release());
        return true;
    }

    public object GetStatus() => new
    {
        inProgress = _inProgress,
        finished   = _finished,
        scanned    = _scanned,
        flagged    = _flagged,
        error      = _error,
    };

    // No I/O in the scan itself (see class summary), so unlike SyncService.RunAsync
    // there is nothing to await — Task.Run's Action overload runs this on a threadpool
    // thread without an async state machine.
    private void RunAsync()
    {
        try
        {
            ScanMediaType("movie");
            ScanMediaType("show");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            log.LogError(ex, "Theme re-check scan failed");
        }
        finally
        {
            _finished   = true;
            _inProgress = false;
        }
    }

    private void ScanMediaType(string mediaType)
    {
        foreach (var item in db.GetRecheckCandidates(mediaType))
        {
            var id     = item["id"]?.ToString() ?? "";
            var folder = item["folderName"]?.ToString() ?? "";

            // 'kept' is the user's answer to a specific verdict, and it is permanent —
            // skip it even though the title still fails today's rules. The way back is a
            // fresh download: DeleteTheme resets status away from 'downloaded'
            // (MoviesController.cs:84), which drops the row out of GetRecheckCandidates
            // (Database.cs:959, "WHERE status = 'downloaded'") regardless of
            // recheck_state, and a later successful download clears the flag itself
            // (DownloadService.cs:227).
            if (item["recheckState"]?.ToString() == "kept") continue;

            // Judge only what is actually there. A row can say 'downloaded' while the
            // file was removed on disk out-of-band.
            if (string.IsNullOrEmpty(folder) || !ThemeFiles.HasUsableThemeInExistingFolder(folder)) continue;

            var prov = db.GetLatestThemeProvenance(id, mediaType);

            // No recorded title means no evidence to judge — a direct-URL download never
            // gets one (DownloadService.cs:156 declares themeTitle null; the non-provider
            // branch at :189-207 never assigns it). Flagging these would be noise about
            // something that cannot be assessed.
            if (prov?.ThemeTitle is not { } themeTitle) continue;

            _scanned++;

            var reason = ThemeMatch.RejectionReason(
                themeTitle, item["title"]?.ToString(), item["year"] as int?, prov.MatchScore);
            if (reason == null) continue;

            // A hand-picked theme is never replaced without asking: manual picks fail the
            // rules more often than they should, because a real track's title often
            // carries no music word at all ("Blade Runner 2049 - Mesa").
            var state = ResolveProvenance(prov.PickedBy, prov.SourceUrl) == "manual" ? "review" : "queued";
            db.SetRecheck(id, mediaType, state, reason);
            _flagged++;

            log.LogInformation("Re-check flagged {MediaType} '{Title}' as {State}: {Reason}",
                mediaType, LogSanitizer.Clean(item["title"]?.ToString() ?? ""), state, reason);
        }
    }

    /// <summary>
    /// How a theme was chosen. An explicit <c>picked_by</c> always wins; rows written
    /// before this feature have none, so provenance falls back to the one signal the old
    /// data actually carries — a non-provider URL can only have come from
    /// POST /api/download-url (MoviesController.cs:187), which a human typed.
    ///
    /// The residual gap is known and accepted: a legacy theme hand-picked from the search
    /// list is indistinguishable from an auto-picked one. Treating every legacy row as
    /// manual instead would make the first re-check — the one that clears the backlog this
    /// feature exists for — entirely manual.
    /// </summary>
    public static string ResolveProvenance(string? pickedBy, string? sourceUrl) =>
        pickedBy ?? (string.IsNullOrEmpty(sourceUrl) || DownloadService.IsProviderUrl(sourceUrl)
            ? "auto" : "manual");
}
