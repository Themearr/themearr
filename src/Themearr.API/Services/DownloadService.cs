using System.Collections.Concurrent;
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

            string downloadUrl;
            string? themeTitle = null;

            if (videoId != null)
            {
                // YouTube URL — use youtube-mp36 RapidAPI (may require polling while status=processing)
                var apiKey = db.GetSetting("rapidapi_key", "");
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("RapidAPI key is not configured. Please add it in Settings.");

                AddLog(movieId, $"[themearr] Fetching download link for video {videoId}…");
                log.LogInformation("Fetching RapidAPI download link for {MovieId}: {VideoId}", movieId, videoId);

                var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(30);

                string? status = null;
                string? link = null;
                string? title = null;
                var deadline = DateTime.UtcNow.AddMinutes(5);
                var attempt = 0;

                while (DateTime.UtcNow < deadline)
                {
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

                    status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
                    title  = root.TryGetProperty("title",  out var t)  ? t.GetString()  : null;

                    if (status == "ok")
                    {
                        link = root.TryGetProperty("link", out var lnk) ? lnk.GetString() : null;
                        if (string.IsNullOrEmpty(link))
                            throw new InvalidOperationException($"RapidAPI returned ok but missing link: {body}");
                        break;
                    }

                    if (status == "processing")
                    {
                        AddLog(movieId, $"[themearr] Processing… (attempt {attempt}, retrying in 3s)");
                        await Task.Delay(3000);
                        continue;
                    }

                    // Any other status is a hard failure
                    var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : body;
                    throw new InvalidOperationException($"RapidAPI error (status={status}): {msg}");
                }

                if (status != "ok" || string.IsNullOrEmpty(link))
                    throw new InvalidOperationException("RapidAPI timed out waiting for processing to complete.");

                downloadUrl = link;
                themeTitle = title;
                AddLog(movieId, "[themearr] Got download link. Downloading…");
            }
            else
            {
                // Non-YouTube URL — download directly
                downloadUrl = url;
                AddLog(movieId, $"[themearr] Downloading from URL…");
            }

            var outputPath = Path.Combine(folder, "theme.mp3");

            // Remove any existing theme files before writing
            foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
                File.Delete(f);

            var http2 = httpClientFactory.CreateClient();
            http2.Timeout = TimeSpan.FromMinutes(15);
            using var dlResp = await http2.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);

            if (!dlResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Download failed ({(int)dlResp.StatusCode}): {dlResp.ReasonPhrase}");

            await using var fileStream = File.Create(outputPath);
            await dlResp.Content.CopyToAsync(fileStream);
            await fileStream.FlushAsync();

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
