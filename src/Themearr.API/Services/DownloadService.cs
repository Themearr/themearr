using System.Diagnostics;
using System.Text.RegularExpressions;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class DownloadService(Database db, ILogger<DownloadService> log)
{
    public async Task<string> DownloadThemeAsync(string movieId, string youtubeUrl)
    {
        var movie = db.GetMovie(movieId)
            ?? throw new KeyNotFoundException($"Movie not found: {movieId}");

        var folder = movie["folderName"]?.ToString()
            ?? throw new InvalidOperationException("Movie has no folder path");

        var url = NormaliseYoutubeUrl(youtubeUrl.Trim());
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            throw new ArgumentException("Invalid URL");

        if (!IsCommandAvailable("yt-dlp"))
            throw new InvalidOperationException("yt-dlp is not installed or not in PATH");

        var outputTemplate = Path.Combine(folder, "theme.%(ext)s");
        var psi = new ProcessStartInfo
        {
            FileName               = "yt-dlp",
            Arguments              = $"-x --audio-format mp3 --audio-quality 0 --no-playlist -o \"{outputTemplate}\" \"{url}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        log.LogInformation("Running yt-dlp for {MovieId}: {Url}", movieId, url);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var completed = await Task.WhenAny(
            Task.Run(() => proc.WaitForExit()),
            Task.Delay(TimeSpan.FromMinutes(15)));

        if (!proc.HasExited)
        {
            proc.Kill(true);
            throw new TimeoutException("Download timed out after 15 minutes");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            var tail = (stderr + "\n" + stdout).Trim();
            if (tail.Length > 1200) tail = tail[^1200..];
            throw new InvalidOperationException($"yt-dlp failed (exit {proc.ExitCode}): {tail}");
        }

        db.SetMovieStatus(movieId, "downloaded");
        return movieId;
    }

    private static string NormaliseYoutubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host.ToLower().TrimStart('w').TrimStart('w').TrimStart('w').TrimStart('.');
        // www.youtube.com / youtube.com / m.youtube.com
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
