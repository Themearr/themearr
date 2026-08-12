using Microsoft.Data.Sqlite;
using Themearr.API.Data;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the re-check columns on both a fresh database and one upgraded from a schema
/// that predates them — the second case is every existing install.
/// </summary>
public class RecheckSchemaTests
{
    private static HashSet<string> Columns(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var cols = new HashSet<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }

    [Fact]
    public void Init_onAFreshDatabase_addsEveryRecheckColumn()
    {
        using var dir = new TempDir();
        var path = dir.File("fresh.db");
        new Database(path).Init();

        Assert.Contains("picked_by",   Columns(path, "theme_history"));
        Assert.Contains("match_score", Columns(path, "theme_history"));
        foreach (var table in new[] { "movies", "shows" })
        {
            Assert.Contains("recheck_state",  Columns(path, table));
            Assert.Contains("recheck_reason", Columns(path, table));
        }
    }

    [Fact]
    public void Init_isIdempotent_soASecondStartupDoesNotThrow()
    {
        using var dir = new TempDir();
        var path = dir.File("twice.db");
        new Database(path).Init();
        new Database(path).Init();   // would throw "duplicate column name" if unguarded

        Assert.Contains("match_score", Columns(path, "theme_history"));
    }

    [Fact]
    public void Init_rerunOverAnAlreadyMigratedDatabase_leavesProvenanceNull()
    {
        using var dir = new TempDir();
        var path = dir.File("upgrade.db");

        // After migration, a row inserted into the already-migrated table: no picked_by, no match_score
        // (both are nullable). This test does NOT exercise upgrading a pre-migration database — it
        // verifies idempotence: running Init() again over an already-migrated table does not discard
        // the old rows or throw a "duplicate column" error.
        var db = new Database(path);
        db.Init();
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO theme_history (movie_id, movie_title, downloaded_at, media_type) " +
                "VALUES ('m1', 'Dune', '2026-01-01T00:00:00Z', 'movie')";
            cmd.ExecuteNonQuery();
        }

        new Database(path).Init();   // re-run migrations over the existing row

        using var check = new SqliteConnection($"Data Source={path}");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT picked_by, match_score FROM theme_history WHERE movie_id = 'm1'";
        using var reader = q.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public void Init_migratesADatabaseThatPredatesTheColumns_preservingItsRows()
    {
        using var dir = new TempDir();
        var path = dir.File("upgrade.db");

        // Build a pre-V3 database: the schema as it existed before picked_by/match_score
        // were added. Columns match the initial CREATE TABLE (Database.cs:56-64) plus
        // media_type added by MigrateHistoryTableV2 (Database.cs:110-111).
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE theme_history (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    movie_id      TEXT NOT NULL,
                    movie_title   TEXT NOT NULL,
                    movie_year    INTEGER,
                    theme_title   TEXT,
                    source_url    TEXT,
                    downloaded_at TEXT NOT NULL,
                    media_type    TEXT NOT NULL DEFAULT 'movie'
                )
                """;
            cmd.ExecuteNonQuery();

            // A row written by the pre-upgrade schema: no picked_by, no match_score.
            cmd.CommandText =
                "INSERT INTO theme_history (movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at, media_type) " +
                "VALUES ('m1', 'Dune', 2021, 'Dune Theme', 'https://example.test/theme', '2026-01-01T00:00:00Z', 'movie')";
            cmd.ExecuteNonQuery();
        }

        // Run Init() for the first time on this pre-migration database.
        new Database(path).Init();

        // Verify: new columns exist and the old row's provenance is NULL.
        Assert.Contains("picked_by",   Columns(path, "theme_history"));
        Assert.Contains("match_score", Columns(path, "theme_history"));
        using var check = new SqliteConnection($"Data Source={path}");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT picked_by, match_score FROM theme_history WHERE movie_id = 'm1'";
        using var reader = q.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));  // picked_by should be NULL
        Assert.True(reader.IsDBNull(1));  // match_score should be NULL
    }
}
