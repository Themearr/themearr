using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;
using Themearr.API.Services.Health;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController(HealthCache health, TaskRegistry tasks) : ControllerBase
{
    [HttpGet("health")]
    public async Task<HealthResponse> Health(CancellationToken ct) => (await health.GetAsync(ct)).Response;

    [HttpGet("tasks")]
    public IReadOnlyList<TaskState> Tasks() => tasks.Snapshot();

    [HttpPost("tasks/{id}/run")]
    public IActionResult Run(string id)
    {
        if (!tasks.Exists(id))
            return NotFound(new { detail = "Unknown task" });

        var state = tasks.Snapshot().FirstOrDefault(t => t.Id == id);
        if (state?.IsRunning == true)
            return Conflict(new { detail = "That task is already running" });

        // Trigger() returning false means a run is already queued, which is the same
        // outcome the caller wanted — report success either way.
        tasks.Trigger(id);
        return Accepted(new { started = true });
    }
}
