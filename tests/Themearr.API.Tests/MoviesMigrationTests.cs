using Microsoft.Data.Sqlite;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class MoviesMigrationTests
{
    /// <summary>Builds a database on the pre-B1 (Plex-keyed) schema.</summary>
    private static string OldSchemaDb(TempDir dir)
    {
        var path = Path.Combine(dir.Path, "old.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        conn.Execute("""
            CREATE TABLE movies (
                id              TEXT PRIMARY KEY,
                plex_server_id  TEXT NOT NULL,
                plex_rating_key TEXT NOT NULL,
                title           TEXT NOT NULL,
                year            INTEGER,
                sourcePath      TEXT,
                folderName      TEXT,
                status          TEXT NOT NULL DEFAULT 'pending',
                ignored         INTEGER NOT NULL DEFAULT 0,
                synced_at       TEXT,
                UNIQUE(plex_server_id, plex_rating_key)
            )
            """);
        conn.Execute("""
            CREATE TABLE theme_history (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                movie_id      TEXT NOT NULL,
                movie_title   TEXT NOT NULL,
                movie_year    INTEGER,
                theme_title   TEXT,
                source_url    TEXT,
                downloaded_at TEXT NOT NULL
            )
            """);
        conn.Execute("CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        return path;
    }

    private static void InsertOldMovie(string dbPath, string id, string folder, string title, string status, int ignored)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        conn.Execute(
            "INSERT INTO movies (id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status, ignored) " +
            "VALUES (@id, 'srv1', @rk, @t, 1995, '/plex/path/file.mkv', @f, @s, @ig)",
            ("@id", id), ("@rk", id.Split(':')[^1]), ("@t", title),
            ("@f", folder), ("@s", status), ("@ig", ignored));
    }

    /// <summary>
    /// Creates a real directory under the test's temp dir and returns its path. ReadMovieRow
    /// filters out non-ignored rows whose folderName doesn't exist on disk, so tests that rely
    /// on a row surviving (or being read back at all) must use paths that actually exist —
    /// otherwise, once the readers are updated to the new schema, assertions like
    /// Assert.Single(...) would pass because every row got filtered to zero, not because the
    /// migration behaviour under test actually held.
    /// </summary>
    private static string MovieDir(TempDir dir, string name)
    {
        var path = Path.Combine(dir.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Movies_are_rekeyed_by_folder_and_keep_status_and_ignored()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        var heatFolder = MovieDir(dir, "Heat (1995)");
        var roninFolder = MovieDir(dir, "Ronin (1998)");
        InsertOldMovie(path, "srv1:101", heatFolder, "Heat", "downloaded", 0);
        InsertOldMovie(path, "srv1:102", roninFolder, "Ronin", "pending", 1);

        new Database(path).Init();

        var db = new Database(path);
        var heat = db.GetMovie(MovieFolderId.For(heatFolder));
        Assert.NotNull(heat);
        Assert.Equal("downloaded", heat!["status"]?.ToString());
        Assert.Equal(heatFolder, heat["folderName"]?.ToString());

        var ronin = db.GetMovie(MovieFolderId.For(roninFolder));
        Assert.NotNull(ronin);
        Assert.Equal(1L, Convert.ToInt64(ronin!["ignored"]));
    }

    [Fact]
    public void The_plex_identifiers_are_preserved_in_source_ref_so_posters_keep_working()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        var heatFolder = MovieDir(dir, "Heat (1995)");
        InsertOldMovie(path, "srv1:101", heatFolder, "Heat", "pending", 0);

        new Database(path).Init();

        var movie = new Database(path).GetMovie(MovieFolderId.For(heatFolder));
        Assert.Equal("plex", movie!["source"]?.ToString());
        Assert.Equal("srv1:101", movie["sourceRef"]?.ToString());
    }

    [Fact]
    public void History_rows_are_remapped_to_the_new_ids()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        InsertOldMovie(path, "srv1:101", "/movies/Heat (1995)", "Heat", "downloaded", 0);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            conn.Execute(
                "INSERT INTO theme_history (movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at) " +
                "VALUES ('srv1:101', 'Heat', 1995, 'Heat Theme', 'https://example.test/x', '2026-01-01T00:00:00Z')");
        }

        new Database(path).Init();

        var history = new Database(path).GetThemeHistory();
        var entry = Assert.Single(history);
        Assert.Equal(MovieFolderId.For("/movies/Heat (1995)"), entry["movieId"]?.ToString());
    }

    [Fact]
    public void Rows_with_no_resolved_folder_are_dropped()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        var heatFolder = MovieDir(dir, "Heat (1995)");
        InsertOldMovie(path, "srv1:101", heatFolder, "Heat", "pending", 0);
        InsertOldMovie(path, "srv1:999", "", "Orphan", "pending", 0);

        new Database(path).Init();

        Assert.Single(new Database(path).GetAllMovies());
    }

    [Fact]
    public void Two_movies_in_one_folder_collapse_to_one_row()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        var heatFolder = MovieDir(dir, "Heat (1995)");
        InsertOldMovie(path, "srv1:101", heatFolder, "Heat", "downloaded", 0);
        InsertOldMovie(path, "srv1:102", heatFolder, "Heat (Director's Cut)", "pending", 0);

        new Database(path).Init();

        Assert.Single(new Database(path).GetAllMovies());
    }

    [Fact]
    public void Running_init_twice_is_a_no_op()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        var heatFolder = MovieDir(dir, "Heat (1995)");
        InsertOldMovie(path, "srv1:101", heatFolder, "Heat", "downloaded", 0);

        new Database(path).Init();
        new Database(path).Init();

        var movie = Assert.Single(new Database(path).GetAllMovies());
        Assert.Equal("downloaded", movie["status"]?.ToString());
    }

    /// <summary>
    /// Regression test for the critical re-entry bug: MigrateMoviesTable used to decide it
    /// still had work to do whenever "movies" lacked plex_server_id/plex_rating_key — which
    /// is exactly the shape V4 leaves the table in. So a second Init() call (e.g. the next
    /// app startup) would rename the table, recreate the OLD schema, and copy across only
    /// id/title/year/folderName/status, silently wiping ignored flags, source_ref (the Plex
    /// identity poster loading depends on) and sourcePath.
    ///
    /// Asserts on raw rows via SQL rather than GetAllMovies/GetMovie: those readers are
    /// mid-migration (still select the pre-V4 columns) and would throw before this bug could
    /// even be observed, which is exactly why it went unnoticed.
    /// </summary>
    [Fact]
    public void Running_init_twice_preserves_ignored_and_plex_identity_after_the_v4_rekey()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        var heatFolder = MovieDir(dir, "Heat (1995)");
        var roninFolder = MovieDir(dir, "Ronin (1998)");
        InsertOldMovie(path, "srv1:101", heatFolder, "Heat", "downloaded", 0);
        InsertOldMovie(path, "srv1:102", roninFolder, "Ronin", "pending", 1);

        new Database(path).Init();
        new Database(path).Init();

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        var rows = new Dictionary<string, (string Source, string SourceRef, long Ignored, string SourcePath)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, folderName, source, source_ref, ignored, sourcePath FROM movies";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var folder = r.GetString(1);
                rows[folder] = (r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                                 r.GetInt64(4), r.IsDBNull(5) ? "" : r.GetString(5));
            }
        }

        Assert.Equal(2, rows.Count);

        var heat = rows[heatFolder];
        Assert.Equal("plex", heat.Source);
        Assert.Equal("srv1:101", heat.SourceRef);
        Assert.Equal(0L, heat.Ignored);
        Assert.Equal("/plex/path/file.mkv", heat.SourcePath);

        var ronin = rows[roninFolder];
        Assert.Equal("plex", ronin.Source);
        Assert.Equal("srv1:102", ronin.SourceRef);
        Assert.Equal(1L, ronin.Ignored);
        Assert.Equal("/plex/path/file.mkv", ronin.SourcePath);
    }

    [Fact]
    public void Folders_differing_only_by_a_trailing_separator_collapse_to_one_row()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        var heatFolder = MovieDir(dir, "Heat (1995)");
        InsertOldMovie(path, "srv1:101", heatFolder, "Heat", "downloaded", 0);
        InsertOldMovie(path, "srv1:102", heatFolder + "/", "Heat (Director's Cut)", "pending", 0);

        new Database(path).Init();

        // Raw SQL, not GetAllMovies: the readers still select the pre-V4 columns and would
        // throw, but the collapse behaviour this test checks lives entirely in the migration
        // and doesn't need them.
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM movies";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(1L, count);
    }
}

// `Database.cs` keeps its own SQL helper `file`-scoped so nothing outside it can bypass
// the `Database` class's public API. This test builds a raw pre-migration schema directly,
// so it needs the same convenience helper — kept file-local here for the same reason.
file static class SqliteExtensions
{
    public static void Execute(this SqliteConnection conn, string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql; // nosemgrep: csharp-sqli — literal SQL only; all values bound via SqliteParameter
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
