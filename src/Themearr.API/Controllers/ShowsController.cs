using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

/// <summary>
/// The shows API. A deliberate parallel of <see cref="MoviesController"/> rather than a
/// media-type-generic controller: branching every movie route by media type risks changing
/// movie behaviour, and the logic genuinely worth sharing already lives in
/// <see cref="ThemeFiles"/> and <see cref="DownloadService"/>.
///
/// Unlike the movie routes' legacy shape, everything here is namespaced under
/// <c>/api/shows</c> — except posters, which must sit under the public <c>/api/poster</c>
/// prefix (see <see cref="PosterController.GetShow"/>).
/// </summary>
[ApiController]
[Route("api/shows")]
public class ShowsController(
    Database db, YoutubeService youtube, DownloadService download, PosterUrlSigner posterSigner,
    ILogger<ShowsController> log,
    // Optional so the existing test constructions keep compiling, and null-safe because
    // the delete-side refresh is best-effort anyway — same pattern as DownloadService.
    PlexService? plex = null) : ControllerBase
{
    [HttpGet]
    public IActionResult ListShows()
    {
        var shows = db.GetAllShows();
        var posterExpiry = DateTimeOffset.UtcNow.AddHours(12);
        foreach (var show in shows)
        {
            var id = show.GetValueOrDefault("id")?.ToString() ?? "";

            // Shows come only from Plex, so unlike movies there is no active-source check
            // here — a show with a source_ref always has a Plex poster to sign a URL for.
            var hasPoster = !string.IsNullOrEmpty(show.GetValueOrDefault("sourceRef")?.ToString());

            show["posterUrl"] = (!string.IsNullOrEmpty(id) && hasPoster)
                ? posterSigner.ShowPosterPath(id, posterExpiry)
                : null;
        }
        return Ok(shows);
    }

    [HttpGet("{showId}/search")]
    public async Task<IActionResult> SearchYoutube(string showId, [FromQuery] string? q = null)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var title = show["title"]?.ToString() ?? "";

        // Year-free by default: a show spans years, so including one biases the search
        // toward a single season's upload. Same query the auto-download worker uses, so a
        // manual search and an automatic one agree on what they are looking for.
        var query = !string.IsNullOrWhiteSpace(q) ? q : ShowAutoDownloadService.BuildQuery(title);

        try
        {
            var results = await youtube.SearchAsync(query, maxResults: 8, title: title);
            return Ok(new { show, results });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }
    }

    // A show whose status is 'plexTheme' is NOT blocked here. That status is informational
    // — it tells the UI why the show is being skipped by default — and the UI is expected
    // to require an explicit "download anyway". The API accepting it is what makes the
    // override possible at all.
    [HttpPost("{showId}/download")]
    [Consumes("application/json")]
    public IActionResult Download(string showId, [FromBody] ShowDownloadRequest req)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });

        if (download.DownloadBlockedReason(isProviderUrl: true) is { } notReady)
        {
            log.LogWarning("Show download for {ShowId} blocked: {Reason}", LogSanitizer.Clean(showId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        // A human picked this specific video from the search results — "manual".
        if (!download.Start(showId, $"https://www.youtube.com/watch?v={req.VideoId}", "show", "manual"))
            return Conflict(new { detail = "A download is already running for this item. Wait for it to finish before starting another." });
        return Accepted(new { started = true, showId });
    }

    [HttpPost("{showId}/download-url")]
    [Consumes("application/json")]
    public IActionResult DownloadUrl(string showId, [FromBody] ShowDownloadUrlRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) || !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
            return BadRequest(new { detail = "Invalid URL" });

        if (uri.Scheme is not ("http" or "https"))
            return BadRequest(new { detail = "Only http and https URLs are supported." });

        if (HostGuard.IsPrivateOrLoopback(uri.Host))
            return BadRequest(new { detail = "Refusing to download from a private or loopback address." });

        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });

        // A pasted YouTube URL still goes through the provider, so pre-flight it
        // (config + quota cooldown). Direct URLs are not gated.
        if (download.DownloadBlockedReason(DownloadService.IsProviderUrl(req.Url)) is { } notReady)
        {
            log.LogWarning("Show download-url for {ShowId} blocked: {Reason}", LogSanitizer.Clean(showId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        // A human pasted this URL directly — "manual".
        if (!download.Start(showId, req.Url, "show", "manual"))
            return Conflict(new { detail = "A download is already running for this item. Wait for it to finish before starting another." });
        return Accepted(new { started = true, showId });
    }

    [HttpGet("{showId}/download/status")]
    public IActionResult DownloadStatus(string showId) => Ok(download.GetStatus(showId, "show"));

    [HttpPost("{showId}/ignore")]
    public IActionResult IgnoreShow(string showId)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        db.SetShowIgnored(showId, true);
        return Ok(new { ignored = true });
    }

    [HttpPost("{showId}/unignore")]
    public IActionResult UnignoreShow(string showId)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        db.SetShowIgnored(showId, false);
        return Ok(new { ignored = false });
    }

    // The two re-check resolutions a human can make on a flagged show — mirrors
    // MoviesController.RecheckKeep/RecheckReplace, including why the existing reason is
    // preserved rather than cleared (see that comment).
    [HttpPost("{showId}/recheck/keep")]
    public IActionResult RecheckKeep(string showId)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        var reason = db.GetFlaggedItems().FirstOrDefault(i => i.MediaType == "show" && i.Id == showId)?.Reason;
        db.SetRecheck(showId, "show", "kept", reason);
        return Ok(new { state = "kept" });
    }

    [HttpPost("{showId}/recheck/replace")]
    public IActionResult RecheckReplace(string showId)
    {
        if (db.GetShow(showId) == null) return NotFound(new { detail = "Show not found" });
        var reason = db.GetFlaggedItems().FirstOrDefault(i => i.MediaType == "show" && i.Id == showId)?.Reason;
        db.SetRecheck(showId, "show", "queued", reason);
        return Ok(new { state = "queued" });
    }

    // New in the re-check feature (a scoped amendment to the spec, not scope creep): the
    // shows adapter today composes auto-download client-side out of search + the manual
    // download endpoint, so a matcher-chosen show theme would be recorded picked_by =
    // 'manual' and could never be auto-replaced by a later re-check. Recording provenance
    // here instead of trusting a client-supplied flag is the only way to make it honest,
    // and it closes a movie/show parity gap that predates this feature — mirrors
    // MoviesController.AutoDownload (MoviesController.cs:127-166).
    [HttpPost("auto-download/{showId}")]
    public async Task<IActionResult> AutoDownload(string showId)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var title = show["title"]?.ToString() ?? "";
        // Year-free, same as the manual search above (ShowsController.cs:55) — a show
        // spans years, so including one biases the search toward a single season.
        var query = ShowAutoDownloadService.BuildQuery(title);

        List<Dictionary<string, object?>> results;
        try
        {
            results = await youtube.SearchAsync(query, maxResults: 8, title: title);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
            return UnprocessableEntity(new { detail = "No suitable match found — please select manually." });

        if (download.DownloadBlockedReason(isProviderUrl: true) is { } notReady)
        {
            log.LogWarning("Show auto-download for {ShowId} blocked: {Reason}", LogSanitizer.Clean(showId), notReady);
            return UnprocessableEntity(new { detail = notReady });
        }

        var videoId = best["videoId"]?.ToString() ?? "";
        var url     = $"https://www.youtube.com/watch?v={videoId}";
        // A person clicked the button, but the matcher picked the video — "auto" is
        // about who chose the result, not who triggered the request (mirrors
        // MoviesController.AutoDownload's identical comment, MoviesController.cs:161-163).
        if (!download.Start(showId, url, "show", "auto", best["score"] as int?))
            return Conflict(new { detail = "A download is already running for this item. Wait for it to finish before starting another." });

        return Accepted(new { started = true, showId, videoId, videoTitle = best["title"] });
    }

    [HttpDelete("{showId}/theme")]
    public IActionResult DeleteTheme(string showId)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var folder = show["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return BadRequest(new { detail = "Show has no folder" });

        // Confine deletes to the configured library roots (see DownloadService).
        var roots = db.GetLibraryPaths();
        if (roots.Count > 0 && !ThemeFiles.IsWithinRoots(folder, roots))
            return BadRequest(new { detail = "Refusing to delete outside the configured library roots." });

        bool deleted;
        try { deleted = download.DeleteTheme(showId, "show"); }
        catch (UnauthorizedAccessException ex) { return BadRequest(new { detail = ex.Message }); }

        // The transaction resets pending/re-check state with deletion so a concurrent
        // automatic replacement cannot recreate a theme after this action completes.
        if (deleted)
        {
            // Plex keeps playing its cached theme until the item is refreshed — the same
            // staleness issue #45 fixed for downloads, in the delete direction. Fire and
            // forget: this action's signature is pinned synchronous, and a DELETE must
            // not wait out PlexService.RefreshTimeout on a wedged server. The helper
            // never faults, so discarding the task can't drop an exception.
            _ = plex?.TryRefreshItemMetadataAsync(
                show.GetValueOrDefault("source")?.ToString(),
                show.GetValueOrDefault("sourceRef")?.ToString(), log, showId);
        }

        return Ok(new { deleted });
    }

    [HttpGet("{showId}/theme/audio")]
    public IActionResult GetThemeAudio(string showId)
    {
        var show = db.GetShow(showId);
        if (show == null) return NotFound(new { detail = "Show not found" });

        var folder = show["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return NotFound(new { detail = "No folder" });

        var themeFile = ThemeFiles.FindThemeFile(folder);
        if (themeFile == null) return NotFound(new { detail = "No theme file" });

        // ETag + Last-Modified so repeated visits don't re-download the same theme file.
        // Framework honours If-None-Match / If-Modified-Since and returns 304 automatically.
        // ThemeStat, not a bare FileInfo: the theme can vanish between FindThemeFile
        // above and the stat — a re-download whose container changed replaces across
        // names (issue #48) — and one unlucky request must degrade to 404, never 500.
        if (ThemeFiles.ThemeStat(themeFile) is not { } stat)
            return NotFound(new { detail = "No theme file" });
        var etag = new EntityTagHeaderValue($"\"{stat.Length:x}-{stat.LastWriteTimeUtc.Ticks:x}\"");
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(themeFile, ThemeFiles.ContentTypeFor(themeFile),
            stat.LastWriteTimeUtc, etag, enableRangeProcessing: true);
    }
}

public record ShowDownloadRequest(string VideoId);
public record ShowDownloadUrlRequest(string Url);
