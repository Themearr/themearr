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

        // Prune, passing dir1 with a trailing separator (which should match the kept movie)
        var dir1WithTrailingSeparator = dir1.Path.EndsWith(Path.DirectorySeparatorChar)
            ? dir1.Path
            : dir1.Path + Path.DirectorySeparatorChar;

        var deleted = db.PruneMoviesExcept([dir1WithTrailingSeparator]);

        // Should have deleted nothing because dir1 (with or without trailing separator) is "kept"
        Assert.Equal(0, deleted);

        // Both movies should still be in the database
        var after = db.GetAllMovies();
        Assert.Equal(2, after.Count);
    }
}
