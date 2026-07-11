using System.Web;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexImageUrlTests
{
    [Fact]
    public void Transcode_returnsPhotoTranscodeUrl_withResizeParams()
    {
        var url = PlexImageUrl.Transcode("http://plex:32400", "12345", "TOK", 300, 450);

        Assert.StartsWith("http://plex:32400/photo/:/transcode?", url);
        var q = HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("300", q["width"]);
        Assert.Equal("450", q["height"]);
        Assert.Equal("TOK", q["X-Plex-Token"]);
    }

    [Fact]
    public void Transcode_embedsEncodedInnerThumbUrl()
    {
        var url = PlexImageUrl.Transcode("http://plex:32400", "12345", "TOK", 300, 450);

        // The inner image path must be percent-encoded so it rides as a single query value.
        Assert.Contains("url=%2Flibrary%2Fmetadata%2F12345%2Fthumb", url);

        var q = HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("/library/metadata/12345/thumb?X-Plex-Token=TOK", q["url"]);
    }

    [Fact]
    public void Transcode_trimsTrailingSlashOnBaseUrl()
    {
        var url = PlexImageUrl.Transcode("http://plex:32400/", "1", "T", 300, 450);
        Assert.StartsWith("http://plex:32400/photo/:/transcode?", url);
        Assert.DoesNotContain("32400//photo", url);
    }
}
