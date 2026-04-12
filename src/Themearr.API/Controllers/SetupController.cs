using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/setup")]
public class SetupController(Database db, PlexService plex) : ControllerBase
{
    // ── Status ────────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public IActionResult Status() => Ok(SetupPayload());

    // ── Plex PIN login ────────────────────────────────────────────────────────

    [HttpPost("plex/login")]
    public async Task<IActionResult> StartPlexLogin([FromBody] PlexLoginRequest req)
    {
        var result = await plex.CreateLoginPinAsync(req.ForwardUrl?.Trim() ?? "");
        return Ok(result);
    }

    [HttpGet("plex/login/status")]
    public async Task<IActionResult> PlexLoginStatus([FromQuery] int pinId, [FromQuery] string code)
    {
        Dictionary<string, object> pinState;
        try { pinState = await plex.CheckLoginPinAsync(pinId, code); }
        catch (InvalidOperationException ex) { return BadRequest(new { detail = ex.Message }); }

        var claimed = (bool)pinState["claimed"];
        if (!claimed)
            return Ok(new
            {
                claimed    = false,
                connected  = false,
                accountName = db.GetSetting("plex_account_name"),
            });

        var authToken = pinState["authToken"]?.ToString() ?? "";
        db.SetSetting("plex_access_token", authToken);

        string accountName;
        try { accountName = await plex.GetAccountNameAsync(authToken); }
        catch { accountName = "Plex user"; }
        db.SetSetting("plex_account_name", accountName);

        return Ok(new
        {
            claimed      = true,
            connected    = true,
            needsSelection = true,
            accountName,
        });
    }

    // ── Server / library discovery ────────────────────────────────────────────

    [HttpGet("plex/servers")]
    public async Task<IActionResult> PlexServers()
    {
        var token = db.GetSetting("plex_access_token").Trim();
        if (string.IsNullOrEmpty(token))
            return BadRequest(new { detail = "Plex sign-in is required first" });

        try
        {
            var servers = await plex.DiscoverServersAsync(token);
            return Ok(new { servers });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"Plex server discovery failed: {ex.Message}" });
        }
    }

    [HttpPost("plex/libraries")]
    public async Task<IActionResult> PlexLibraries([FromBody] PlexLibrariesRequest req)
    {
        var payload = new Dictionary<string, object>();
        foreach (var server in req.Servers)
        {
            var serverId  = server.GetValueOrDefault("id", "")?.ToString()?.Trim() ?? "";
            var serverUrl = server.GetValueOrDefault("url", "")?.ToString()?.Trim() ?? "";
            var urls      = server.GetValueOrDefault("urls") is System.Text.Json.JsonElement je
                ? je.EnumerateArray().Select(u => u.GetString() ?? "").Where(u => !string.IsNullOrEmpty(u)).ToList()
                : new List<string>();
            var token     = server.GetValueOrDefault("token", "")?.ToString()?.Trim() ?? "";

            if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(token))
                continue;

            var candidates = urls.Prepend(serverUrl).Distinct().ToList();
            try
            {
                payload[serverId] = await plex.ListLibrariesAsync(candidates, token);
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { detail = $"Failed to list libraries for {serverId}: {ex.Message}" });
            }
        }
        return Ok(new { libraries = payload });
    }

    // ── Save selection ────────────────────────────────────────────────────────

    [HttpPost("plex/selection")]
    public IActionResult SaveSelection([FromBody] PlexSelectionRequest req)
    {
        if (req.Servers == null || req.Servers.Count == 0)
            return BadRequest(new { detail = "Select at least one Plex server" });

        var total = req.SelectedLibraries?.Values.Sum(v => v.Count) ?? 0;
        if (total == 0)
            return BadRequest(new { detail = "Select at least one movie library" });

        db.SetPlexServers(req.Servers);
        db.SetSelectedLibraries(req.SelectedLibraries ?? []);
        db.SetPathMappings(req.PathMappings ?? []);
        db.SetLibraryPaths(req.LibraryPaths ?? []);

        var primary = req.Servers[0];
        db.SetSetting("plex_server_name", primary.GetValueOrDefault("name", "")?.ToString() ?? "");
        db.SetSetting("plex_server_url",  primary.GetValueOrDefault("url",  "")?.ToString() ?? "");
        db.SetSetting("plex_server_token",primary.GetValueOrDefault("token","")?.ToString() ?? "");
        db.MarkSetupComplete();

        return Ok(SetupPayload());
    }

    // ── Logout ───────────────────────────────────────────────────────────────

    [HttpPost("plex/logout")]
    public IActionResult PlexLogout()
    {
        db.SetSetting("plex_access_token", "");
        db.SetSetting("plex_account_name", "");
        db.SetSetting("setup_complete", "");
        return Ok(new { success = true });
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [HttpPost("reset")]
    public IActionResult Reset()
    {
        db.ResetAppState();
        return Ok(SetupPayload());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private object SetupPayload()
    {
        var plexConnected = !string.IsNullOrEmpty(db.GetSetting("plex_access_token").Trim());
        var selectedServers = db.GetPlexServers();
        var selectedLibraries = db.GetSelectedLibraries();
        var libCount = selectedLibraries.Values.Sum(v => v.Count);

        return new
        {
            setupComplete    = db.IsSetupComplete() && libCount > 0,
            plexConnected,
            plexAccountName  = db.GetSetting("plex_account_name"),
            selectedServers,
            selectedLibraries,
            pathMappings     = db.GetPathMappings(),
            libraryPaths     = db.GetLibraryPaths(),
        };
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record PlexLoginRequest(string? ForwardUrl);

public class PlexLibrariesRequest
{
    public List<Dictionary<string, object?>> Servers { get; set; } = [];
}

public class PlexSelectionRequest
{
    public List<Dictionary<string, object?>> Servers         { get; set; } = [];
    public Dictionary<string, List<string>>  SelectedLibraries { get; set; } = [];
    public List<Dictionary<string, string>>  PathMappings     { get; set; } = [];
    public List<string>                      LibraryPaths     { get; set; } = [];
}
