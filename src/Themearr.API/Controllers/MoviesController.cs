using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api")]
public class MoviesController(Database db, YoutubeService youtube, DownloadService download) : ControllerBase
{
    [HttpGet("movies")]
    public IActionResult ListMovies()
    {
        var movies = db.GetAllMovies();
        var serverMap = db.GetPlexServersDict();
        foreach (var movie in movies)
        {
            var sid = movie.GetValueOrDefault("plexServerId")?.ToString() ?? "";
            var rk  = movie.GetValueOrDefault("plexRatingKey")?.ToString() ?? "";
            movie["posterUrl"] = (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(rk) && serverMap.TryGetValue(sid, out var srv))
                ? $"{srv.Url}/library/metadata/{rk}/thumb?X-Plex-Token={srv.Token}"
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

        var deleted = false;
        foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
        {
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
        return PhysicalFile(themeFile, contentType, enableRangeProcessing: true);
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

        var videoId = best["videoId"]?.ToString() ?? "";
        var url     = $"https://www.youtube.com/watch?v={videoId}";
        download.Start(movieId, url);

        return Accepted(new { started = true, movieId, videoId, videoTitle = best["title"] });
    }

    [HttpPost("download")]
    public IActionResult Download([FromBody] DownloadRequest req)
    {
        if (db.GetMovie(req.MovieId) == null)
            return NotFound(new { detail = "Movie not found" });

        var url = $"https://www.youtube.com/watch?v={req.VideoId}";
        download.Start(req.MovieId, url);
        return Accepted(new { started = true, movieId = req.MovieId });
    }

    [HttpPost("download-url")]
    public IActionResult DownloadUrl([FromBody] DownloadUrlRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) || !Uri.IsWellFormedUriString(req.Url, UriKind.Absolute))
            return BadRequest(new { detail = "Invalid URL" });

        if (db.GetMovie(req.MovieId) == null)
            return NotFound(new { detail = "Movie not found" });

        download.Start(req.MovieId, req.Url);
        return Accepted(new { started = true, movieId = req.MovieId });
    }

    [HttpGet("download/status/{movieId}")]
    public IActionResult DownloadStatus(string movieId)
    {
        return Ok(download.GetStatus(movieId));
    }
}

public record DownloadRequest(string MovieId, string VideoId);
public record DownloadUrlRequest(string MovieId, string Url);
