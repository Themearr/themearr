using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/stats")]
public class StatsController(Database db, PosterUrlSigner posterSigner, LibrarySourceResolver sources)
    : ControllerBase
{
    [HttpGet]
    public IActionResult GetStats()
    {
        var stats     = db.GetStats();
        var posterExpiry = DateTimeOffset.UtcNow.AddHours(12);
        var activeSource = sources.Active.Name;

        // Attach signed, token-free poster URLs (same as MoviesController).
        foreach (var movie in stats.RecentlyAdded)
        {
            var id = movie.GetValueOrDefault("id")?.ToString() ?? "";

            // source_ref is opaque outside its own source (see PosterController); only a
            // movie whose source matches the active one has a poster to sign a URL for.
            var hasPoster = movie.GetValueOrDefault("source")?.ToString() == activeSource
                         && !string.IsNullOrEmpty(movie.GetValueOrDefault("sourceRef")?.ToString());

            movie["posterUrl"] = (!string.IsNullOrEmpty(id) && hasPoster)
                ? posterSigner.PosterPath(id, posterExpiry)
                : null;
        }

        return Ok(new
        {
            total         = stats.Total,
            downloaded    = stats.Downloaded,
            pending       = stats.Pending,
            ignored       = stats.Ignored,
            coverage      = stats.Coverage,
            addedThisWeek = stats.AddedThisWeek,
            recentActivity = stats.RecentActivity,
            recentlyAdded  = stats.RecentlyAdded,
        });
    }

    // Shows are counted separately from movies: the two libraries have different
    // denominators, and shows carry a state movies do not (plexTheme). Returns no poster
    // URLs, so it never touches the source resolver.
    [HttpGet("shows")]
    public IActionResult GetShowStats()
    {
        var s = db.GetShowStats();
        return Ok(new
        {
            total      = s.Total,
            downloaded = s.Downloaded,
            plexTheme  = s.PlexTheme,
            pending    = s.Pending,
            ignored    = s.Ignored,
            coverage   = s.Coverage,
        });
    }
}
