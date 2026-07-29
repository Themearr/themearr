using Themearr.API.Data;

namespace Themearr.API.Tests;

public class ThemeHistoryMediaTypeTests
{
    private static Database New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    [Fact]
    public void AddThemeHistory_defaults_to_movie_and_records_show_when_asked()
    {
        using var dir = new TempDir();
        var db = New(dir);

        db.AddThemeHistory("m1", "A Movie", 2001, "Theme", "http://x");   // default → movie
        db.AddThemeHistory("s1", "A Show",  2010, "Intro", "http://y", "show");

        var rows = db.GetThemeHistory();
        Assert.Equal("show",  rows.Single(r => (string)r["movieId"]! == "s1")["mediaType"]);
        Assert.Equal("movie", rows.Single(r => (string)r["movieId"]! == "m1")["mediaType"]);
    }

    /// <summary>
    /// The column is added by migration, so rows written by an older build (before the
    /// column existed) must still read back as movies rather than tripping the NOT NULL
    /// read. Simulates a legacy install by inserting through the pre-migration shape.
    /// </summary>
    [Fact]
    public void Existing_history_rows_are_backfilled_as_movies()
    {
        using var dir = new TempDir();
        var dbPath = Path.Combine(dir.Path, "legacy.db");

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
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
                    downloaded_at TEXT NOT NULL
                );
                INSERT INTO theme_history (movie_id, movie_title, downloaded_at)
                VALUES ('old1', 'Legacy Movie', '2020-01-01T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        var db = new Database(dbPath);
        db.Init();

        var row = db.GetThemeHistory().Single(r => (string)r["movieId"]! == "old1");
        Assert.Equal("movie", row["mediaType"]);
    }

    /// <summary>
    /// The dashboard's coverage/total/pending all come from the movies table, so its
    /// history-derived numbers must stay movies-only — otherwise a show download
    /// inflates "this week" against a movies-only denominator.
    /// </summary>
    [Fact]
    public void Dashboard_stats_ignore_show_history()
    {
        using var dir = new TempDir();
        var db = New(dir);

        db.AddThemeHistory("m1", "A Movie", 2001, "Theme", "http://x");
        db.AddThemeHistory("s1", "A Show",  2010, "Intro", "http://y", "show");

        var stats = db.GetStats();
        Assert.Equal(1, stats.AddedThisWeek);
        Assert.DoesNotContain(stats.RecentActivity, a => (string)a["movieId"]! == "s1");
    }
}
