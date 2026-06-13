using System.Collections.Concurrent;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class DownloadService(
    IThemeAudioProvider provider, Database db, IHttpClientFactory httpClientFactory,
    IConfiguration config, ILogger<DownloadService> log)
{
    private sealed record JobState(bool InProgress, bool Finished, string? Error, DateTime StartedAtUtc = default);
    private readonly ConcurrentDictionary<string, JobState>          _jobs    = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _jobLogs = new();

    private const int MaxLogLines = 300;

    // After a provider quota-exhaustion (HTTP 429) we pause downloads until this time
    // so the auto-download loop stops hammering the API. Volatile single-writer state.
    private volatile int _quotaCooldownUntilUnix;

    private TimeSpan QuotaCooldown
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("THEMEARR_QUOTA_COOLDOWN_MINUTES")
                      ?? config["Themearr:QuotaCooldownMinutes"];
            return int.TryParse(raw, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : TimeSpan.FromHours(1);
        }
    }

    // Hard ceiling on a single download. A stalled CDN connection (silent TCP drop)
    // can leave the response-stream read hanging forever; without this bound the job
    // stays "in progress" and wedges the auto-download loop until a restart.
    private TimeSpan DownloadTimeout
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("THEMEARR_DOWNLOAD_TIMEOUT_SECONDS")
                      ?? config["Themearr:DownloadTimeoutSeconds"];
            return int.TryParse(raw, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(15);
        }
    }

    // True while a quota cooldown is active; `untilUtc` is the (UTC) resume time.
    public bool IsQuotaCoolingDown(out DateTime untilUtc)
    {
        var until = _quotaCooldownUntilUnix;
        untilUtc = until == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(until).UtcDateTime;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < until;
    }

    public bool Start(string movieId, string youtubeUrl)
    {
        if (_jobs.TryGetValue(movieId, out var existing) && existing.InProgress)
            return false;

        var url  = NormaliseYoutubeUrl(youtubeUrl.Trim());
        var logs = _jobLogs.GetOrAdd(movieId, _ => new ConcurrentQueue<string>());
        while (logs.TryDequeue(out _)) { }   // clear previous run's logs

        _jobs[movieId] = new JobState(true, false, null, DateTime.UtcNow);
        _ = Task.Run(() => RunAsync(movieId, url));
        return true;
    }

    // Defense-in-depth beyond the per-job timeout: if a job somehow stays "in progress"
    // past the timeout plus a grace margin (e.g. a backend that ignores cancellation),
    // stop counting it as blocking so a single pathological download can't wedge the
    // auto-download loop until a restart.
    private TimeSpan WatchdogGrace
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("THEMEARR_DOWNLOAD_WATCHDOG_GRACE_SECONDS")
                      ?? config["Themearr:DownloadWatchdogGraceSeconds"];
            return int.TryParse(raw, out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(30);
        }
    }

    public bool IsAnyInProgress()
    {
        var staleBefore = DateTime.UtcNow - (DownloadTimeout + WatchdogGrace);
        return _jobs.Values.Any(j => j.InProgress && j.StartedAtUtc > staleBefore);
    }

    // Single gate for provider-bound downloads, shared by the manual/UI endpoints AND
    // the auto-download loop so they behave identically: blocks when the provider is
    // unconfigured or while a 429 quota cooldown is active (so a manual retry doesn't
    // just burn another billed request). Direct (non-provider) URLs are never gated.
    public string? DownloadBlockedReason(bool isProviderUrl)
    {
        if (!isProviderUrl) return null;
        if (provider.CheckConfiguration() is { } notReady) return notReady;
        if (IsQuotaCoolingDown(out var until))
            return $"RapidAPI quota is exhausted — downloads are paused until {until:HH:mm} UTC. Try again later.";
        return null;
    }

    // True if this URL would be handled by the theme-audio provider (a YouTube URL)
    // rather than fetched directly. Used to decide whether a provider readiness
    // check applies before starting a download.
    public static bool IsProviderUrl(string url) => ExtractVideoId(url) != null;

    public object GetStatus(string movieId)
    {
        if (!_jobs.TryGetValue(movieId, out var state))
            return new { inProgress = false, finished = false, error = (string?)null, logs = Array.Empty<string>() };

        _jobLogs.TryGetValue(movieId, out var logQueue);
        var lines = logQueue?.ToArray() ?? [];
        if (lines.Length > 50) lines = lines[^50..];

        return new { inProgress = state.InProgress, finished = state.Finished, error = state.Error, logs = lines };
    }

    private void AddLog(string movieId, string message)
    {
        if (!_jobLogs.TryGetValue(movieId, out var logQueue)) return;
        logQueue.Enqueue(message);
        while (logQueue.Count > MaxLogLines)
            logQueue.TryDequeue(out _);
    }

    private async Task RunAsync(string movieId, string url)
    {
        try
        {
            var movie = db.GetMovie(movieId)
                ?? throw new KeyNotFoundException($"Movie not found: {movieId}");

            var folder = movie["folderName"]?.ToString()
                ?? throw new InvalidOperationException("Movie has no folder path");

            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                throw new ArgumentException("Invalid URL");

            var videoId = ExtractVideoId(url);

            string? themeTitle = null;

            // Confine writes to the configured library roots so a malicious/compromised
            // Plex server can't redirect a theme write to an arbitrary directory (e.g.
            // /opt/themearr). Enforced only when roots are configured, to preserve
            // behaviour on installs that rely solely on direct path resolution.
            var roots = db.GetLibraryPaths();
            if (roots.Count > 0 && !ThemeFiles.IsWithinRoots(folder, roots))
                throw new UnauthorizedAccessException(
                    $"Refusing to write outside the configured library roots: \"{folder}\".");

            var outputPath = Path.Combine(folder, "theme.mp3");

            // Fail fast with an actionable message if the folder isn't writable — the
            // common Proxmox/LXC case where the themearr service user lacks permission
            // on a bind-mounted media folder. Without this the download fails opaquely
            // for every movie and the auto-loop just silently cools each one down.
            if (!ThemeFiles.IsDirectoryWritable(folder))
                throw new UnauthorizedAccessException(
                    $"Cannot write to \"{folder}\". The themearr service user needs write permission on " +
                    "this movie folder — on Proxmox/LXC, add the themearr user to your media group.");

            // Bound the whole download (incl. the response-stream read, which
            // HttpClient.Timeout does NOT cover once streaming) so a stalled
            // connection can't hang the job forever.
            using var cts = new CancellationTokenSource(DownloadTimeout);
            var token = cts.Token;

            if (videoId != null)
            {
                // YouTube URL — delegate to the configured theme-audio provider.
                themeTitle = await provider.DownloadAsync(videoId, outputPath, msg => AddLog(movieId, msg), token);
            }
            else
            {
                // Non-YouTube URL — download directly
                AddLog(movieId, "[themearr] Downloading from URL…");

                using var dlResp = await FetchFollowingSafeRedirectsAsync(url, token);

                if (!dlResp.IsSuccessStatusCode)
                {
                    var errBody = await dlResp.Content.ReadAsStringAsync(token);
                    var snippet = errBody.Length > 300 ? errBody[..300] : errBody;
                    throw new InvalidOperationException($"Download failed ({(int)dlResp.StatusCode}): {snippet}");
                }

                // Atomic: stream to theme.mp3.part then move into place, so a failed or
                // empty download never clobbers a previously-good theme.
                await ThemeFiles.WriteAtomicAsync(
                    await dlResp.Content.ReadAsStreamAsync(token), outputPath, StreamLimits.MaxThemeBytes, token);
            }

            // Remove stale alternate-extension theme files (e.g. an old theme.m4a) now
            // that the new theme.mp3 is safely in place — never before the download.
            foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
                if (!string.Equals(f, outputPath, StringComparison.Ordinal)
                    && Path.GetExtension(f) is not (".part" or ".ytdl"))
                    try { File.Delete(f); } catch { /* best effort */ }

            AddLog(movieId, "[themearr] Download complete.");

            var title = movie["title"]?.ToString() ?? "";
            var year  = movie["year"] is int y ? y : (int?)null;
            db.SetMovieStatus(movieId, "downloaded");
            db.AddThemeHistory(movieId, title, year, themeTitle, url);
            _jobs[movieId] = new JobState(false, true, null);
        }
        catch (QuotaExceededException ex)
        {
            var until = DateTimeOffset.UtcNow.Add(QuotaCooldown);
            _quotaCooldownUntilUnix = (int)until.ToUnixTimeSeconds();
            log.LogWarning("RapidAPI quota exhausted — pausing downloads until {Until:o}. {Detail}",
                until.UtcDateTime, ex.Message);
            AddLog(movieId, $"[themearr] RapidAPI quota exhausted — pausing downloads until {until.UtcDateTime:HH:mm} UTC.");
            _jobs[movieId] = new JobState(false, true, ex.Message);
        }
        catch (OperationCanceledException)
        {
            var msg = $"Download timed out after {DownloadTimeout.TotalSeconds:0}s and was aborted.";
            log.LogWarning("Download for {MovieId} timed out after {Timeout}", movieId, DownloadTimeout);
            AddLog(movieId, $"[themearr] {msg}");
            _jobs[movieId] = new JobState(false, true, msg);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Download failed for {MovieId}", movieId);
            _jobs[movieId] = new JobState(false, true, ex.Message);
        }
    }

    // Fetches `url`, following redirects manually so EVERY hop is re-validated against
    // the SSRF guard. A 3xx to an internal address (169.254.x, 10.x, …) is the classic
    // bypass of an initial-host-only check; the download-url endpoint validates the
    // first host, and this closes the redirect gap. Uses the "no-redirect" client.
    private async Task<HttpResponseMessage> FetchFollowingSafeRedirectsAsync(string url, CancellationToken ct)
    {
        const int MaxRedirects = 5;
        var http = httpClientFactory.CreateClient("no-redirect");
        http.Timeout = Timeout.InfiniteTimeSpan; // the CTS bounds the whole operation

        var current = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var resp = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
            {
                resp.Dispose();
                if (hop >= MaxRedirects)
                    throw new InvalidOperationException("Too many redirects while downloading.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme is not ("http" or "https") || HostGuard.IsPrivateOrLoopback(next.Host))
                    throw new InvalidOperationException(
                        "Refusing to follow a redirect to a private, loopback, or non-http(s) address.");
                current = next;
                continue;
            }
            return resp;
        }
    }

    // Single source of truth for YouTube URL parsing. Returns null for non-YouTube URLs.
    private static string? ExtractVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];

        if (host is "youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            var v = System.Web.HttpUtility.ParseQueryString(uri.Query)["v"]?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        if (host is "youtu.be")
        {
            var videoId = uri.AbsolutePath.Trim('/');
            return string.IsNullOrEmpty(videoId) ? null : videoId;
        }
        return null;
    }

    private static string NormaliseYoutubeUrl(string url)
    {
        var videoId = ExtractVideoId(url);
        return videoId == null ? url : $"https://www.youtube.com/watch?v={videoId}";
    }
}
