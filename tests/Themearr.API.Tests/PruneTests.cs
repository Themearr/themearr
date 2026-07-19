using Themearr.API.Data;

namespace Themearr.API.Tests;

public class PruneTests
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
    public void A_kept_folder_with_a_trailing_separator_is_not_deleted()
    {
        using var dir1 = new TempDir();
        using var dir2 = new TempDir();

        var db = NewDb();
        db.UpsertMovies(
        [
            new MovieRecord(dir1.Path, "test", "1", "Movie One", 2020, ""),
            new MovieRecord(dir2.Path, "test", "2", "Movie Two", 2021, ""),
        ]);

        // Verify both movies were upserted
        var before = db.GetAllMovies();
        Assert.Equal(2, before.Count);

        // Prune, passing BOTH folders as kept, but with a trailing separator appended to one
        // of them. The kept set is normalized via MovieFolderId, so the trailing separator
        // must not defeat the match.
        var dir1WithTrailingSeparator = dir1.Path.EndsWith(Path.DirectorySeparatorChar)
            ? dir1.Path
            : dir1.Path + Path.DirectorySeparatorChar;

        var deleted = db.PruneMoviesExcept([dir1WithTrailingSeparator, dir2.Path]);

        // Should have deleted nothing: both folders are kept, trailing separator notwithstanding.
        Assert.Equal(0, deleted);

        // Both movies should still be in the database
        var after = db.GetAllMovies();
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public void An_empty_kept_set_deletes_nothing()
    {
        using var dir1 = new TempDir();
        using var dir2 = new TempDir();

        var db = NewDb();
        db.UpsertMovies(
        [
            new MovieRecord(dir1.Path, "test", "1", "Movie One", 2020, ""),
            new MovieRecord(dir2.Path, "test", "2", "Movie Two", 2021, ""),
        ]);

        // An empty kept set is a safety guard against pruning on a failed/empty sync, not a
        // "keep nothing" instruction — it must delete nothing rather than empty the library.
        var deleted = db.PruneMoviesExcept([]);

        Assert.Equal(0, deleted);
        Assert.Equal(2, db.GetAllMovies().Count);
    }

    [Fact]
    public void A_folder_absent_from_the_kept_set_is_deleted()
    {
        using var dir1 = new TempDir();
        using var dir2 = new TempDir();

        var db = NewDb();
        db.UpsertMovies(
        [
            new MovieRecord(dir1.Path, "test", "1", "Movie One", 2020, ""),
            new MovieRecord(dir2.Path, "test", "2", "Movie Two", 2021, ""),
        ]);

        var deleted = db.PruneMoviesExcept([dir1.Path]);

        Assert.Equal(1, deleted);
        var remaining = Assert.Single(db.GetAllMovies());
        Assert.Equal(dir1.Path, remaining["folderName"]?.ToString());
    }
}
