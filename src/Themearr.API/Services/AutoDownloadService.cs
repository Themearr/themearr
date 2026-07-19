using System.Collections.Concurrent;
using Themearr.API.Data;

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

    // Exposed for DownloadWorkerCheck: "is the worker alive, and what did it last do".
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

        // Roll the last-started movie into the cooldown map based on its final state.
        if (_lastStartedMovieId != null)
        {
            var final = db.GetMovie(_lastStartedMovieId);
            var status = final?["status"]?.ToString();
            if (status != "downloaded")
                _cooldownUntil[_lastStartedMovieId] = DateTime.UtcNow + ErrorCooldown;
            _lastStartedMovieId = null;
        }

        ExpireCooldowns();

        var movies = db.GetAllMovies();
        var pending = movies.Where(m => (m["status"]?.ToString() ?? "") == "pending").ToList();
        var candidate = pending.FirstOrDefault(m =>
            !_cooldownUntil.ContainsKey(m["id"]?.ToString() ?? ""));

        if (candidate == null)
        {
            Tick = Tick with
            {
                Result = pending.Count == 0
                    ? "skipped: no pending movies"
                    : $"skipped: all {pending.Count} pending movies are in cooldown",
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
            results = await yt.SearchAsync(query, maxResults: 8, movieTitle: title, movieYear: year);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "AutoDownload: YouTube search failed for {Title}", LogSanitizer.Clean(title));
            _cooldownUntil[movieId] = DateTime.UtcNow + ErrorCooldown;
            Tick = Tick with { Result = $"search failed for '{title}': {ex.Message}" };
            return;
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
        {
            log.LogInformation("AutoDownload: no confident match for '{Title}' — backing off {Hrs}h",
                LogSanitizer.Clean(title), NoMatchCooldown.TotalHours);
            _cooldownUntil[movieId] = DateTime.UtcNow + NoMatchCooldown;
            Tick = Tick with { Result = $"no confident match for '{title}'; cooldown {NoMatchCooldown.TotalHours}h" };
            return;
        }

        var videoId = best["videoId"]?.ToString() ?? "";
        var url = $"https://www.youtube.com/watch?v={videoId}";

        log.LogInformation("AutoDownload: starting '{Title}' ({Year}) → {VideoId}", LogSanitizer.Clean(title), year, LogSanitizer.Clean(videoId));
        if (!download.Start(movieId, url))
        {
            // Raced with another starter — try again next tick.
            _cooldownUntil[movieId] = DateTime.UtcNow + ErrorCooldown;
            Tick = Tick with { Result = $"race: Start() returned false for '{title}'" };
            return;
        }

        _lastStartedMovieId = movieId;
        _downloadsStarted++;
        Tick = Tick with { Result = $"started '{title}' → {videoId}" };
    }

    private void ExpireCooldowns()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _cooldownUntil)
            if (kv.Value < now) _cooldownUntil.TryRemove(kv.Key, out _);
    }
}
