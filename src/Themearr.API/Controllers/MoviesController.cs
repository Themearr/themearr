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
    public async Task<IActionResult> SearchYoutube(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var title = movie["title"]?.ToString() ?? "";
        var year  = movie["year"]?.ToString() ?? "";
        var query = $"{title} {year} theme song".Trim();

        try
        {
            var results = await youtube.SearchAsync(query, maxResults: 3);
            return Ok(new { movie, results });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }
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
