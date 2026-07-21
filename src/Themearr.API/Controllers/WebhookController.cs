using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

/// <summary>
/// Receives Radarr's Connect webhooks so a newly imported movie gets its theme in
/// seconds rather than at the next scheduled sync.
///
/// Sits under /api/*, so ApiAuthMiddleware guards it and the API key works here
/// without an exemption.
/// </summary>
[ApiController]
[Route("api/webhook")]
public class WebhookController(TaskRegistry tasks, ILogger<WebhookController> log) : ControllerBase
{
    // Radarr's payloads are a few KB; 64 KB is generous headroom over that and keeps a
    // caller from forcing the default 30 MB body through JsonDocument parsing.
    private const int MaxBodyBytes = 64 * 1024;

    // Long enough for any real Radarr eventType (the longest today is a handful of
    // characters); short enough that a hostile value can't be reflected back at size.
    private const int MaxEventTypeLength = 64;

    [HttpPost("radarr")]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxBodyBytes)]
    public IActionResult Radarr([FromBody] JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("eventType", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
            return BadRequest(new { detail = "Expected a Radarr webhook payload with an eventType." });

        var eventType = typeElement.GetString() ?? "";
        if (eventType.Length > MaxEventTypeLength)
            eventType = eventType[..MaxEventTypeLength];

        // Radarr sends this when the operator presses Test. Answering it plainly is what
        // makes configuring the connection give feedback, rather than deferring the
        // discovery of a wrong URL or key to the next import.
        if (eventType == "Test")
            return Ok(new { received = "Test", detail = "Themearr is reachable." });

        // "Download" is Radarr's import event. Everything else — Grab, Rename,
        // MovieDelete, Health — is acknowledged and ignored: returning anything but 200
        // makes Radarr report the connection as failing and may disable it.
        if (eventType != "Download")
            return Ok(new { received = eventType, detail = "Ignored." });

        // Signal the existing sync rather than inserting the movie here: the sync owns
        // resolving and upserting, and a second write path into the movie table would
        // drift. The trigger channel holds one slot, so a batch import that fires many
        // webhooks still produces a single sync.
        tasks.Trigger(AutoSyncService.SyncTaskId);
        log.LogInformation("Radarr reported an import — library sync requested");
        return Ok(new { received = eventType, detail = "Sync requested." });
    }
}
