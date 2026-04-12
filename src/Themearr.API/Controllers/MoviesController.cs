using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api")]
public class MoviesController(Database db, YoutubeService youtube, DownloadService download) : ControllerBase
{
    [HttpGet("movies")]
    public IActionResult ListMovies() => Ok(db.GetAllMovies());

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
    public async Task<IActionResult> Download([FromBody] DownloadRequest req)
    {
        var url = $"https://www.youtube.com/watch?v={req.VideoId}";
        return await DownloadInternal(req.MovieId, url);
    }

    [HttpPost("download-url")]
    public async Task<IActionResult> DownloadUrl([FromBody] DownloadUrlRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) || !Uri.IsWellFormedUriString(req.Url, UriKind.Absolute))
            return BadRequest(new { detail = "Invalid URL" });
        return await DownloadInternal(req.MovieId, req.Url);
    }

    private async Task<IActionResult> DownloadInternal(string movieId, string url)
    {
        try
        {
            await download.DownloadThemeAsync(movieId, url);
            return Ok(new { status = "downloaded", movieId });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { detail = ex.Message }); }
        catch (ArgumentException ex)    { return BadRequest(new { detail = ex.Message }); }
        catch (TimeoutException ex)     { return StatusCode(504, new { detail = ex.Message }); }
        catch (Exception ex)            { return StatusCode(500, new { detail = ex.Message }); }
    }
}

public record DownloadRequest(string MovieId, string VideoId);
public record DownloadUrlRequest(string MovieId, string Url);
