using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class DownloadService(Database db, ILogger<DownloadService> log)
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

            if (!IsCommandAvailable("yt-dlp"))
                throw new InvalidOperationException("yt-dlp is not installed or not in PATH");

            var outputTemplate = Path.Combine(folder, "theme.%(ext)s");
            var psi = new ProcessStartInfo
            {
                FileName               = "yt-dlp",
                Arguments              = $"-x --audio-format mp3 --audio-quality 0 --no-playlist --no-simulate --js-runtimes node --print \"%(title)s\" -o \"{outputTemplate}\" \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };

            log.LogInformation("Running yt-dlp for {MovieId}: {Url}", movieId, url);

            _jobLogs.TryGetValue(movieId, out var logQueue);

            using var proc = Process.Start(psi)!;

            var stdoutLines = new List<string>();
            var stderrLines = new List<string>();

            var stdoutTask = Task.Run(async () => {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    stdoutLines.Add(line);
            });

            var stderrTask = Task.Run(async () => {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                {
                    stderrLines.Add(line);
                    if (logQueue != null)
                    {
                        logQueue.Enqueue(line);
                        // Trim to prevent unbounded growth
                        while (logQueue.Count > MaxLogLines)
                            logQueue.TryDequeue(out _);
                    }
                }
            });

            await Task.WhenAny(
                Task.Run(() => proc.WaitForExit()),
                Task.Delay(TimeSpan.FromMinutes(15)));

            if (!proc.HasExited)
            {
                proc.Kill(true);
                throw new TimeoutException("Download timed out after 15 minutes");
            }

            await Task.WhenAll(stdoutTask, stderrTask);

            var stdout = string.Join("\n", stdoutLines);
            var stderr = string.Join("\n", stderrLines);

            if (proc.ExitCode != 0)
            {
                var tail = (stderr + "\n" + stdout).Trim();
                if (tail.Length > 1200) tail = tail[^1200..];
                throw new InvalidOperationException($"yt-dlp failed (exit {proc.ExitCode}): {tail}");
            }

            // Verify a theme file was actually produced (yt-dlp can exit 0 if
            // the download succeeds but ffmpeg post-processing fails silently)
            var themeFile = Directory.EnumerateFiles(folder, "theme.*")
                                     .FirstOrDefault(f => Path.GetExtension(f) is not (".part" or ".ytdl"));
            if (themeFile == null)
                throw new InvalidOperationException(
                    "yt-dlp exited successfully but no theme file was written — " +
                    "ffmpeg may not be installed or the conversion failed");

            var title      = movie["title"]?.ToString() ?? "";
            var year       = movie["year"] is int y ? y : (int?)null;
            var themeTitle = stdout.Split('\n')
                                   .Select(l => l.Trim())
                                   .FirstOrDefault(l => l.Length > 0);
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

    private static bool IsCommandAvailable(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }
}
