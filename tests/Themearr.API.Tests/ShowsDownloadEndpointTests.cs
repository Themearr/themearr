using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;

namespace Themearr.API.Tests;

public class ShowsDownloadEndpointTests
{
    [Fact]
    public void Download_404s_for_an_unknown_show()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);

        var result = ShowsControllerTests.New(db).Download("nope", new ShowDownloadRequest("vid123"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void DownloadUrl_rejects_a_private_address()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");

        var result = ShowsControllerTests.New(db)
            .DownloadUrl(id, new ShowDownloadUrlRequest("http://169.254.169.254/latest/meta-data"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DownloadUrl_rejects_a_non_http_scheme()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");

        var result = ShowsControllerTests.New(db)
            .DownloadUrl(id, new ShowDownloadUrlRequest("file:///etc/passwd"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// A Plex-themed show is informational, not blocked — the UI gates it behind an
    /// explicit "download anyway", but the API must accept it.
    /// </summary>
    [Fact]
    public void Download_is_accepted_for_a_plexTheme_show()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "PlexThemed", plexHasTheme: true);

        var result = ShowsControllerTests.New(db).Download(id, new ShowDownloadRequest("vid123"));

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public void Status_reports_not_started_for_an_untouched_show()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");

        var result = Assert.IsType<OkObjectResult>(ShowsControllerTests.New(db).DownloadStatus(id));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.False(body.GetProperty("inProgress").GetBoolean());
        Assert.False(body.GetProperty("finished").GetBoolean());
    }
}
