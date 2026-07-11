using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api")]
public class MoviesController(
    Database db, YoutubeService youtube, DownloadService download, PosterUrlSigner posterSigner,
    ILogger<MoviesController> log) : ControllerBase
{
    [HttpGet("movies")]
    public IActionResult ListMovies()
    {
        var movies = db.GetAllMovies();
        var posterExpiry = DateTimeOffset.UtcNow.AddHours(12);
        foreach (var movie in movies)
        {
            var id  = movie.GetValueOrDefault("id")?.ToString() ?? "";
            var sid = movie.GetValueOrDefault("plexServerId")?.ToString() ?? "";
            var rk  = movie.GetValueOrDefault("plexRatingKey")?.ToString() ?? "";
            // Signed, token-free poster URL — the Plex token stays server-side (see PosterController).
            movie["posterUrl"] = (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(rk))
                ? posterSigner.PosterPath(id, posterExpiry)
                : null;
        }
        return Ok(movies);
    }

    [HttpGet("search/{movieId}")]
    public async Task<IActionResult> SearchYoutube(string movieId, [FromQuery] string? q = null)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var title   = movie["title"]?.ToString() ?? "";
        var yearObj = movie["year"];
        var year    = yearObj?.ToString() ?? "";
        var yearInt = yearObj is int y ? y : (int?)null;
        var query   = !string.IsNullOrWhiteSpace(q) ? q : $"{title} {year} theme".Trim();

        try
        {
            var results = await youtube.SearchAsync(query, maxResults: 8, movieTitle: title, movieYear: yearInt);
            return Ok(new { movie, results });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }
    }

    [HttpDelete("movies/{movieId}/theme")]
    public IActionResult DeleteTheme(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var folder = movie["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder))
            return BadRequest(new { detail = "Movie has no folder" });

        // Confine deletes to the configured library roots (see DownloadService).
        var roots = db.GetLibraryPaths();
        if (roots.Count > 0 && !ThemeFiles.IsWithinRoots(folder, roots))
            return BadRequest(new { detail = "Refusing to delete outside the configured library roots." });

        var deleted = false;
        foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
        {
            if (Path.GetExtension(f) is ".part" or ".ytdl") continue;
            System.IO.File.Delete(f);
            deleted = true;
        }
        return Ok(new { deleted });
    }

    [HttpPost("movies/{movieId}/ignore")]
    public IActionResult IgnoreMovie(string movieId)
    {
        if (db.GetMovie(movieId) == null) return NotFound(new { detail = "Movie not found" });
        db.SetMovieIgnored(movieId, true);
        return Ok(new { ignored = true });
    }

    [HttpPost("movies/{movieId}/unignore")]
    public IActionResult UnignoreMovie(string movieId)
    {
        if (db.GetMovie(movieId) == null) return NotFound(new { detail = "Movie not found" });
        db.SetMovieIgnored(movieId, false);
        return Ok(new { ignored = false });
    }

    [HttpGet("movies/{movieId}/theme/audio")]
    public IActionResult GetThemeAudio(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var folder = movie["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return NotFound(new { detail = "No folder" });

        var themeFile = Directory.EnumerateFiles(folder, "theme.*")
            .FirstOrDefault(f => Path.GetExtension(f) is not (".part" or ".ytdl"));
        if (themeFile == null) return NotFound(new { detail = "No theme file" });

        var contentType = Path.GetExtension(themeFile).ToLower() switch
        {
            ".mp3"  => "audio/mpeg",
            ".m4a"  => "audio/mp4",
            ".ogg"  => "audio/ogg",
            ".opus" => "audio/opus",
            ".webm" => "audio/webm",
            ".flac" => "audio/flac",
            _       => "audio/mpeg",
        };

        // ETag + Last-Modified so repeated visits don't re-download the same theme file.
        // Framework honours If-None-Match / If-Modified-Since and returns 304 automatically.
        var info = new FileInfo(themeFile);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(themeFile, contentType, info.LastWriteTimeUtc, etag, enableRangeProcessing: true);
    }

    [HttpPost("auto-download/{movieId}")]
    public async Task<IActionResult> AutoDownload(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var title   = movie["title"]?.ToString() ?? "";
        var yearObj = movie["year"];
        var year    = yearObj?.ToString() ?? "";
        var yearInt = yearObj is int y ? y : (int?)null;
        var query   = $"{title} {year} theme".Trim();

        List<Dictionary<string, object?>> results;
        try
        {
            results = await youtube.SearchAsync(query, maxResults: 8, movieTitle: title, movieYear: yearInt);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
            return UnprocessableEntity(new { detail = "No suitable match found — please select manually." });

        if (download.DownloadBlockedReason(isProviderUrl: true) is { } notReady)
        {
            log.LogWarning("Auto-download for {MovieId} blocked: {Reason}", LogSanitizer.Clean(movieId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        var videoId = best["videoId"]?.ToString() ?? "";
        var url     = $"https://www.youtube.com/watch?v={videoId}";
        download.Start(movieId, url);

        return Accepted(new { started = true, movieId, videoId, videoTitle = best["title"] });
    }

    [HttpPost("download")]
    [Consumes("application/json")]
    public IActionResult Download([FromBody] DownloadRequest req)
    {
        if (db.GetMovie(req.MovieId) == null)
            return NotFound(new { detail = "Movie not found" });

        if (download.DownloadBlockedReason(isProviderUrl: true) is { } notReady)
        {
            log.LogWarning("Download for {MovieId} blocked: {Reason}", LogSanitizer.Clean(req.MovieId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        var url = $"https://www.youtube.com/watch?v={req.VideoId}";
        download.Start(req.MovieId, url);
        return Accepted(new { started = true, movieId = req.MovieId });
    }

    [HttpPost("download-url")]
    [Consumes("application/json")]
    public IActionResult DownloadUrl([FromBody] DownloadUrlRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) ||
            !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
            return BadRequest(new { detail = "Invalid URL" });

        if (uri.Scheme is not ("http" or "https"))
            return BadRequest(new { detail = "Only http and https URLs are supported." });

        if (HostGuard.IsPrivateOrLoopback(uri.Host))
            return BadRequest(new { detail = "Refusing to download from a private or loopback address." });

        if (db.GetMovie(req.MovieId) == null)
            return NotFound(new { detail = "Movie not found" });

        // A pasted YouTube URL still goes through the provider, so pre-flight it
        // (config + quota cooldown). Direct URLs are not gated.
        if (download.DownloadBlockedReason(DownloadService.IsProviderUrl(req.Url)) is { } notReady)
        {
            log.LogWarning("Download-url for {MovieId} blocked: {Reason}", LogSanitizer.Clean(req.MovieId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        download.Start(req.MovieId, req.Url);
        return Accepted(new { started = true, movieId = req.MovieId });
    }

    [HttpGet("download/status/{movieId}")]
    public IActionResult DownloadStatus(string movieId)
    {
        return Ok(download.GetStatus(movieId));
    }

    // Diagnostic view of the server-side auto-download loop. Use this to verify
    // the background service is actually running when "set and forget" seems broken.
    [HttpGet("auto-download/debug")]
    public IActionResult AutoDownloadDebug([FromServices] AutoDownloadService auto)
        => Ok(auto.GetDiagnostics());
}

public record DownloadRequest(string MovieId, string VideoId);
public record DownloadUrlRequest(string MovieId, string Url);
