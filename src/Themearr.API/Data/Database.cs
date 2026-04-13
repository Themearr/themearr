using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Themearr.API.Data;

public class Database(string dbPath)
{
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    public void Init()
    {
        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS movies (
                id              TEXT PRIMARY KEY,
                plex_server_id  TEXT NOT NULL,
                plex_rating_key TEXT NOT NULL,
                title           TEXT NOT NULL,
                year            INTEGER,
                sourcePath      TEXT,
                folderName      TEXT,
                status          TEXT NOT NULL DEFAULT 'pending',
                ignored         INTEGER NOT NULL DEFAULT 0,
                UNIQUE(plex_server_id, plex_rating_key)
            )
            """);
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS theme_history (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                movie_id      TEXT NOT NULL,
                movie_title   TEXT NOT NULL,
                movie_year    INTEGER,
                theme_title   TEXT,
                source_url    TEXT,
                downloaded_at TEXT NOT NULL
            )
            """);
        MigrateMoviesTable(conn);
        MigrateHistoryTable(conn);
        MigrateMoviesTableV2(conn);
    }

    private static void MigrateHistoryTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(theme_history)";
        var columns = new HashSet<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));

        if (!columns.Contains("theme_title"))
            conn.Execute("ALTER TABLE theme_history ADD COLUMN theme_title TEXT");
        if (!columns.Contains("source_url"))
            conn.Execute("ALTER TABLE theme_history ADD COLUMN source_url TEXT");
    }

    private static void MigrateMoviesTableV2(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(movies)";
        var columns = new HashSet<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));
        if (!columns.Contains("ignored"))
            conn.Execute("ALTER TABLE movies ADD COLUMN ignored INTEGER NOT NULL DEFAULT 0");
    }

    private static void MigrateMoviesTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(movies)";
        var columns = new HashSet<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));

        var required = new[] { "id", "plex_server_id", "plex_rating_key", "title", "year", "sourcePath", "folderName", "status" };
        if (required.All(c => columns.Contains(c))) return;

        conn.Execute("ALTER TABLE movies RENAME TO movies_legacy");
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
                UNIQUE(plex_server_id, plex_rating_key)
            )
            """);
        if (new[] { "id", "title", "year", "folderName", "status" }.All(c => columns.Contains(c)))
        {
            using var r2 = conn.Query("SELECT id, title, year, folderName, status FROM movies_legacy");
            while (r2.Read())
            {
                var legacyId = r2.GetString(0);
                conn.Execute(
                    "INSERT INTO movies (id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status) VALUES (@id, 'legacy', @rk, @t, @y, '', @f, @s)",
                    ("@id", $"legacy:{legacyId}"), ("@rk", legacyId),
                    ("@t", r2.GetString(1)), ("@y", r2.IsDBNull(2) ? null : r2.GetInt32(2)),
                    ("@f", r2.GetString(3)), ("@s", r2.GetString(4)));
            }
        }
        conn.Execute("DROP TABLE movies_legacy");
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    public string GetSetting(string key, string @default = "")
    {
        using var conn = Open();
        using var r = conn.Query("SELECT value FROM settings WHERE key = @k", ("@k", key));
        return r.Read() ? r.GetString(0) : @default;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        conn.Execute(
            "INSERT INTO settings (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("@k", key), ("@v", value));
    }

    public T GetJsonSetting<T>(string key, T @default)
    {
        var raw = GetSetting(key);
        if (string.IsNullOrEmpty(raw)) return @default;
        try { return JsonSerializer.Deserialize<T>(raw) ?? @default; }
        catch { return @default; }
    }

    public void SetJsonSetting<T>(string key, T value) =>
        SetSetting(key, JsonSerializer.Serialize(value));

    // ── Setup flags ───────────────────────────────────────────────────────────

    public bool IsSetupComplete() => GetSetting("setup_complete") == "1";
    public void MarkSetupComplete() => SetSetting("setup_complete", "1");

    public void ResetAppState()
    {
        using var conn = Open();
        conn.Execute("DELETE FROM movies");
        conn.Execute("DELETE FROM settings");
    }

    // ── Plex servers / libraries / paths ────────────────────────────────────

    public List<Dictionary<string, object?>> GetPlexServers() =>
        GetJsonSetting("plex_selected_servers", new List<Dictionary<string, object?>>());

    public void SetPlexServers(List<Dictionary<string, object?>> servers) =>
        SetJsonSetting("plex_selected_servers", servers);

    public Dictionary<string, List<string>> GetSelectedLibraries() =>
        GetJsonSetting("plex_selected_libraries", new Dictionary<string, List<string>>());

    public void SetSelectedLibraries(Dictionary<string, List<string>> libs) =>
        SetJsonSetting("plex_selected_libraries", libs);

    public List<Dictionary<string, string>> GetPathMappings() =>
        GetJsonSetting("path_mappings", new List<Dictionary<string, string>>());

    public void SetPathMappings(List<Dictionary<string, string>> mappings) =>
        SetJsonSetting("path_mappings", mappings);

    public Dictionary<string, (string Url, string Token)> GetPlexServersDict()
    {
        var dict = new Dictionary<string, (string, string)>();
        foreach (var srv in GetPlexServers())
        {
            var id    = srv.GetValueOrDefault("id")?.ToString()    ?? "";
            var url   = srv.GetValueOrDefault("url")?.ToString()   ?? "";
            var token = srv.GetValueOrDefault("token")?.ToString() ?? "";
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(url))
                dict[id] = (url, token);
        }
        return dict;
    }

    public List<string> GetLibraryPaths()
    {
        var paths = GetJsonSetting("library_paths", new List<string>());
        if (paths.Count == 0)
            paths = GetPathMappings()
                .Select(m => m.GetValueOrDefault("target", ""))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToList();
        return paths;
    }

    public void SetLibraryPaths(List<string> paths) =>
        SetJsonSetting("library_paths", paths.Distinct().Where(p => !string.IsNullOrEmpty(p)).ToList());

    // ── Movies ────────────────────────────────────────────────────────────────

    public void UpsertMovies(IEnumerable<MovieRecord> movies)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var m in movies)
            conn.Execute("""
                INSERT INTO movies (id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status)
                VALUES (@id, @sid, @rk, @t, @y, @sp, @fn, 'pending')
                ON CONFLICT(id) DO UPDATE SET
                    plex_server_id  = excluded.plex_server_id,
                    plex_rating_key = excluded.plex_rating_key,
                    title           = excluded.title,
                    year            = excluded.year,
                    sourcePath      = excluded.sourcePath,
                    folderName      = excluded.folderName
                """,
                ("@id", m.Id), ("@sid", m.PlexServerId), ("@rk", m.PlexRatingKey),
                ("@t", m.Title), ("@y", (object?)m.Year ?? DBNull.Value),
                ("@sp", m.SourcePath), ("@fn", m.FolderName));
        tx.Commit();
    }

    public List<Dictionary<string, object?>> GetAllMovies()
    {
        using var conn = Open();
        using var r = conn.Query("SELECT id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status, ignored FROM movies ORDER BY status, title");
        var result = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = ReadMovieRow(r);
            if (row != null) result.Add(row);
        }
        return result;
    }

    public Dictionary<string, object?>? GetMovie(string id)
    {
        using var conn = Open();
        using var r = conn.Query(
            "SELECT id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status, ignored FROM movies WHERE id = @id",
            ("@id", id));
        return r.Read() ? ReadMovieRow(r) : null;
    }

    public void SetMovieStatus(string id, string status)
    {
        using var conn = Open();
        conn.Execute("UPDATE movies SET status = @s WHERE id = @id", ("@s", status), ("@id", id));
    }

    public void SetMovieIgnored(string id, bool ignored)
    {
        using var conn = Open();
        conn.Execute("UPDATE movies SET ignored = @v WHERE id = @id", ("@v", ignored ? 1 : 0), ("@id", id));
    }

    // ── History ───────────────────────────────────────────────────────────────

    public void AddThemeHistory(string movieId, string movieTitle, int? movieYear, string? themeTitle, string? sourceUrl)
    {
        using var conn = Open();
        conn.Execute(
            "INSERT INTO theme_history (movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at) VALUES (@mid, @t, @y, @tt, @url, @dt)",
            ("@mid", movieId), ("@t", movieTitle),
            ("@y",   (object?)movieYear  ?? DBNull.Value),
            ("@tt",  (object?)themeTitle ?? DBNull.Value),
            ("@url", (object?)sourceUrl  ?? DBNull.Value),
            ("@dt",  DateTime.UtcNow.ToString("o")));
    }

    public List<Dictionary<string, object?>> GetThemeHistory(int limit = 200)
    {
        using var conn = Open();
        using var r = conn.Query(
            "SELECT id, movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at FROM theme_history ORDER BY id DESC LIMIT @lim",
            ("@lim", limit));
        var result = new List<Dictionary<string, object?>>();
        while (r.Read())
            result.Add(new Dictionary<string, object?>
            {
                ["id"]           = r.GetInt64(0),
                ["movieId"]      = r.GetString(1),
                ["movieTitle"]   = r.GetString(2),
                ["movieYear"]    = r.IsDBNull(3) ? null : r.GetInt32(3),
                ["themeTitle"]   = r.IsDBNull(4) ? null : r.GetString(4),
                ["sourceUrl"]    = r.IsDBNull(5) ? null : r.GetString(5),
                ["downloadedAt"] = r.GetString(6),
            });
        return result;
    }

    private static Dictionary<string, object?>? ReadMovieRow(SqliteDataReader r)
    {
        var ignored = !r.IsDBNull(8) && r.GetInt32(8) == 1;
        var folder  = r.IsDBNull(6) ? "" : r.GetString(6);

        // Always return ignored movies so they can be unignored from the UI;
        // non-ignored movies with missing folders can't be used so filter them out.
        if (!ignored && (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)))
            return null;

        string status;
        if (ignored)
            status = "ignored";
        else
        {
            var hasTheme = Directory.EnumerateFiles(folder, "theme.*")
                                     .Any(f => Path.GetExtension(f) is not (".part" or ".ytdl"));
            status = hasTheme ? "downloaded" : "pending";
        }

        return new Dictionary<string, object?>
        {
            ["id"]             = r.GetString(0),
            ["plexServerId"]   = r.GetString(1),
            ["plexRatingKey"]  = r.GetString(2),
            ["title"]          = r.GetString(3),
            ["year"]           = r.IsDBNull(4) ? null : r.GetInt32(4),
            ["sourcePath"]     = r.IsDBNull(5) ? null : r.GetString(5),
            ["folderName"]     = folder,
            ["status"]         = status,
        };
    }
}

// ── Extension helpers ─────────────────────────────────────────────────────────

file static class SqliteExtensions
{
    public static void Execute(this SqliteConnection conn, string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static SqliteDataReader Query(this SqliteConnection conn, string sql, params (string name, object? value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteReader();
    }
}

// ── Simple DTO for upsert ──────────────────────────────────────────────────────

public record MovieRecord(
    string Id,
    string PlexServerId,
    string PlexRatingKey,
    string Title,
    int? Year,
    string SourcePath,
    string FolderName);
