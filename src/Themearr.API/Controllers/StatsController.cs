using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/stats")]
public class StatsController(Database db) : ControllerBase
{
    [HttpGet]
    public IActionResult GetStats()
    {
        var stats     = db.GetStats();
        var serverMap = db.GetPlexServersDict();

        // Attach poster URLs to recently-added movies (same logic as MoviesController)
        foreach (var movie in stats.RecentlyAdded)
        {
            var sid = movie.GetValueOrDefault("plexServerId")?.ToString()  ?? "";
            var rk  = movie.GetValueOrDefault("plexRatingKey")?.ToString() ?? "";
            movie["posterUrl"] = (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(rk)
                && serverMap.TryGetValue(sid, out var srv))
                ? $"{srv.Url}/library/metadata/{rk}/thumb?X-Plex-Token={srv.Token}"
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
