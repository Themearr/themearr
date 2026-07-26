using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController(Database db, RadarrLibrarySource radarr, PlexLibrarySource plex, IApiKeyStore keys) : ControllerBase
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

        if (source == "radarr" && string.IsNullOrWhiteSpace(payload.Url))
            return BadRequest(new { detail = "Radarr URL cannot be empty." });

        // A blank URL or key normally means "keep what you had" — e.g. a Plex save submits
        // neither, and must not wipe Radarr's stored config out from under it. But a blank
        // key may only fall back to the stored one when the submitted URL is the one that
        // key belongs to: the UI never receives the stored key back, so pairing a blank key
        // with a *different* URL isn't "leave it as-is" — it's "no key was ever entered for
        // this server". Falling back here would have the server ship the real key, in an
        // X-Api-Key header, to whatever host the caller just named — and this endpoint
        // accepts the very API key credential that's meant to be pasted into Radarr, so an
        // authenticated caller could otherwise make the key exfiltrate itself. Same rule as
        // TestRadarr and Database.SetPlexServersMergingTokens (see UrlsMatch below).
        var storedUrl = db.GetSetting("radarr_url", "").Trim().TrimEnd('/');
        var storedKey = db.GetSetting("radarr_api_key", "");
        var submittedUrl = (payload.Url ?? "").Trim().TrimEnd('/');
        var urlIsChanging = !string.IsNullOrWhiteSpace(submittedUrl) &&
                             !string.IsNullOrEmpty(storedUrl) &&
                             !UrlsMatch(submittedUrl, storedUrl);

        if (string.IsNullOrWhiteSpace(payload.ApiKey))
        {
            if (urlIsChanging)
                return BadRequest(new { detail = "Enter the API key for the new Radarr server." });
            if (source == "radarr" && string.IsNullOrWhiteSpace(storedKey))
                return BadRequest(new { detail = "Radarr API key cannot be empty." });
        }

        db.SetSetting("library_source", source);
        if (!string.IsNullOrWhiteSpace(payload.Url))
            db.SetSetting("radarr_url", payload.Url.Trim().TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
            db.SetSetting("radarr_api_key", payload.ApiKey.Trim());

        return Ok(new { source, configured = !string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")) });
    }

    // Ordinal comparison after trimming a single trailing slash — same rule as
    // Database.UrlsMatch, which guards the equivalent Plex-token re-attachment case.
    // Enough to treat "http://host:7878" and "http://host:7878/" as the same server
    // without being lenient about anything that would actually change the destination.
    private static bool UrlsMatch(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

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

    // ── Plex server URL (manual override) ──────────────────────────────────────
    // Both endpoints are bearer-only: each sends or binds the stored Plex token to an
    // operator-supplied host, so the externally-held API key must not reach them (same
    // gate as apikey management above).
    private IActionResult PlexUrlForbidden() => StatusCode(StatusCodes.Status403Forbidden,
        new { detail = "Changing the Plex server URL requires the access token, not the API key." });

    [HttpPost("plex/test")]
    [Consumes("application/json")]
    public async Task<IActionResult> TestPlex([FromBody] PlexUrlPayload payload, CancellationToken ct)
    {
        if (!AuthenticatedWithBearerToken) return PlexUrlForbidden();

        var url = NormalizePlexUrl(payload.Url);
        if (url is null)
            return BadRequest(new { detail = "Enter a valid server address, e.g. http://192.168.1.50:32400." });

        // Probe with the STORED token for that server — never a token from the request body.
        if (!db.GetPlexServersDict().TryGetValue(payload.ServerId ?? "", out var srv))
            return NotFound(new { detail = "That Plex server is not connected." });

        var reason = await plex.ProbeAsync(url, srv.Token, ct);
        return Ok(new { ok = reason is null, detail = reason ?? "Plex is reachable." });
    }

    [HttpPost("plex/server")]
    [Consumes("application/json")]
    public IActionResult SavePlexUrl([FromBody] PlexUrlPayload payload)
    {
        if (!AuthenticatedWithBearerToken) return PlexUrlForbidden();

        var url = NormalizePlexUrl(payload.Url);
        if (url is null)
            return BadRequest(new { detail = "Enter a valid server address, e.g. http://192.168.1.50:32400." });

        if (!db.UpdatePlexServerUrl(payload.ServerId ?? "", url))
            return NotFound(new { detail = "That Plex server is not connected." });

        return Ok(new { selectedServers = db.GetPlexServersRedacted() });
    }

    public record PlexUrlPayload(string? ServerId, string? Url);

    // Normalizes a user-entered Plex address: trims, defaults to http:// when no scheme is
    // given (Plex local is http on :32400), requires an http(s) URL with a host, and strips a
    // trailing slash. Returns null when the input can't be a valid server address. Private and
    // loopback hosts are allowed on purpose — Plex servers are private, like the discovered URLs.
    private static string? NormalizePlexUrl(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://")) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return text.TrimEnd('/');
    }

    // ── Themearr's own API key ───────────────────────────────────────────────

    // The API key must not be able to read or regenerate itself: otherwise whoever holds
    // it could re-issue it forever and lock the operator out of their own integration.
    // Only the master bearer token may read or regenerate it. This is the one carve-out —
    // the API key otherwise authenticates like the bearer token everywhere else, including
    // endpoints that overwrite the Radarr key or Plex token; see the README's API key section.
    private bool AuthenticatedWithBearerToken => HttpContext.AuthenticatedWithBearerToken();

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
