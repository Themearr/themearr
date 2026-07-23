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
    [Consumes("application/json")]
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
    [Consumes("application/json")]
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
    [Consumes("application/json")]
    public IActionResult SaveSelection([FromBody] PlexSelectionRequest req)
    {
        if (req.Servers == null || req.Servers.Count == 0)
            return BadRequest(new { detail = "Select at least one Plex server" });

        var total = req.SelectedLibraries?.Values.Sum(v => v.Count) ?? 0;
        if (total == 0)
            return BadRequest(new { detail = "Select at least one movie library" });

        // Merge so a re-save that omits the redacted token keeps the stored one.
        db.SetPlexServersMergingTokens(req.Servers);
        db.SetSelectedLibraries(req.SelectedLibraries ?? []);
        db.SetPathMappings(req.PathMappings ?? []);
        db.SetLibraryPaths(req.LibraryPaths ?? []);

        db.MarkSetupComplete();

        return Ok(SetupPayload());
    }

    // ── Non-Plex completion ──────────────────────────────────────────────────

    /// <summary>
    /// Marks setup complete for an install that is not using Plex. The Plex branch
    /// finishes via plex/selection; a Radarr user never touches those endpoints.
    /// </summary>
    [HttpPost("complete")]
    public IActionResult Complete()
    {
        if (db.GetSetting("library_source", "plex") != "radarr")
            return BadRequest(new { detail = "Only a non-Plex library source can complete setup this way." });
        if (string.IsNullOrWhiteSpace(db.GetSetting("radarr_url", "")) ||
            string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")))
            return BadRequest(new { detail = "Configure Radarr before completing setup." });

        db.MarkSetupComplete();
        return Ok(new { setupComplete = true });
    }

    // ── Logout ───────────────────────────────────────────────────────────────

    [HttpPost("plex/logout")]
    public IActionResult PlexLogout()
    {
        db.SetSetting("plex_access_token", "");
        db.SetSetting("plex_account_name", "");
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
        // Plex token is write-only — never echo it back in a GET response.
        var selectedServers = db.GetPlexServersRedacted();
        var selectedLibraries = db.GetSelectedLibraries();
        var libCount = selectedLibraries.Values.Sum(v => v.Count);

        // A Radarr install never selects Plex libraries, so the library-count
        // requirement only makes sense for the Plex source — otherwise setup can
        // never be reported complete and /setup/complete becomes unobservable.
        var isPlex = db.GetSetting("library_source", "plex") == "plex";

        return new
        {
            setupComplete    = db.IsSetupComplete() && (!isPlex || libCount > 0),
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
