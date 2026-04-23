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
    ILogger<AutoDownloadService> log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ErrorCooldown    = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoMatchCooldown  = TimeSpan.FromHours(6);

    // Per-movie cooldown: don't re-try the same title on every tick.
    private readonly ConcurrentDictionary<string, DateTime> _cooldownUntil = new();
    // Tracks the last movie we kicked off so we can record its outcome on the next tick.
    private string? _lastStartedMovieId;

    // ── Diagnostic state (exposed via GET /api/auto-download/debug) ──────────
    private DateTime? _lastTickAt;
    private string    _lastTickResult = "never run";
    private int       _ticksCompleted;
    private int       _downloadsStarted;

    public object GetDiagnostics()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();
        return new
        {
            enabled            = db.GetSetting("auto_download", "false") == "true",
            setupComplete      = db.IsSetupComplete(),
            rapidApiConfigured = !string.IsNullOrEmpty(db.GetSetting("rapidapi_key", "")),
            downloadInProgress = download.IsAnyInProgress(),
            lastStartedMovieId = _lastStartedMovieId,
            lastTickAt         = _lastTickAt,
            lastTickResult     = _lastTickResult,
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
                _lastTickResult = $"exception: {ex.Message}";
                log.LogWarning(ex, "AutoDownload tick failed");
            }
            finally
            {
                _ticksCompleted++;
                _lastTickAt = DateTime.UtcNow;
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
            _lastTickResult = "skipped: auto_download is off";
            return;
        }
        if (!db.IsSetupComplete())
        {
            _lastTickResult = "skipped: setup not complete";
            return;
        }

        // One download at a time — respect whatever the user or the queue page already started.
        if (download.IsAnyInProgress())
        {
            _lastTickResult = "skipped: a download is in progress";
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
            _lastTickResult = pending.Count == 0
                ? "skipped: no pending movies"
                : $"skipped: all {pending.Count} pending movies are in cooldown";
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
            log.LogWarning(ex, "AutoDownload: YouTube search failed for {Title}", title);
            _cooldownUntil[movieId] = DateTime.UtcNow + ErrorCooldown;
            _lastTickResult = $"search failed for '{title}': {ex.Message}";
            return;
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
        {
            log.LogInformation("AutoDownload: no confident match for '{Title}' — backing off {Hrs}h",
                title, NoMatchCooldown.TotalHours);
            _cooldownUntil[movieId] = DateTime.UtcNow + NoMatchCooldown;
            _lastTickResult = $"no confident match for '{title}'; cooldown {NoMatchCooldown.TotalHours}h";
            return;
        }

        var videoId = best["videoId"]?.ToString() ?? "";
        var url = $"https://www.youtube.com/watch?v={videoId}";

        log.LogInformation("AutoDownload: starting '{Title}' ({Year}) → {VideoId}", title, year, videoId);
        if (!download.Start(movieId, url))
        {
            // Raced with another starter — try again next tick.
            _cooldownUntil[movieId] = DateTime.UtcNow + ErrorCooldown;
            _lastTickResult = $"race: Start() returned false for '{title}'";
            return;
        }

        _lastStartedMovieId = movieId;
        _downloadsStarted++;
        _lastTickResult = $"started '{title}' → {videoId}";
    }

    private void ExpireCooldowns()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _cooldownUntil)
            if (kv.Value < now) _cooldownUntil.TryRemove(kv.Key, out _);
    }
}
