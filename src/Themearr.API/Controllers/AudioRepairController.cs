using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;
namespace Themearr.API.Controllers;

[ApiController]
[Route("api/audio-repair")]
public sealed class AudioRepairController(AudioRepairService repair) : ControllerBase
{
    [HttpPost("preview")]
    public async Task<IActionResult> Preview(AudioRepairPreviewRequest request,CancellationToken ct)
    {
        try {return Ok(await repair.PreviewAsync(request,ct));}
        catch(ArgumentException ex) {return BadRequest(new {detail=ex.Message});}
        catch(InvalidOperationException ex) {return Conflict(new {detail=ex.Message});}
    }
    [HttpPost("apply")]
    public IActionResult Apply(AudioRepairApplyRequest request)
    {
        var result=repair.StartApply(request.PreviewId,request.ItemIds);
        return result.Started?Accepted(result):Conflict(new {detail=result.Error});
    }
    [HttpGet("status")]
    public IActionResult Status(Guid? runId=null) => Ok(repair.GetStatus(runId));
    [HttpPost("cancel")]
    public IActionResult Cancel(AudioRepairCancelRequest request) => Ok(new {cancelled=repair.Cancel(request.RunId)});
}
