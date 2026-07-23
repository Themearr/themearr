using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api")]
public class VersionController(UpdateService update) : ControllerBase
{
    [HttpGet("version")]
    public Task<IActionResult> GetVersion() =>
        update.GetVersionInfoAsync().ContinueWith(t => (IActionResult)Ok(t.Result));

    [HttpPost("version/refresh")]
    public async Task<IActionResult> RefreshVersion()
    {
        update.InvalidateCache();
        var info = await update.GetVersionInfoAsync();
        return Ok(info);
    }

    [HttpPost("update")]
    public async Task<IActionResult> StartUpdate()
    {
        // A root-level host update must require the master token, not the externally-held
        // API key: otherwise a compromised Radarr (which holds that key) could trigger it.
        if (!HttpContext.AuthenticatedWithBearerToken())
            return StatusCode(StatusCodes.Status403Forbidden,
                new { detail = "Starting an update requires the access token, not the API key." });

        var started = await update.StartAsync();
        return Ok(new { started, detail = started ? null : "Update already in progress" });
    }

    [HttpGet("update/status")]
    public IActionResult UpdateStatus() => Ok(update.GetStatus());
}
