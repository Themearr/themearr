using System.Collections.Concurrent;
using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Background service that walks the pending SHOW queue and downloads best-match themes
/// automatically when auto-download is enabled — the show-side parallel of
/// <see cref="AutoDownloadService"/>.
///
/// This is a deliberate duplicate rather than a shared generic worker: the two differ in
/// their pending source, their search query (a show isn't per-year, so no year and a
/// "theme song" suffix) and their status setter, and the security-critical download core
/// they both call is already shared via <see cref="DownloadService"/>. Both gate on the
/// same <see cref="DownloadService.IsAnyInProgress"/>, so movie and show downloads
/// naturally serialize against one another.
/// </summary>
public class ShowAutoDownloadService(
    IServiceProvider services,
    DownloadService  download,
    IThemeAudioProvider provider,
    ILogger<ShowAutoDownloadService> log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval   = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ErrorCooldown   = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoMatchCooldown = TimeSpan.FromHours(6);

    // Per-show cooldown: don't re-try the same title on every tick.
    private readonly ConcurrentDictionary<string, DateTime> _cooldownUntil = new();
    // Tracks the last show we kicked off so we can record its outcome on the next tick.
    private string? _lastStartedShowId;
    // Whether that show was a re-check pickup rather than an ordinary pending one — the
    // outcome block needs this to know which signal (recheck_state vs status) to judge it
    // by. See RollLastOutcome; mirrors AutoDownloadService's movie-side field exactly.
    private bool _lastStartedWasRecheck;

    private sealed record TickState(DateTime? At, string Result);

    private TickState _tick = new(null, "never run");

    private TickState Tick
    {
        get => Volatile.Read(ref _tick);
        set => Volatile.Write(ref _tick, value);
    }

    private int _ticksCompleted;
    private int _downloadsStarted;
    private long _startedAtTicks;

    public DateTime? StartedAt
    {
        get { var t = Volatile.Read(ref _startedAtTicks); return t == 0 ? null : new DateTime(t, DateTimeKind.Utc); }
    }
    public DateTime? LastTickAt     => Tick.At;
    public string    LastTickResult => Tick.Result;

    /// <summary>
    /// The YouTube query for a show theme. Deliberately carries NO year: a show spans
    /// years, so "The Wire 2002 theme" biases the search towards one season's upload
    /// rather than the series theme. "theme song" is the phrasing show themes are
    /// actually published under.
    /// </summary>
    public static string BuildQuery(string title) => $"{title.Trim()} theme song";

    public object GetDiagnostics()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();
        return new
        {
            enabled            = db.GetSetting("auto_download", "false") == "true",
            setupComplete      = db.IsSetupComplete(),
            rapidApiConfigured = provider.CheckConfiguration() == null,
            quotaCoolingDown   = download.IsQuotaCoolingDown(out var quotaUntil),
            quotaCooldownUntil = quotaUntil == DateTime.MinValue ? (DateTime?)null : quotaUntil,
            downloadInProgress = download.IsAnyInProgress(),
            lastStartedShowId  = _lastStartedShowId,
            lastTickAt         = Tick.At,
            lastTickResult     = Tick.Result,
            ticksCompleted     = _ticksCompleted,
            downloadsStarted   = _downloadsStarted,
            pendingCount       = db.GetAllShows().Count(s => (s["status"]?.ToString() ?? "") == "pending"),
            recheckQueuedCount = db.GetRecheckQueued("show").Count,
            cooldowns          = _cooldownUntil
                                   .OrderBy(kv => kv.Value)
                                   .ToDictionary(kv => kv.Key, kv => kv.Value),
            checkIntervalSec   = (int)CheckInterval.TotalSeconds,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("ShowAutoDownloadService started — first tick in 60s, then every {Sec}s",
            (int)CheckInterval.TotalSeconds);
        Volatile.Write(ref _startedAtTicks, DateTime.UtcNow.Ticks);

        // Warm-up delay so DB init and the first show sync can land first. Slightly
        // longer than the movie worker's so the two don't wake on the same second.
        await Task.Delay(TimeSpan.FromSeconds(60), ct);

        while (!ct.IsCancellationRequested)
        {
            try { Tick = Tick with { Result = await TryDownloadOnceAsync(ct) }; }
            catch (Exception ex)
            {
                Tick = Tick with { Result = "last tick failed — see the application log" };
                log.LogWarning(ex, "ShowAutoDownload tick failed");
            }
            finally
            {
                _ticksCompleted++;
                Tick = Tick with { At = DateTime.UtcNow };
            }

            await Task.Delay(CheckInterval, ct);
        }
    }

    /// <summary>
    /// One pass of the queue. Returns the tick-result string rather than storing it, so
    /// the guard conditions are testable without running the timer loop.
    /// </summary>
    public async Task<string> TryDownloadOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();
        var yt = scope.ServiceProvider.GetRequiredService<YoutubeService>();

        if (db.GetSetting("auto_download", "false") != "true")
            return "skipped: auto_download is off";

        if (!db.IsSetupComplete())
            return "skipped: setup not complete";

        // Don't churn through pending shows when the provider can't download yet —
        // surface the actionable reason instead of failing one show per tick.
        if (provider.CheckConfiguration() is { } notReady)
            return $"skipped: {notReady}";

        // Circuit-breaker: after a quota 429 the provider sets a cooldown.
        if (download.IsQuotaCoolingDown(out var quotaUntil))
            return $"skipped: RapidAPI quota cooldown until {quotaUntil:o}";

        // One download at a time, across BOTH workers — IsAnyInProgress is global.
        if (download.IsAnyInProgress())
            return "skipped: a download is in progress";

        RollLastOutcome(db);

        ExpireCooldowns();

        var pending = GetVerifiedPending(db);
        var (candidate, isRecheck, recheckQueued) = SelectCandidate(db, pending);

        if (candidate == null)
            return pending.Count == 0 && recheckQueued.Count == 0
                ? "skipped: no pending shows"
                : $"skipped: {pending.Count} pending shows and {recheckQueued.Count} queued re-checks are in cooldown";

        var showId = candidate["id"]?.ToString() ?? "";
        var title  = candidate["title"]?.ToString() ?? "";
        var query  = BuildQuery(title);

        List<Dictionary<string, object?>> results;
        try
        {
            results = await yt.SearchAsync(query, maxResults: 8, title: title);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ShowAutoDownload: YouTube search failed for {Title}", LogSanitizer.Clean(title));
            _cooldownUntil[showId] = DateTime.UtcNow + ErrorCooldown;
            return $"search failed for '{title}' — see the application log";
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
            return HandleNoMatch(db, showId, title, isRecheck);

        var videoId = best["videoId"]?.ToString() ?? "";
        var url = $"https://www.youtube.com/watch?v={videoId}";

        log.LogInformation("ShowAutoDownload: starting '{Title}' → {VideoId}",
            LogSanitizer.Clean(title), LogSanitizer.Clean(videoId));
        // The matcher picked this candidate, not a person — "auto" — carrying its score
        // along so the re-check can weigh a later match against the one that shipped.
        if (!download.Start(showId, url, "show", "auto", best["score"] as int?))
        {
            // Raced with another starter — try again next tick.
            _cooldownUntil[showId] = DateTime.UtcNow + ErrorCooldown;
            return $"race: Start() returned false for '{title}'";
        }

        _lastStartedShowId = showId;
        _lastStartedWasRecheck = isRecheck;
        _downloadsStarted++;
        return $"started '{title}' → {videoId}";
    }

    /// <summary>
    /// Rolls the previously-started show into the cooldown map based on whether it
    /// actually worked, then clears the tracking fields for the next tick. Mirrors
    /// AutoDownloadService.RollLastOutcome exactly — see its doc comment for THE HAZARD
    /// this exists to avoid: a re-check show is already 'downloaded' before the worker
    /// touches it, so `status` alone cannot tell a failed replacement from a successful
    /// one. `recheck_state` can: DownloadService clears it to null only on success
    /// (DownloadService.cs:227, the AddThemeHistory success block), so a show still
    /// present in GetRecheckQueued after the tick means the replacement did not land.
    /// </summary>
    private void RollLastOutcome(Database db)
    {
        if (_lastStartedShowId == null) return;

        var failed = _lastStartedWasRecheck
            ? db.GetRecheckQueued("show").Any(s => s["id"]?.ToString() == _lastStartedShowId)
            : db.GetShow(_lastStartedShowId)?["status"]?.ToString() != "downloaded";

        if (failed)
            _cooldownUntil[_lastStartedShowId] = DateTime.UtcNow + ErrorCooldown;

        _lastStartedShowId = null;
        _lastStartedWasRecheck = false;
    }

    /// <summary>
    /// Cheap pre-filter on the stored status column, disk-verified. A stored-pending show
    /// whose theme appeared out-of-band is reconciled to 'downloaded' so it leaves the
    /// cheap set on the next tick — the show-side twin of AutoDownloadService's
    /// GetVerifiedPending.
    /// </summary>
    private List<Dictionary<string, object?>> GetVerifiedPending(Database db)
    {
        var storedPending = db.GetPendingShows();
        var pending = new List<Dictionary<string, object?>>();
        foreach (var s in storedPending)
        {
            var folder = s["folderName"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            if (ThemeFiles.HasUsableThemeInExistingFolder(folder))
            {
                db.SetShowStatus(s["id"]?.ToString() ?? "", "downloaded");
                continue;
            }
            pending.Add(s);
        }
        return pending;
    }

    /// <summary>
    /// Picks the next candidate. Pending shows (no theme at all) always win — a missing
    /// theme is a worse gap than a wrong one — so the re-check queue is only consulted
    /// once the pending selection comes up empty, whether because the queue is genuinely
    /// dry or because every pending show is presently in cooldown. Mirrors
    /// AutoDownloadService.SelectCandidate exactly.
    /// </summary>
    private (Dictionary<string, object?>? candidate, bool isRecheck, List<Dictionary<string, object?>> recheckQueued)
        SelectCandidate(Database db, List<Dictionary<string, object?>> pending)
    {
        var pendingCandidate = pending.FirstOrDefault(s => !_cooldownUntil.ContainsKey(s["id"]?.ToString() ?? ""));
        if (pendingCandidate != null)
            return (pendingCandidate, false, []);

        var recheckQueued = db.GetRecheckQueued("show");
        var recheckCandidate = recheckQueued.FirstOrDefault(s => !_cooldownUntil.ContainsKey(s["id"]?.ToString() ?? ""));
        return (recheckCandidate, recheckCandidate != null, recheckQueued);
    }

    /// <summary>
    /// What happens when a search comes back with no confident match. A pending show
    /// still has no theme either way, so cooling down and retrying next tick is unchanged.
    /// A re-check show already has a theme, so a repeated search rarely turning up
    /// something new means retrying on NoMatchCooldown would burn RapidAPI quota forever
    /// chasing an answer that will not change — it goes straight to 'review' instead, via
    /// the compare-and-set TrySetRecheck (never the unconditional SetRecheck). See
    /// Database.cs:1071-1082 for the race TrySetRecheck closes: a "Keep this" click
    /// landing mid-search must never be overwritten by this write. A false return means
    /// someone else already resolved the row — nothing to log, just move on.
    /// </summary>
    private string HandleNoMatch(Database db, string id, string title, bool isRecheck)
    {
        if (isRecheck)
        {
            var moved = db.TrySetRecheck(id, "show", "review", "no better match found", expectedState: "queued");
            return moved
                ? $"no better match found for '{title}' — moved to review"
                : $"no better match found for '{title}' — row was already resolved, left alone";
        }

        log.LogInformation("ShowAutoDownload: no confident match for '{Title}' — backing off {Hrs}h",
            LogSanitizer.Clean(title), NoMatchCooldown.TotalHours);
        _cooldownUntil[id] = DateTime.UtcNow + NoMatchCooldown;
        return $"no confident match for '{title}'; cooldown {NoMatchCooldown.TotalHours}h";
    }

    private void ExpireCooldowns()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _cooldownUntil)
            if (kv.Value < now) _cooldownUntil.TryRemove(kv.Key, out _);
    }

    // ── Test seams ─────────────────────────────────────────────────────────────
    // Same rationale as AutoDownloadService's: the candidate/outcome/no-match logic is
    // private, and YoutubeService has no interface so a live search can't be faked from a
    // test. Internal: visible only to Themearr.API.Tests via that file's
    // InternalsVisibleTo (one assembly attribute suffices for the whole assembly).

    internal void NoteStartedForTest(string id, bool wasRecheck)
    {
        _lastStartedShowId = id;
        _lastStartedWasRecheck = wasRecheck;
    }

    internal void RollLastOutcomeForTest()
    {
        using var scope = services.CreateScope();
        RollLastOutcome(scope.ServiceProvider.GetRequiredService<Database>());
    }

    internal bool IsCoolingDownForTest(string id) => _cooldownUntil.ContainsKey(id);

    internal (Dictionary<string, object?>? candidate, bool isRecheck) SelectCandidateForTest()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();
        var (candidate, isRecheck, _) = SelectCandidate(db, GetVerifiedPending(db));
        return (candidate, isRecheck);
    }

    internal string HandleNoMatchForTest(string id, string title, bool isRecheck)
    {
        using var scope = services.CreateScope();
        return HandleNoMatch(scope.ServiceProvider.GetRequiredService<Database>(), id, title, isRecheck);
    }
}
