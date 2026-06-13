using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api")]
public class PosterController(
    Database db, PosterUrlSigner signer, IHttpClientFactory httpFactory, ILogger<PosterController> log)
    : ControllerBase
{
    // Streams a movie's Plex thumbnail through the server so the Plex access token is
    // never placed in a client-visible URL. This route is exempt from bearer auth (an
    // <img> can't send an Authorization header) and instead self-authenticates via the
    // signed, expiring query string produced by PosterUrlSigner.
    [HttpGet("poster")]
    public async Task<IActionResult> Get([FromQuery] string id, [FromQuery] long exp, [FromQuery] string sig)
    {
        if (string.IsNullOrEmpty(id) || !signer.Verify(id, exp, sig, DateTimeOffset.UtcNow))
            return Unauthorized();

        var movie = db.GetMovie(id);
        var serverId = movie?.GetValueOrDefault("plexServerId")?.ToString() ?? "";
        var ratingKey = movie?.GetValueOrDefault("plexRatingKey")?.ToString() ?? "";
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(ratingKey))
            return NotFound();

        if (!db.GetPlexServersDict().TryGetValue(serverId, out var srv))
            return NotFound();

        var thumbUrl = $"{srv.Url}/library/metadata/{ratingKey}/thumb?X-Plex-Token={srv.Token}";
        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await http.GetAsync(thumbUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return NotFound();

            using var buffer = new MemoryStream();
            await StreamLimits.CopyWithLimitAsync(
                await resp.Content.ReadAsStreamAsync(), buffer, StreamLimits.MaxPosterBytes);

            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(buffer.ToArray(), contentType);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Poster fetch failed for {Id}", id);
            return NotFound();
        }
    }
}
