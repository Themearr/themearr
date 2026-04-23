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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Warm-up delay so DB init + Plex sync can land first
        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await TryAutoDownloadOne(); }
            catch (Exception ex) { log.LogWarning(ex, "AutoDownload tick failed"); }

            await Task.Delay(CheckInterval, ct);
        }
    }

    private async Task TryAutoDownloadOne()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();
        var yt = scope.ServiceProvider.GetRequiredService<YoutubeService>();

        if (db.GetSetting("auto_download", "false") != "true") return;
        if (!db.IsSetupComplete()) return;

        // One download at a time — respect whatever the user or the queue page already started.
        if (download.IsAnyInProgress()) return;

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
        var candidate = movies.FirstOrDefault(m =>
            (m["status"]?.ToString() ?? "") == "pending" &&
            !_cooldownUntil.ContainsKey(m["id"]?.ToString() ?? ""));
        if (candidate == null) return;

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
            return;
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
        {
            log.LogInformation("AutoDownload: no confident match for '{Title}' — backing off {Hrs}h",
                title, NoMatchCooldown.TotalHours);
            _cooldownUntil[movieId] = DateTime.UtcNow + NoMatchCooldown;
            return;
        }

        var videoId = best["videoId"]?.ToString() ?? "";
        var url = $"https://www.youtube.com/watch?v={videoId}";

        log.LogInformation("AutoDownload: starting '{Title}' ({Year}) → {VideoId}", title, year, videoId);
        if (!download.Start(movieId, url))
        {
            // Raced with another starter — try again next tick.
            _cooldownUntil[movieId] = DateTime.UtcNow + ErrorCooldown;
            return;
        }

        _lastStartedMovieId = movieId;
    }

    private void ExpireCooldowns()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _cooldownUntil)
            if (kv.Value < now) _cooldownUntil.TryRemove(kv.Key, out _);
    }
}
