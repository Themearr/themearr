using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowStatsTests
{
    private static string Add(Database db, TempDir dir, string title, bool plexHasTheme, bool localTheme)
    {
        var folder = Path.Combine(dir.Path, title);
        Directory.CreateDirectory(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", $"srv1:{title}", title, 2010, folder, plexHasTheme)]);
        if (localTheme) File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        return MediaFolderId.For(folder);
    }

    [Fact]
    public void Counts_each_state_and_treats_plexTheme_as_covered()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();

        Add(db, dir, "Downloaded", plexHasTheme: false, localTheme: true);
        Add(db, dir, "PlexThemed", plexHasTheme: true,  localTheme: false);
        Add(db, dir, "Pending",    plexHasTheme: false, localTheme: false);
        var ignoredId = Add(db, dir, "Ignored", plexHasTheme: false, localTheme: false);
        db.SetShowIgnored(ignoredId, true);

        var stats = db.GetShowStats();

        Assert.Equal(4, stats.Total);
        Assert.Equal(1, stats.Downloaded);
        Assert.Equal(1, stats.PlexTheme);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Ignored);
        // (downloaded + plexTheme) / total — a Plex-themed show is covered.
        Assert.Equal(50.0, stats.Coverage);
    }

    [Fact]
    public void Empty_library_reports_zero_coverage_without_dividing_by_zero()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();

        var stats = db.GetShowStats();

        Assert.Equal(0, stats.Total);
        Assert.Equal(0.0, stats.Coverage);
    }

    [Fact]
    public void Endpoint_returns_the_counts()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        Add(db, dir, "PlexThemed", plexHasTheme: true, localTheme: false);

        // `sources` is null! deliberately: this endpoint returns no poster URLs, so it must
        // never touch the source resolver. If that changes, this throws loudly.
        var controller = new StatsController(db, new PosterUrlSigner([1, 2, 3]), null!);
        var result = Assert.IsType<OkObjectResult>(controller.GetShowStats());
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("plexTheme").GetInt32());
        Assert.Equal(100.0, body.GetProperty("coverage").GetDouble());
    }
}
