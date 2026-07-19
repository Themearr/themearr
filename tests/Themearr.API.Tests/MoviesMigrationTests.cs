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

    [Fact]
    public void Movies_are_rekeyed_by_folder_and_keep_status_and_ignored()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        InsertOldMovie(path, "srv1:101", "/movies/Heat (1995)", "Heat", "downloaded", 0);
        InsertOldMovie(path, "srv1:102", "/movies/Ronin (1998)", "Ronin", "pending", 1);

        new Database(path).Init();

        var db = new Database(path);
        var heat = db.GetMovie(MovieFolderId.For("/movies/Heat (1995)"));
        Assert.NotNull(heat);
        Assert.Equal("downloaded", heat!["status"]?.ToString());
        Assert.Equal("/movies/Heat (1995)", heat["folderName"]?.ToString());

        var ronin = db.GetMovie(MovieFolderId.For("/movies/Ronin (1998)"));
        Assert.NotNull(ronin);
        Assert.Equal(1L, Convert.ToInt64(ronin!["ignored"]));
    }

    [Fact]
    public void The_plex_identifiers_are_preserved_in_source_ref_so_posters_keep_working()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        InsertOldMovie(path, "srv1:101", "/movies/Heat (1995)", "Heat", "pending", 0);

        new Database(path).Init();

        var movie = new Database(path).GetMovie(MovieFolderId.For("/movies/Heat (1995)"));
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
        InsertOldMovie(path, "srv1:101", "/movies/Heat (1995)", "Heat", "pending", 0);
        InsertOldMovie(path, "srv1:999", "", "Orphan", "pending", 0);

        new Database(path).Init();

        Assert.Single(new Database(path).GetAllMovies());
    }

    [Fact]
    public void Two_movies_in_one_folder_collapse_to_one_row()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        InsertOldMovie(path, "srv1:101", "/movies/Heat (1995)", "Heat", "downloaded", 0);
        InsertOldMovie(path, "srv1:102", "/movies/Heat (1995)", "Heat (Director's Cut)", "pending", 0);

        new Database(path).Init();

        Assert.Single(new Database(path).GetAllMovies());
    }

    [Fact]
    public void Running_init_twice_is_a_no_op()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        InsertOldMovie(path, "srv1:101", "/movies/Heat (1995)", "Heat", "downloaded", 0);

        new Database(path).Init();
        new Database(path).Init();

        var movie = Assert.Single(new Database(path).GetAllMovies());
        Assert.Equal("downloaded", movie["status"]?.ToString());
    }

    [Fact]
    public void Folders_differing_only_by_a_trailing_separator_collapse_to_one_row()
    {
        using var dir = new TempDir();
        var path = OldSchemaDb(dir);
        InsertOldMovie(path, "srv1:101", "/movies/Heat (1995)", "Heat", "downloaded", 0);
        InsertOldMovie(path, "srv1:102", "/movies/Heat (1995)/", "Heat (Director's Cut)", "pending", 0);

        new Database(path).Init();

        Assert.Single(new Database(path).GetAllMovies());
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
