using Microsoft.AspNetCore.Mvc;

namespace Themearr.API.Tests;

public class ShowsThemeEndpointTests
{
    [Fact]
    public void Ignore_then_unignore_round_trips()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");
        var controller = ShowsControllerTests.New(db);

        Assert.IsType<OkObjectResult>(controller.IgnoreShow(id));
        Assert.Equal("ignored", db.GetShow(id)!["status"]);

        Assert.IsType<OkObjectResult>(controller.UnignoreShow(id));
        Assert.Equal("pending", db.GetShow(id)!["status"]);
    }

    /// <summary>
    /// Mirrors the movie contract: an in-app delete must reset stored status to 'pending'
    /// so the auto-download worker's stored-status pre-filter re-adopts the show.
    /// </summary>
    [Fact]
    public void DeleteTheme_removes_the_file_and_resets_stored_status()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");
        var folder = db.GetShow(id)!["folderName"]!.ToString()!;
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        db.SetShowStatus(id, "downloaded");

        var result = Assert.IsType<OkObjectResult>(ShowsControllerTests.New(db).DeleteTheme(id));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.True(body.GetProperty("deleted").GetBoolean());
        Assert.False(File.Exists(Path.Combine(folder, "theme.mp3")));
        Assert.Single(db.GetPendingShows());   // stored column reset, not just the disk
    }

    [Fact]
    public void DeleteTheme_refuses_a_folder_outside_the_configured_roots()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");
        var folder = db.GetShow(id)!["folderName"]!.ToString()!;
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        // A root that does not contain the show folder.
        db.SetLibraryPaths([Path.Combine(dir.Path, "elsewhere")]);

        var result = ShowsControllerTests.New(db).DeleteTheme(id);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(File.Exists(Path.Combine(folder, "theme.mp3")));   // untouched
    }

    [Fact]
    public void ThemeAudio_404s_when_there_is_no_theme()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");

        Assert.IsType<NotFoundObjectResult>(ShowsControllerTests.New(db).GetThemeAudio(id));
    }

    [Fact]
    public void ThemeAudio_serves_the_file_with_the_right_content_type()
    {
        using var dir = new TempDir();
        var db = ShowsControllerTests.NewDb(dir);
        var id = ShowsControllerTests.AddShow(db, dir, "Show");
        var folder = db.GetShow(id)!["folderName"]!.ToString()!;
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);

        var result = Assert.IsType<PhysicalFileResult>(ShowsControllerTests.New(db).GetThemeAudio(id));

        Assert.Equal("audio/mpeg", result.ContentType);
        Assert.True(result.EnableRangeProcessing);
    }
}
