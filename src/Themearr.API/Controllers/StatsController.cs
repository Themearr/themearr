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
            var id  = movie.GetValueOrDefault("id")?.ToString()           ?? "";
            var sid = movie.GetValueOrDefault("plexServerId")?.ToString()  ?? "";
            var rk  = movie.GetValueOrDefault("plexRatingKey")?.ToString() ?? "";
            movie["posterUrl"] = (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(rk))
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
