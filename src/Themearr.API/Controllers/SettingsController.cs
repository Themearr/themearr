using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Themearr.API.Data;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController(Database db) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        selectedServers   = db.GetPlexServers(),
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

    [HttpPost]
    public IActionResult Save([FromBody] SettingsPayload req)
    {
        db.SetPlexServers(req.SelectedServers);
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
            db.SetSetting("plex_server_token", p.GetValueOrDefault("token", "")?.ToString() ?? "");
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
