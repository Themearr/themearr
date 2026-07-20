using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api")]
public class PosterController(
    Database db, PosterUrlSigner signer, LibrarySourceResolver sources, ILogger<PosterController> log)
    : ControllerBase
{
    // Streams a movie's poster through the server so the source's credentials are
    // never placed in a client-visible URL. This route is exempt from bearer auth (an
    // <img> can't send an Authorization header) and instead self-authenticates via the
    // signed, expiring query string produced by PosterUrlSigner.
    // Poster grid thumbnails: clamp to a small width so we never proxy multi-MB
    // full-resolution artwork for a tiny card. 300px covers retina grid cells.
    private const int DefaultWidth = 300;
    private const int MaxWidth = 600;

    [HttpGet("poster")]
    public async Task<IActionResult> Get(
        [FromQuery] string id, [FromQuery] long exp, [FromQuery] string sig, [FromQuery] int? w = null)
    {
        if (string.IsNullOrEmpty(id) || !signer.Verify(id, exp, sig, DateTimeOffset.UtcNow))
            return Unauthorized();

        var movie = db.GetMovie(id);
        var source = sources.Active;
        // source_ref is opaque outside its own source, so the source fetches its own poster.
        if (movie?.GetValueOrDefault("source")?.ToString() != source.Name) return NotFound();

        var width = Math.Clamp(w ?? DefaultWidth, 40, MaxWidth);
        var sourceRef = movie.GetValueOrDefault("sourceRef")?.ToString() ?? "";

        try
        {
            await using var stream = await source.FetchPosterAsync(sourceRef, width, HttpContext.RequestAborted);
            if (stream is null) return NotFound();

            using var buffer = new MemoryStream();
            await StreamLimits.CopyWithLimitAsync(stream, buffer, StreamLimits.MaxPosterBytes);
            buffer.Position = 0;

            Response.Headers.CacheControl = "private, max-age=86400";
            return File(buffer.ToArray(), "image/jpeg");
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Poster fetch failed for {Id}", LogSanitizer.Clean(id));
            return NotFound();
        }
    }
}
