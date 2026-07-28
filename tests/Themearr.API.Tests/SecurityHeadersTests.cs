using Microsoft.AspNetCore.Http;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// The CSP has to be tight enough to be worth having and loose enough that the app's own
/// features work. It previously blocked three of them, so each allowance below is pinned
/// to the specific feature that needs it — and the tight defaults are pinned too, so
/// loosening one directive can't quietly loosen the rest.
/// </summary>
public class SecurityHeadersTests
{
    private static string Csp()
    {
        var ctx = new DefaultHttpContext();
        SecurityHeaders.Apply(ctx.Response.Headers);
        return ctx.Response.Headers["Content-Security-Policy"].ToString();
    }

    /// <summary>
    /// The theme preview fetches the audio with an auth header and plays it via
    /// URL.createObjectURL, so the source is a blob: URL. Without this the player loads
    /// but sits at 0:00.
    /// </summary>
    [Fact]
    public void Media_src_allows_blob_for_the_theme_preview_player()
    {
        var csp = Csp();
        var mediaSrc = csp.Split(';').Single(d => d.Trim().StartsWith("media-src"));
        Assert.Contains("blob:", mediaSrc);
        Assert.Contains("'self'", mediaSrc);
    }

    /// <summary>
    /// YouTube search results are rendered as &lt;img&gt; straight from YouTube's CDN.
    /// The URL is supplied by YouTube, not built by us, so it can come from a shard host
    /// (i9.ytimg.com) — hence the wildcard rather than a single pinned host.
    /// </summary>
    [Fact]
    public void Img_src_allows_youtube_thumbnail_cdn()
    {
        var imgSrc = Csp().Split(';').Single(d => d.Trim().StartsWith("img-src"));
        Assert.Contains("https://*.ytimg.com", imgSrc);
        Assert.Contains("'self'", imgSrc);
        Assert.Contains("data:", imgSrc);
    }

    /// <summary>
    /// Issue #27 also reported Google Fonts being blocked, but this app has never loaded a
    /// web font — it uses a system font stack, and fonts.googleapis.com appears nowhere in
    /// the source or git history. Allowing it would loosen the header for a request the app
    /// never makes, so this test exists to stop that being "fixed" back in.
    /// </summary>
    [Fact]
    public void No_external_font_or_style_origins_are_allowed()
    {
        var csp = Csp();
        Assert.DoesNotContain("fonts.googleapis.com", csp);
        Assert.DoesNotContain("fonts.gstatic.com", csp);
    }

    [Fact]
    public void The_restrictive_defaults_are_still_in_place()
    {
        var csp = Csp();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.Contains("form-action 'self'", csp);
    }

    /// <summary>Nothing may load in an iframe, and no origin may be inferred from content.</summary>
    [Fact]
    public void The_other_security_headers_are_applied()
    {
        var ctx = new DefaultHttpContext();
        SecurityHeaders.Apply(ctx.Response.Headers);

        Assert.Equal("nosniff",     ctx.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY",        ctx.Response.Headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", ctx.Response.Headers["Referrer-Policy"]);
    }
}
