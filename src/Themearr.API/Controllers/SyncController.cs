using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController(Database db, SyncService sync) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> StartSync()
    {
        var servers   = db.GetPlexServers();
        var libraries = db.GetSelectedLibraries();
        if (servers.Count == 0 || libraries.Values.Sum(v => v.Count) == 0)
            return BadRequest(new { detail = "Plex sign-in is not complete" });

        var started = await sync.StartAsync();
        return Ok(new { started, detail = started ? null : "Sync already in progress" });
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(sync.GetStatus());
}
