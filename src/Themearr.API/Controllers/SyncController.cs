using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController(SyncService sync, LibrarySourceResolver sources) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> StartSync()
    {
        // Delegates to the active source rather than assuming Plex, so a Radarr install's
        // sync button works and a misconfigured source gets a source-appropriate message
        // instead of a generic 400 (or, before this, one that only ever mentioned Plex).
        var reason = sources.Active.SyncBlockedReason;
        if (reason is not null)
            return BadRequest(new { detail = reason });

        var started = await sync.StartAsync();
        return Ok(new { started, detail = started ? null : "Sync already in progress" });
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(sync.GetStatus());
}
