using Microsoft.Data.Sqlite;
using Themearr.API.Data;

namespace Themearr.API.Tests;

public class ShowsSchemaTests
{
    [Fact]
    public void Init_creates_a_shows_table_with_the_expected_columns()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();

        using var conn = new SqliteConnection($"Data Source={Path.Combine(dir.Path, "test.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(shows)";
        var cols = new HashSet<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));

        foreach (var expected in new[] { "id", "folderName", "source", "source_ref", "title",
                                         "year", "sourcePath", "status", "ignored", "synced_at", "plex_has_theme" })
            Assert.Contains(expected, cols);
    }
}
