namespace Themearr.API.Services;

/// <summary>
/// Security headers applied to every response (static SPA and API alike).
///
/// Extracted from Program.cs so the policy is unit-testable: a CSP is only useful while it
/// is tight, and the only way to loosen it safely is to pin each allowance to the feature
/// that needs it. See <c>SecurityHeadersTests</c>.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// Posters and theme files are proxied same-origin, so almost nothing external is
    /// needed. The two exceptions are deliberate:
    ///
    /// <list type="bullet">
    /// <item><c>img-src https://*.ytimg.com</c> — YouTube search results are rendered as
    /// &lt;img&gt; directly from YouTube's thumbnail CDN. The URL is supplied by YouTube
    /// rather than constructed here, and can come from a shard host (i9.ytimg.com), so
    /// pinning a single host would break again on YouTube's whim.</item>
    /// <item><c>media-src blob:</c> — the theme preview fetches the audio with an auth
    /// header and plays it via URL.createObjectURL, which yields a blob: URL. This is the
    /// app's own same-origin bytes, not a new external trust.</item>
    /// </list>
    ///
    /// No external style or font origins: this app uses a system font stack and loads no
    /// web font. (Issue #27 reported Google Fonts being blocked; it has never used them.)
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: https://*.ytimg.com; media-src 'self' blob:; " +
        "style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public static void Apply(IHeaderDictionary h)
    {
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = ContentSecurityPolicy;
    }
}
