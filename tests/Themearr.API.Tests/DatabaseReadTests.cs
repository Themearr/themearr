using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

// Characterization tests for the DB read paths that go through the `Query` helper,
// so the command-dispose refactor is provably behavior-preserving.
public class DatabaseReadTests
{
    private static Database NewDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "themearr-test-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new Database(Path.Combine(dir, "themearr.db"));
        db.Init();
        return db;
    }

    [Fact]
    public void GetSetting_roundtripsAndDefaults()
    {
        var db = NewDb();
        db.SetSetting("k", "v");
        Assert.Equal("v", db.GetSetting("k"));
        Assert.Equal("fallback", db.GetSetting("missing", "fallback"));
    }

    [Fact]
    public void GetAllMovies_and_GetStats_reflectDiskStatus()
    {
        using var downloadedDir = new TempDir();
        downloadedDir.Write("theme.mp3", new byte[] { 1, 2, 3 });
        using var pendingDir = new TempDir();

        var db = NewDb();
        db.UpsertMovies(
        [
            new MovieRecord(downloadedDir.Path, "s", "1", "Downloaded Movie", 2020, "/plex/1/f.mkv"),
            new MovieRecord(pendingDir.Path,    "s", "2", "Pending Movie",    2021, "/plex/2/f.mkv"),
        ]);

        var all = db.GetAllMovies();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m["title"]?.ToString() == "Downloaded Movie" && m["status"]?.ToString() == "downloaded");
        Assert.Contains(all, m => m["title"]?.ToString() == "Pending Movie"    && m["status"]?.ToString() == "pending");

        var stats = db.GetStats();
        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.Downloaded);
        Assert.Equal(1, stats.Pending);
    }

    [Fact]
    public void GetMovie_returnsRowOrNull()
    {
        using var dir = new TempDir();
        var db = NewDb();
        db.UpsertMovies([new MovieRecord(dir.Path, "s", "7", "Heat", 1995, "/plex/heat/f.mkv")]);

        Assert.Equal("Heat", db.GetMovie(MovieFolderId.For(dir.Path))!["title"]);
        Assert.Null(db.GetMovie("does-not-exist"));
    }

    [Fact]
    public void GetThemeHistory_returnsRecordedThemes_newestFirst()
    {
        var db = NewDb();
        db.AddThemeHistory("s:1", "Movie A", 2020, "Theme A", "http://a");
        db.AddThemeHistory("s:2", "Movie B", 2021, "Theme B", "http://b");

        var hist = db.GetThemeHistory();
        Assert.Equal(2, hist.Count);
        Assert.Equal("Movie B", hist[0]["movieTitle"]); // newest first (ORDER BY id DESC)
        Assert.Equal("Theme A", hist[1]["themeTitle"]);
    }
}
