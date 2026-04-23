using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api")]
public class MoviesController(Database db, YoutubeService youtube, DownloadService download) : ControllerBase
{
    [HttpGet("movies")]
    public IActionResult ListMovies()
    {
        var movies = db.GetAllMovies();
        var serverMap = db.GetPlexServersDict();
        foreach (var movie in movies)
        {
            var sid = movie.GetValueOrDefault("plexServerId")?.ToString() ?? "";
            var rk  = movie.GetValueOrDefault("plexRatingKey")?.ToString() ?? "";
            movie["posterUrl"] = (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(rk) && serverMap.TryGetValue(sid, out var srv))
                ? $"{srv.Url}/library/metadata/{rk}/thumb?X-Plex-Token={srv.Token}"
                : null;
        }
        return Ok(movies);
    }

    [HttpGet("search/{movieId}")]
    public async Task<IActionResult> SearchYoutube(string movieId, [FromQuery] string? q = null)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var title   = movie["title"]?.ToString() ?? "";
        var yearObj = movie["year"];
        var year    = yearObj?.ToString() ?? "";
        var yearInt = yearObj is int y ? y : (int?)null;
        var query   = !string.IsNullOrWhiteSpace(q) ? q : $"{title} {year} theme".Trim();

        try
        {
            var results = await youtube.SearchAsync(query, maxResults: 8, movieTitle: title, movieYear: yearInt);
            return Ok(new { movie, results });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }
    }

    [HttpDelete("movies/{movieId}/theme")]
    public IActionResult DeleteTheme(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var folder = movie["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder))
            return BadRequest(new { detail = "Movie has no folder" });

        var deleted = false;
        foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
        {
            System.IO.File.Delete(f);
            deleted = true;
        }
        return Ok(new { deleted });
    }

    [HttpPost("movies/{movieId}/ignore")]
    public IActionResult IgnoreMovie(string movieId)
    {
        if (db.GetMovie(movieId) == null) return NotFound(new { detail = "Movie not found" });
        db.SetMovieIgnored(movieId, true);
        return Ok(new { ignored = true });
    }

    [HttpPost("movies/{movieId}/unignore")]
    public IActionResult UnignoreMovie(string movieId)
    {
        if (db.GetMovie(movieId) == null) return NotFound(new { detail = "Movie not found" });
        db.SetMovieIgnored(movieId, false);
        return Ok(new { ignored = false });
    }

    [HttpGet("movies/{movieId}/theme/audio")]
    public IActionResult GetThemeAudio(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var folder = movie["folderName"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(folder)) return NotFound(new { detail = "No folder" });

        var themeFile = Directory.EnumerateFiles(folder, "theme.*")
            .FirstOrDefault(f => Path.GetExtension(f) is not (".part" or ".ytdl"));
        if (themeFile == null) return NotFound(new { detail = "No theme file" });

        var contentType = Path.GetExtension(themeFile).ToLower() switch
        {
            ".mp3"  => "audio/mpeg",
            ".m4a"  => "audio/mp4",
            ".ogg"  => "audio/ogg",
            ".opus" => "audio/opus",
            ".webm" => "audio/webm",
            ".flac" => "audio/flac",
            _       => "audio/mpeg",
        };

        // ETag + Last-Modified so repeated visits don't re-download the same theme file.
        // Framework honours If-None-Match / If-Modified-Since and returns 304 automatically.
        var info = new FileInfo(themeFile);
        var etag = new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(themeFile, contentType, info.LastWriteTimeUtc, etag, enableRangeProcessing: true);
    }

    [HttpPost("auto-download/{movieId}")]
    public async Task<IActionResult> AutoDownload(string movieId)
    {
        var movie = db.GetMovie(movieId);
        if (movie == null) return NotFound(new { detail = "Movie not found" });

        var title   = movie["title"]?.ToString() ?? "";
        var yearObj = movie["year"];
        var year    = yearObj?.ToString() ?? "";
        var yearInt = yearObj is int y ? y : (int?)null;
        var query   = $"{title} {year} theme".Trim();

        List<Dictionary<string, object?>> results;
        try
        {
            results = await youtube.SearchAsync(query, maxResults: 8, movieTitle: title, movieYear: yearInt);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { detail = $"YouTube search error: {ex.Message}" });
        }

        var best = results.FirstOrDefault(r => r.GetValueOrDefault("bestMatch") is true);
        if (best == null)
            return UnprocessableEntity(new { detail = "No suitable match found — please select manually." });

        var videoId = best["videoId"]?.ToString() ?? "";
        var url     = $"https://www.youtube.com/watch?v={videoId}";
        download.Start(movieId, url);

        return Accepted(new { started = true, movieId, videoId, videoTitle = best["title"] });
    }

    [HttpPost("download")]
    public IActionResult Download([FromBody] DownloadRequest req)
    {
        if (db.GetMovie(req.MovieId) == null)
            return NotFound(new { detail = "Movie not found" });

        var url = $"https://www.youtube.com/watch?v={req.VideoId}";
        download.Start(req.MovieId, url);
        return Accepted(new { started = true, movieId = req.MovieId });
    }

    [HttpPost("download-url")]
    public IActionResult DownloadUrl([FromBody] DownloadUrlRequest req)
    {
        if (string.IsNullOrEmpty(req.Url) ||
            !Uri.TryCreate(req.Url, UriKind.Absolute, out var uri))
            return BadRequest(new { detail = "Invalid URL" });

        if (uri.Scheme is not ("http" or "https"))
            return BadRequest(new { detail = "Only http and https URLs are supported." });

        if (IsPrivateOrLoopbackHost(uri.Host))
            return BadRequest(new { detail = "Refusing to download from a private or loopback address." });

        if (db.GetMovie(req.MovieId) == null)
            return NotFound(new { detail = "Movie not found" });

        download.Start(req.MovieId, req.Url);
        return Accepted(new { started = true, movieId = req.MovieId });
    }

    // ── SSRF guard ────────────────────────────────────────────────────────────
    // Blocks IP literals and resolved hostnames that fall into private, loopback,
    // link-local, or IPv6-unique-local ranges. Best-effort: a TOCTOU between DNS
    // resolution here and the actual HTTP GET remains, but this rejects the easy
    // cases (127.0.0.1, 10.x, 169.254.x, ::1, localhost).
    private static bool IsPrivateOrLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
            addresses = [literal];
        else
            try { addresses = Dns.GetHostAddresses(host); }
            catch { return true; } // fail-closed on DNS errors

        return addresses.Any(IsPrivateAddress);
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16, 100.64.0.0/10 (CGNAT), 0.0.0.0/8
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            if (b[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var b = ip.GetAddressBytes();
            // fc00::/7 unique-local
            if ((b[0] & 0xFE) == 0xFC) return true;
        }
        return false;
    }

    [HttpGet("download/status/{movieId}")]
    public IActionResult DownloadStatus(string movieId)
    {
        return Ok(download.GetStatus(movieId));
    }
}

public record DownloadRequest(string MovieId, string VideoId);
public record DownloadUrlRequest(string MovieId, string Url);
