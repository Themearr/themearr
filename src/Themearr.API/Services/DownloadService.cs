using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class DownloadService(Database db, IHttpClientFactory httpClientFactory, ILogger<DownloadService> log)
{
    private sealed record JobState(bool InProgress, bool Finished, string? Error);
    private readonly ConcurrentDictionary<string, JobState>          _jobs    = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _jobLogs = new();

    private const int MaxLogLines = 300;

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
                // YouTube URL — use youtube-mp36 RapidAPI, poll until ready then download immediately
                var apiKey   = db.GetSetting("rapidapi_key", "");
                var username = db.GetSetting("rapidapi_username", "");
                if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(username))
                    throw new InvalidOperationException("RapidAPI key and username are not configured. Please add them in Settings.");

                var usernameMd5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(username))).ToLower();

                AddLog(movieId, $"[themearr] Fetching download link for video {videoId}…");
                log.LogInformation("Fetching RapidAPI download link for {MovieId}: {VideoId}", movieId, videoId);

                var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromMinutes(10);

                var deadline = DateTime.UtcNow.AddMinutes(5);
                var attempt = 0;

                while (true)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new InvalidOperationException("RapidAPI timed out waiting for processing to complete.");

                    attempt++;
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://youtube-mp36.p.rapidapi.com/dl?id={videoId}");
                    req.Headers.Add("X-RapidAPI-Key", apiKey);
                    req.Headers.Add("X-RapidAPI-Host", "youtube-mp36.p.rapidapi.com");

                    using var resp = await http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"RapidAPI error ({(int)resp.StatusCode}): {body}");

                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;

                    if (status == "processing")
                    {
                        AddLog(movieId, $"[themearr] Processing… (attempt {attempt})");
                        await Task.Delay(1000);
                        continue;
                    }

                    if (status != "ok")
                    {
                        var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : body;
                        throw new InvalidOperationException($"RapidAPI error (status={status}): {msg}");
                    }

                    var link = root.TryGetProperty("link", out var lnk) ? lnk.GetString() : null;
                    if (string.IsNullOrEmpty(link))
                        throw new InvalidOperationException($"RapidAPI returned ok but missing link: {body}");

                    themeTitle = root.TryGetProperty("title", out var t) ? t.GetString() : null;
                    AddLog(movieId, "[themearr] Got download link. Downloading immediately…");

                    // Download immediately while the link is fresh, with whitelist headers
                    using var dlReq = new HttpRequestMessage(HttpMethod.Get, link);
                    dlReq.Headers.TryAddWithoutValidation("User-Agent", $"Mozilla/5.0 {username}");
                    dlReq.Headers.Add("X-RUN", usernameMd5);
                    using var dlResp = await http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead);
                    if (!dlResp.IsSuccessStatusCode)
                    {
                        // Link expired — re-poll the API for a fresh one
                        AddLog(movieId, $"[themearr] Link expired ({(int)dlResp.StatusCode}), re-polling for a fresh link…");
                        await Task.Delay(1000);
                        continue;
                    }

                    await using var fileStream = File.Create(outputPath);
                    await dlResp.Content.CopyToAsync(fileStream);
                    await fileStream.FlushAsync();
                    break;
                }
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
        catch (Exception ex)
        {
            log.LogError(ex, "Download failed for {MovieId}", movieId);
            _jobs[movieId] = new JobState(false, true, ex.Message);
        }
    }

    private static string? ExtractVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLower().TrimStart('w').TrimStart('w').TrimStart('w').TrimStart('.');
        if (host is "youtube.com" or "m.youtube.com")
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var v = q["v"]?.Trim();
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host.ToLower().TrimStart('w').TrimStart('w').TrimStart('w').TrimStart('.');
        if (host is "youtube.com" or "m.youtube.com")
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var v = q["v"]?.Trim();
            if (!string.IsNullOrEmpty(v))
                return $"https://www.youtube.com/watch?v={v}";
        }
        if (host is "youtu.be")
        {
            var videoId = uri.AbsolutePath.Trim('/');
            if (!string.IsNullOrEmpty(videoId)) return $"https://youtu.be/{videoId}";
        }
        return url;
    }
}
