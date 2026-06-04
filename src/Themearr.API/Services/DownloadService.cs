using System.Collections.Concurrent;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class DownloadService(
    IThemeAudioProvider provider, Database db, IHttpClientFactory httpClientFactory,
    IConfiguration config, ILogger<DownloadService> log)
{
    private sealed record JobState(bool InProgress, bool Finished, string? Error);
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

        _jobs[movieId] = new JobState(true, false, null);
        _ = Task.Run(() => RunAsync(movieId, url));
        return true;
    }

    public bool IsAnyInProgress() => _jobs.Values.Any(j => j.InProgress);

    // Cheap (no-network) readiness check for the configured provider. Returns a
    // user-facing message when downloads can't run yet (e.g. missing credentials),
    // else null. Lets callers pre-flight before kicking off an async job.
    public string? CheckProviderReadiness() => provider.CheckConfiguration();

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

            var outputPath = Path.Combine(folder, "theme.mp3");

            // Remove any existing theme files before writing
            foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
                File.Delete(f);

            if (videoId != null)
            {
                // YouTube URL — delegate to the configured theme-audio provider.
                themeTitle = await provider.DownloadAsync(videoId, outputPath, msg => AddLog(movieId, msg));
            }
            else
            {
                // Non-YouTube URL — download directly
                AddLog(movieId, "[themearr] Downloading from URL…");

                var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromMinutes(15);
                using var dlResp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (!dlResp.IsSuccessStatusCode)
                {
                    var errBody = await dlResp.Content.ReadAsStringAsync();
                    var snippet = errBody.Length > 300 ? errBody[..300] : errBody;
                    throw new InvalidOperationException($"Download failed ({(int)dlResp.StatusCode}): {snippet}");
                }

                await using var fileStream = File.Create(outputPath);
                await dlResp.Content.CopyToAsync(fileStream);
                await fileStream.FlushAsync();
            }

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
        catch (Exception ex)
        {
            log.LogError(ex, "Download failed for {MovieId}", movieId);
            _jobs[movieId] = new JobState(false, true, ex.Message);
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
