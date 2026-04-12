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

    [HttpPost("update")]
    public async Task<IActionResult> StartUpdate()
    {
        var started = await update.StartAsync();
        return Ok(new { started, detail = started ? null : "Update already in progress" });
    }

    [HttpGet("update/status")]
    public IActionResult UpdateStatus() => Ok(update.GetStatus());
}
