namespace Themearr.API.Services;

/// <summary>
/// Builds Plex image URLs. Grid posters go through Plex's photo transcoder so the
/// server (and browser) fetch a small resized thumbnail (~30 KB) instead of the
/// full-resolution artwork (often 1–2 MB) — a large library otherwise moves tens of
/// MB of poster data on every page load.
/// </summary>
public static class PlexImageUrl
{
    public static string Transcode(string baseUrl, string ratingKey, string token, int width, int height)
    {
        var root = baseUrl.TrimEnd('/');
        var inner = $"/library/metadata/{ratingKey}/thumb?X-Plex-Token={token}";
        var encodedInner = Uri.EscapeDataString(inner);
        return $"{root}/photo/:/transcode?width={width}&height={height}&minSize=1&upscale=0" +
               $"&url={encodedInner}&X-Plex-Token={Uri.EscapeDataString(token)}";
    }
}
