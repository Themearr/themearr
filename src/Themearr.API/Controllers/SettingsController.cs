using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController(Database db, RadarrLibrarySource radarr, IApiKeyStore keys) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        // Plex token is write-only — never echo it back in a GET response.
        selectedServers   = db.GetPlexServersRedacted(),
        selectedLibraries = db.GetSelectedLibraries(),
        pathMappings      = db.GetPathMappings(),
        libraryPaths      = db.GetLibraryPaths(),
        advanced = new
        {
            maxSearchDirs = int.Parse(db.GetSetting("max_search_dirs", "20000")),
            searchDepth   = int.Parse(db.GetSetting("search_depth", "4")),
        },
        autoDownload = db.GetSetting("auto_download", "false") == "true",
        autoSync     = db.GetSetting("auto_sync",     "false") == "true",
        lastAutoSyncAt = db.GetSetting("last_auto_sync_at", ""),
    });

    // [Consumes] forces a JSON content-type (and thus a CORS preflight), which — on top
    // of the header-only bearer auth — blocks simple cross-site POSTs from forging this.
    [HttpPost]
    [Consumes("application/json")]
    public IActionResult Save([FromBody] SettingsPayload req)
    {
        // Merge so a save that omits the redacted token keeps the stored one.
        db.SetPlexServersMergingTokens(req.SelectedServers);
        db.SetSelectedLibraries(req.SelectedLibraries);
        db.SetPathMappings(req.PathMappings);
        db.SetLibraryPaths(req.LibraryPaths);

        var maxDirs = Math.Clamp(req.Advanced.GetValueOrDefault("maxSearchDirs", 20000), 500, 100000);
        var depth   = Math.Clamp(req.Advanced.GetValueOrDefault("searchDepth", 4), 1, 10);
        db.SetSetting("max_search_dirs", maxDirs.ToString());
        db.SetSetting("search_depth", depth.ToString());
        db.SetSetting("auto_download", req.AutoDownload ? "true" : "false");
        db.SetSetting("auto_sync",     req.AutoSync     ? "true" : "false");

        if (req.SelectedServers.Count > 0)
        {
            var p = req.SelectedServers[0];
            db.SetSetting("plex_server_name",  p.GetValueOrDefault("name",  "")?.ToString() ?? "");
            db.SetSetting("plex_server_url",   p.GetValueOrDefault("url",   "")?.ToString() ?? "");
            // Preserve the stored token when the save omits it (redacted round-trip).
            var incomingToken = p.GetValueOrDefault("token", "")?.ToString() ?? "";
            db.SetSetting("plex_server_token",
                string.IsNullOrEmpty(incomingToken) ? db.GetPrimaryServerToken() : incomingToken);
        }
        if (req.SelectedServers.Count > 0 && req.SelectedLibraries.Values.Sum(v => v.Count) > 0)
            db.MarkSetupComplete();

        return Get();
    }

    // ── RapidAPI key ──────────────────────────────────────────────────────────

    [HttpGet("rapidapi")]
    public IActionResult GetRapidApiKey()
    {
        var key      = db.GetSetting("rapidapi_key", "");
        var username = db.GetSetting("rapidapi_username", "");
        return Ok(new { configured = !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(username) });
    }

    [HttpPost("rapidapi")]
    [Consumes("application/json")]
    public IActionResult SaveRapidApiKey([FromBody] RapidApiKeyPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Key))
            return BadRequest(new { detail = "API key cannot be empty." });
        if (string.IsNullOrWhiteSpace(payload.Username))
            return BadRequest(new { detail = "RapidAPI username cannot be empty." });
        db.SetSetting("rapidapi_key",      payload.Key.Trim());
        db.SetSetting("rapidapi_username", payload.Username.Trim());
        return Ok(new { configured = true });
    }

    [HttpDelete("rapidapi")]
    public IActionResult DeleteRapidApiKey()
    {
        db.SetSetting("rapidapi_key",      "");
        db.SetSetting("rapidapi_username", "");
        return Ok(new { configured = false });
    }

    // ── Radarr library source ────────────────────────────────────────────────

    [HttpGet("radarr")]
    public IActionResult GetRadarr() => Ok(new
    {
        source     = db.GetSetting("library_source", "plex"),
        url        = db.GetSetting("radarr_url", ""),
        // The key itself is never returned — same rule as the RapidAPI endpoint above.
        configured = !string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")),
    });

    [HttpPost("radarr")]
    [Consumes("application/json")]
    public IActionResult SaveRadarr([FromBody] RadarrPayload payload)
    {
        var source = (payload.Source ?? "plex").Trim();
        if (source is not ("plex" or "radarr"))
            return BadRequest(new { detail = "Library source must be 'plex' or 'radarr'." });

        if (source == "radarr")
        {
            if (string.IsNullOrWhiteSpace(payload.Url))
                return BadRequest(new { detail = "Radarr URL cannot be empty." });
            if (string.IsNullOrWhiteSpace(payload.ApiKey) &&
                string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")))
                return BadRequest(new { detail = "Radarr API key cannot be empty." });
        }

        db.SetSetting("library_source", source);
        // A blank URL or key means "keep what you had" — e.g. a Plex save submits neither,
        // and must not wipe Radarr's stored config out from under it.
        if (!string.IsNullOrWhiteSpace(payload.Url))
            db.SetSetting("radarr_url", payload.Url.Trim().TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
            db.SetSetting("radarr_api_key", payload.ApiKey.Trim());

        return Ok(new { source, configured = !string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")) });
    }

    [HttpPost("radarr/test")]
    [Consumes("application/json")]
    public async Task<IActionResult> TestRadarr([FromBody] RadarrPayload payload, CancellationToken ct)
    {
        // Test what the user is about to save, not what is stored, so a wrong key is
        // caught while they are still looking at the field. Probes directly against the
        // submitted values — never writes to settings, so this can't race a scheduled
        // sync or a real save that lands mid-probe (see RadarrLibrarySource.ProbeAsync).
        var url = (payload.Url ?? "").Trim().TrimEnd('/');

        string key;
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            key = payload.ApiKey.Trim();
        }
        else
        {
            // No key submitted — only fall back to the stored key when the submitted
            // URL is the one that key belongs to. Otherwise an authenticated caller
            // could make the server ship the real Radarr key to a host of their
            // choosing (the response never reveals the key, but it would still spend it).
            var storedUrl = db.GetSetting("radarr_url", "").Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(storedUrl) && string.Equals(url, storedUrl, StringComparison.OrdinalIgnoreCase))
            {
                key = db.GetSetting("radarr_api_key", "");
            }
            else
            {
                return Ok(new { ok = false, detail = "Enter the API key for this server." });
            }
        }

        var reason = await radarr.ProbeAsync(url, key, ct);
        return Ok(new { ok = reason is null, detail = reason ?? "Radarr is reachable." });
    }

    public record RadarrPayload(string? Source, string? Url, string? ApiKey);

    // ── Themearr's own API key ───────────────────────────────────────────────

    // A credential must not be able to manage credentials: the API key is for operating
    // Themearr (triggering a sync, reading status), not for administering the key itself —
    // otherwise whoever holds the API key can re-issue it forever and lock the operator
    // out of their own integration. Only the master bearer token may read or regenerate it.
    private bool AuthenticatedWithBearerToken =>
        HttpContext.Items.TryGetValue(ApiAuthMiddleware.AuthSchemeItemKey, out var scheme) &&
        (scheme as string) == ApiAuthMiddleware.BearerScheme;

    private IActionResult ApiKeyManagementForbidden() => StatusCode(StatusCodes.Status403Forbidden,
        new { detail = "Managing the API key requires the access token, not the API key." });

    /// <summary>
    /// Returns the API key in full. Unlike Radarr's key — which Themearr holds and never
    /// discloses — this one is issued to the operator to paste into an external tool, so
    /// it has to be readable.
    /// </summary>
    [HttpGet("apikey")]
    public IActionResult GetApiKey()
    {
        if (!AuthenticatedWithBearerToken) return ApiKeyManagementForbidden();

        Response.Headers.CacheControl = "no-store";
        return Ok(new { key = keys.Current });
    }

    [HttpPost("apikey/regenerate")]
    public IActionResult RegenerateApiKey()
    {
        if (!AuthenticatedWithBearerToken) return ApiKeyManagementForbidden();

        Response.Headers.CacheControl = "no-store";
        return Ok(new { key = keys.Regenerate() });
    }
}

public record RapidApiKeyPayload(string Key, string Username);

public class SettingsPayload
{
    public List<Dictionary<string, object?>> SelectedServers    { get; set; } = [];
    public Dictionary<string, List<string>>  SelectedLibraries  { get; set; } = [];
    public List<Dictionary<string, string>>  PathMappings       { get; set; } = [];
    public List<string>                      LibraryPaths       { get; set; } = [];
    public Dictionary<string, int>           Advanced           { get; set; } = [];
    public bool                              AutoDownload       { get; set; } = false;
    public bool                              AutoSync           { get; set; } = false;
}
