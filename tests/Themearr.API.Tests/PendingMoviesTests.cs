using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// <see cref="Database.GetPendingMovies"/> is the cheap, stored-status pre-filter the
/// auto-download worker uses instead of <see cref="Database.GetAllMovies"/>, so an idle,
/// fully-downloaded library doesn't trigger a full per-movie disk scan every 30 seconds.
/// It MUST read the stored <c>status</c> column and never touch the filesystem.
/// </summary>
public class PendingMoviesTests
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
    public void Returns_stored_pending_only_excluding_downloaded_and_ignored()
    {
        using var d1 = new TempDir();
        using var d2 = new TempDir();
        using var d3 = new TempDir();

        var db = NewDb();
        db.UpsertMovies(
        [
            new MovieRecord(d1.Path, "test", "1", "Aaa", 2020, ""),
            new MovieRecord(d2.Path, "test", "2", "Bbb", 2021, ""),
            new MovieRecord(d3.Path, "test", "3", "Ccc", 2022, ""),
        ]);
        db.SetMovieStatus(MovieFolderId.For(d2.Path), "downloaded");
        db.SetMovieIgnored(MovieFolderId.For(d3.Path), true);

        var pending = db.GetPendingMovies();

        var only = Assert.Single(pending);
        Assert.Equal("Aaa", only["title"]?.ToString());
    }

    [Fact]
    public void Does_not_stat_the_filesystem()
    {
        // A stored-pending movie whose folder does NOT exist is still returned — proving
        // this reads the stored status column, not a per-movie disk check. GetAllMovies,
        // which IS disk-derived, filters the same row out. That divergence is exactly what
        // makes the worker's pre-filter free on a fully-downloaded library.
        var db = NewDb();
        var ghost = Path.Combine(Path.GetTempPath(), "themearr-missing-" + Guid.NewGuid().ToString("N"));
        db.UpsertMovies([new MovieRecord(ghost, "test", "1", "Ghost", 2020, "")]);

        Assert.Empty(db.GetAllMovies());      // disk-derived: missing folder → filtered out
        Assert.Single(db.GetPendingMovies()); // stored-status: still present, no stat
    }
}
