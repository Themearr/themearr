using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Themearr.API.Data;

// The re-check hazard (see RollLastOutcome's doc comment) can only be pinned by driving
// the tick's private candidate/outcome/no-match logic directly — YoutubeService has no
// interface, so nothing past the search call can be faked from outside the assembly.
[assembly: InternalsVisibleTo("Themearr.API.Tests")]

namespace Themearr.API.Services;

/// <summary>
/// Background service that walks the pending queue and downloads best-match themes
/// automatically when auto-download is enabled. This is what makes "set and forget"
/// work — the queue no longer needs the browser to be open.
/// </summary>
public class AutoDownloadService(
    IServiceProvider services,
    DownloadService  download,
    IThemeAudioProvider provider,
    ILogger<AutoDownloadService> log) : BackgroundService, Health.IDownloadWorkerStatus
{
    private static readonly TimeSpan CheckInterval    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ErrorCooldown    = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoMatchCooldown  = TimeSpan.FromHours(6);

    // Per-movie cooldown: don't re-try the same title on every tick.
    private readonly ConcurrentDictionary<string, DateTime> _cooldownUntil = new();
    // Tracks the last movie we kicked off so we can record its outcome on the next tick.
    private string? _lastStartedMovieId;
    // Whether that movie was a re-check pickup rather than an ordinary pending one — the
    // outcome block needs this to know which signal (recheck_state vs status) to judge it
    // by. See RollLastOutcome.
    private bool _lastStartedWasRecheck;

    // ── Diagnostic state (exposed via GET /api/auto-download/debug) ──────────
    // Published as one immutable value so a reader on another thread always sees a
    // coherent timestamp/result pair — DownloadWorkerCheck renders them together, and
    // a torn read would describe the wrong tick. Same pattern as TaskRegistry.
    private sealed record TickState(DateTime? At, string Result);

    private TickState _tick = new(null, "never run");

    private TickState Tick
    {
        get => Volatile.Read(ref _tick);
        set => Volatile.Write(ref _tick, value);
    }

    private int _ticksCompleted;
    private int _downloadsStarted;

    // Set once when ExecuteAsync begins (before the warm-up delay). Stored as ticks
    // so publication is a single volatile write, readable without a torn DateTime.
    // 0 means the loop has not started yet.
    private long _startedAtTicks;

    // Exposed for DownloadWorkerCheck: "is the worker alive, and what did it last do".
    public DateTime? StartedAt
    {
        get { var t = Volatile.Read(ref _startedAtTicks); return t == 0 ? null : new DateTime(t, DateTimeKind.Utc); }
    }
    public DateTime? LastTickAt     => Tick.At;
    public string    LastTickResult => Tick.Result;

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
            lastStartedMovieId = _lastStartedMovieId,
            lastTickAt         = Tick.At,
            lastTickResult     = Tick.Result,
            ticksCompleted     = _ticksCompleted,
            downloadsStarted   = _downloadsStarted,
            pendingCount       = db.GetAllMovies().Count(m => (m["status"]?.ToString() ?? "") == "pending"),
            recheckQueuedCount = db.GetRecheckQueued("movie").Count,
            cooldowns          = _cooldownUntil
                                   .OrderBy(kv => kv.Value)
                                   .ToDictionary(kv => kv.Key, kv => kv.Value),
            checkIntervalSec   = (int)CheckInterval.TotalSeconds,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("AutoDownloadService started — first tick in 45s, then every {Sec}s",
            (int)CheckInterval.TotalSeconds);
        Volatile.Write(ref _startedAtTicks, DateTime.UtcNow.Ticks);

        // Warm-up delay so DB init + Plex sync can land first
        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await TryAutoDownloadOne(); }
            catch (Exception ex)
            {
                Tick = Tick with { Result = "last tick failed — see the application log" };
                log.LogWarning(ex, "AutoDownload tick failed");
            }
            finally
            {
                _ticksCompleted++;
                Tick = Tick with { At = DateTime.UtcNow };
            }

            await Task.Delay(CheckInterval, ct);
        }
    }

    private async Task TryAutoDownloadOne()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();
        var yt = scope.ServiceProvider.GetRequiredService<YoutubeService>();

        if (db.GetSetting("auto_download", "false") != "true")
        {
            Tick = Tick with { Result = "skipped: auto_download is off" };
            return;
        }
        if (!db.IsSetupComplete())
        {
            Tick = Tick with { Result = "skipped: setup not complete" };
            return;
        }

        // Don't churn through pending movies when the provider can't download yet —
        // surface the actionable reason instead of failing one movie per tick.
        if (provider.CheckConfiguration() is { } notReady)
        {
            Tick = Tick with { Result = $"skipped: {notReady}" };
            return;
        }

        // Circuit-breaker: after a quota 429 the provider sets a cooldown. Stop
        // hammering the API until it clears.
        if (download.IsQuotaCoolingDown(out var quotaUntil))
        {
            Tick = Tick with { Result = $"skipped: RapidAPI quota cooldown until {quotaUntil:o}" };
            return;
        }

        // One download at a time — respect whatever the user or the queue page already started.
        if (download.IsAnyInProgress())
        {
            Tick = Tick with { Result = "skipped: a download is in progress" };
            return;
        }

        RollLastOutcome(db);

        ExpireCooldowns();

        var pending = GetVerifiedPending(db);
        var (candidate, isRecheck, recheckQueued) = SelectCandidate(db, pending);

        if (candidate == null)
        {
            Tick = Tick with
            {
                Result = pending.Count == 0 && recheckQueued.Count == 0
                    ? "skipped: no pending movies"
                    : $"skipped: {pending.Count} pending movies and {recheckQueued.Count} queued re-checks are in cooldown",
            };
            return;
        }

        var movieId = candidate["id"]?.ToString() ?? "";
        var title   = candidate["title"]?.ToString() ?? "";
        var year    = candidate["year"] is int y ? y : (int?)null;
        var query   = $"{title} {year} theme".Trim();

        List<Dictionary<string, object?>> results;
        try
        {
            results = await yt.SearchAsync(query, maxResults: 8, title: title, year: year);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "AutoDownload: YouTube search failed for {Title}", LogSanitizer.Clean(title));
            _cooldownUntil[movieId] = DateTime.UtcNow + ErrorCooldown;
            Tick = Tick with { Result = $"search failed for '{title}' — see the application log" };
            return;
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
        {
            HandleNoMatch(db, movieId, title, isRecheck);
            return;
        }

        var videoId = best["videoId"]?.ToString() ?? "";
        var url = $"https://www.youtube.com/watch?v={videoId}";

        log.LogInformation("AutoDownload: starting '{Title}' ({Year}) → {VideoId}", LogSanitizer.Clean(title), year, LogSanitizer.Clean(videoId));
        // The matcher picked this candidate, not a person — "auto" — carrying its score
        // along so the re-check can weigh a later match against the one that shipped.
        if (!download.Start(movieId, url, "movie", "auto", best["score"] as int?))
        {
            // Raced with another starter — try again next tick.
            _cooldownUntil[movieId] = DateTime.UtcNow + ErrorCooldown;
            Tick = Tick with { Result = $"race: Start() returned false for '{title}'" };
            return;
        }

        _lastStartedMovieId = movieId;
        _lastStartedWasRecheck = isRecheck;
        _downloadsStarted++;
        Tick = Tick with { Result = $"started '{title}' → {videoId}" };
    }

    /// <summary>
    /// Rolls the previously-started movie into the cooldown map based on whether it
    /// actually worked, then clears the tracking fields for the next tick.
    ///
    /// THE HAZARD: an ordinary pending movie is correctly judged by `status` —
    /// "downloaded" means the file landed. A re-check movie is a different shape: it was
    /// ALREADY 'downloaded' before this worker touched it (it has a theme, just a bad
    /// one), so `status` reads "downloaded" whether or not the replacement search found
    /// anything. Judging a re-check item by `status` would apply no cooldown, leave the
    /// row 'queued', and retry it every 30s forever, burning API quota indefinitely. The
    /// signal that actually moves is `recheck_state`: DownloadService clears it to null on
    /// a successful download (DownloadService.cs:227, the AddThemeHistory success block)
    /// and never touches it on failure or timeout, so a row still present in
    /// GetRecheckQueued after the tick means the replacement did not land.
    /// </summary>
    private void RollLastOutcome(Database db)
    {
        if (_lastStartedMovieId == null) return;

        var failed = _lastStartedWasRecheck
            ? db.GetRecheckQueued("movie").Any(m => m["id"]?.ToString() == _lastStartedMovieId)
            : db.GetMovie(_lastStartedMovieId)?["status"]?.ToString() != "downloaded";

        if (failed)
            _cooldownUntil[_lastStartedMovieId] = DateTime.UtcNow + ErrorCooldown;

        _lastStartedMovieId = null;
        _lastStartedWasRecheck = false;
    }

    /// <summary>
    /// Cheap pre-filter on the stored status column, disk-verified. On a fully-downloaded,
    /// idle library the pre-filter is a single indexed query that returns nothing, instead
    /// of stat-ing every movie folder on every 30-second tick. A stored-pending movie whose
    /// theme appeared out-of-band is reconciled to 'downloaded' so it leaves the cheap set
    /// on the next tick. (An in-app delete resets stored status to 'pending' — see
    /// MoviesController — so it re-enters here; only a theme deleted directly on disk for a
    /// 'downloaded' movie is not auto-re-fetched, and the movies page still shows it as
    /// pending.)
    /// </summary>
    private List<Dictionary<string, object?>> GetVerifiedPending(Database db)
    {
        var storedPending = db.GetPendingMovies();
        var pending = new List<Dictionary<string, object?>>();
        foreach (var m in storedPending)
        {
            var folder = m["folderName"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            if (ThemeFiles.HasUsableThemeInExistingFolder(folder))
            {
                db.SetMovieStatus(m["id"]?.ToString() ?? "", "downloaded");
                continue;
            }
            pending.Add(m);
        }
        return pending;
    }

    /// <summary>
    /// Picks the next candidate. Pending movies (no theme at all) always win — a missing
    /// theme is a worse gap than a wrong one — so the re-check queue is only consulted once
    /// the pending selection comes up empty, whether because the queue is genuinely dry or
    /// because every pending movie is presently in cooldown; either way there is nothing
    /// else for this tick to do with the pending queue, and idling instead of working a
    /// ready re-check item would waste the tick. Returns the (possibly empty) re-check
    /// queue too so the caller can report a precise "skipped" reason without a second query.
    /// </summary>
    private (Dictionary<string, object?>? candidate, bool isRecheck, List<Dictionary<string, object?>> recheckQueued)
        SelectCandidate(Database db, List<Dictionary<string, object?>> pending)
    {
        var pendingCandidate = pending.FirstOrDefault(m => !_cooldownUntil.ContainsKey(m["id"]?.ToString() ?? ""));
        if (pendingCandidate != null)
            return (pendingCandidate, false, []);

        var recheckQueued = db.GetRecheckQueued("movie");
        var recheckCandidate = recheckQueued.FirstOrDefault(m => !_cooldownUntil.ContainsKey(m["id"]?.ToString() ?? ""));
        return (recheckCandidate, recheckCandidate != null, recheckQueued);
    }

    /// <summary>
    /// What happens when a search comes back with no confident match. A pending movie
    /// still has no theme either way, so cooling down and retrying next tick is unchanged.
    /// A re-check movie already has a theme — there is no gap to fill, only a candidate
    /// replacement that did not pan out — and a repeat search for the same query rarely
    /// turns up something new, so retrying on NoMatchCooldown would burn RapidAPI quota
    /// forever chasing an answer that will not change. It goes straight to 'review'
    /// instead, via the compare-and-set TrySetRecheck (never the unconditional SetRecheck)
    /// — see Database.cs:1071-1082, whose doc comment explains the race this closes: a
    /// "Keep this" click landing mid-search must never be overwritten by this write.
    /// TrySetRecheck returning false means someone else already resolved the row (a keep,
    /// or a replacement that landed and cleared the flag first) — that is not this worker's
    /// problem to log, just to move on from.
    /// </summary>
    private void HandleNoMatch(Database db, string id, string title, bool isRecheck)
    {
        if (isRecheck)
        {
            var moved = db.TrySetRecheck(id, "movie", "review", "no better match found", expectedState: "queued");
            Tick = Tick with
            {
                Result = moved
                    ? $"no better match found for '{title}' — moved to review"
                    : $"no better match found for '{title}' — row was already resolved, left alone",
            };
            return;
        }

        log.LogInformation("AutoDownload: no confident match for '{Title}' — backing off {Hrs}h",
            LogSanitizer.Clean(title), NoMatchCooldown.TotalHours);
        _cooldownUntil[id] = DateTime.UtcNow + NoMatchCooldown;
        Tick = Tick with { Result = $"no confident match for '{title}'; cooldown {NoMatchCooldown.TotalHours}h" };
    }

    private void ExpireCooldowns()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _cooldownUntil)
            if (kv.Value < now) _cooldownUntil.TryRemove(kv.Key, out _);
    }

    // ── Test seams ─────────────────────────────────────────────────────────────
    // TryAutoDownloadOne's candidate/outcome/no-match logic is private with no other
    // entry point, and YoutubeService has no interface so a live search can't be faked
    // from a test. These seams drive each piece directly instead, each opening its own
    // scope exactly as TryAutoDownloadOne does. Internal: visible only to
    // Themearr.API.Tests via the InternalsVisibleTo at the top of this file.

    internal void NoteStartedForTest(string id, bool wasRecheck)
    {
        _lastStartedMovieId = id;
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

    internal void HandleNoMatchForTest(string id, string title, bool isRecheck)
    {
        using var scope = services.CreateScope();
        HandleNoMatch(scope.ServiceProvider.GetRequiredService<Database>(), id, title, isRecheck);
    }
}
