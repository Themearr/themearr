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

        download.Start(showId, $"https://www.youtube.com/watch?v={req.VideoId}", "show");
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

        download.Start(showId, req.Url, "show");
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

        var deleted = ThemeFiles.DeleteThemes(folder);

        // Reset the stored status so the column stays honest and the auto-download worker's
        // stored-status pre-filter re-adopts this show — same contract as the movie endpoint.
        if (deleted)
        {
            db.SetShowStatus(showId, "pending");

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
        var info = new FileInfo(themeFile);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(themeFile, ThemeFiles.ContentTypeFor(themeFile),
            info.LastWriteTimeUtc, etag, enableRangeProcessing: true);
    }
}

public record ShowDownloadRequest(string VideoId);
public record ShowDownloadUrlRequest(string Url);
