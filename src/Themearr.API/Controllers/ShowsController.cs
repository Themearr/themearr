using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

/// <summary>
/// The shows API. A deliberate parallel of <see cref="MoviesController"/> rather than a
/// media-type-generic controller: branching every movie route by media type risks changing
/// movie behaviour, and the logic genuinely worth sharing already lives in
/// <see cref="ThemeFiles"/> and <see cref="DownloadService"/>.
///
/// Unlike the movie routes' legacy shape, everything here is namespaced under
/// <c>/api/shows</c> — except posters, which must sit under the public <c>/api/poster</c>
/// prefix (see <see cref="PosterController.GetShow"/>).
/// </summary>
[ApiController]
[Route("api/shows")]
public class ShowsController(
    Database db, YoutubeService youtube, DownloadService download, PosterUrlSigner posterSigner,
    ILogger<ShowsController> log) : ControllerBase
{
    [HttpGet]
    public IActionResult ListShows()
    {
        var shows = db.GetAllShows();
        var posterExpiry = DateTimeOffset.UtcNow.AddHours(12);
        foreach (var show in shows)
        {
            var id = show.GetValueOrDefault("id")?.ToString() ?? "";

            // Shows come only from Plex, so unlike movies there is no active-source check
            // here — a show with a source_ref always has a Plex poster to sign a URL for.
            var hasPoster = !string.IsNullOrEmpty(show.GetValueOrDefault("sourceRef")?.ToString());

            show["posterUrl"] = (!string.IsNullOrEmpty(id) && hasPoster)
                ? posterSigner.ShowPosterPath(id, posterExpiry)
                : null;
        }
        return Ok(shows);
    }

    [HttpGet("{showId}/search")]
    public async Task<IActionResult> SearchYoutube(string showId, [FromQuery] string? q = null)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var title = show["title"]?.ToString() ?? "";

        // Year-free by default: a show spans years, so including one biases the search
        // toward a single season's upload. Same query the auto-download worker uses, so a
        // manual search and an automatic one agree on what they are looking for.
        var query = !string.IsNullOrWhiteSpace(q) ? q : ShowAutoDownloadService.BuildQuery(title);

        try
        {
            var results = await youtube.SearchAsync(query, maxResults: 8, title: title);
            return Ok(new { show, results });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }
    }
}
