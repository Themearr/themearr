using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/stats")]
public class StatsController(Database db, PosterUrlSigner posterSigner) : ControllerBase
{
    [HttpGet]
    public IActionResult GetStats()
    {
        var stats     = db.GetStats();
        var posterExpiry = DateTimeOffset.UtcNow.AddHours(12);

        // Attach signed, token-free poster URLs (same as MoviesController).
        foreach (var movie in stats.RecentlyAdded)
        {
            var id = movie.GetValueOrDefault("id")?.ToString() ?? "";

            // Plex stores "{serverId}:{ratingKey}" in source_ref; only Plex movies have a
            // poster to sign a URL for (see PosterController).
            var isPlex = movie.GetValueOrDefault("source")?.ToString() == "plex";
            var parts  = (movie.GetValueOrDefault("sourceRef")?.ToString() ?? "").Split(':', 2);
            var hasRef = parts.Length == 2 && parts.All(p => !string.IsNullOrEmpty(p));

            movie["posterUrl"] = (!string.IsNullOrEmpty(id) && isPlex && hasRef)
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
}
