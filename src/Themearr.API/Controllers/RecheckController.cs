using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

/// <summary>
/// Starting a re-check scan, its status, and the flagged list it produces. Mirrors
/// <see cref="SyncController"/>'s shape — a background service plus a thin controller —
/// since both are "start a scan, poll its status" (SyncController.cs:7-27). The per-item
/// actions on a flagged movie or show live on <see cref="MoviesController"/> and
/// <see cref="ShowsController"/> instead, alongside the ignore/unignore endpoints they
/// parallel, not here.
/// </summary>
[ApiController]
[Route("api/themes/recheck")]
public class RecheckController(ThemeRecheckService recheck, Database db) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Start()
    {
        var started = await recheck.StartAsync();
        return Ok(new { started, detail = started ? null : "Re-check already in progress" });
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(recheck.GetStatus());

    [HttpGet("flagged")]
    public IActionResult Flagged() => Ok(db.GetFlaggedItems());
}
