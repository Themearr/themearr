using System.Text.Json;
using Microsoft.Data.Sqlite;
using Themearr.API.Services;

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
                id          TEXT PRIMARY KEY,
                folderName  TEXT NOT NULL UNIQUE,
                source      TEXT NOT NULL DEFAULT 'plex',
                source_ref  TEXT,
                title       TEXT NOT NULL,
                year        INTEGER,
                sourcePath  TEXT,
                status      TEXT NOT NULL DEFAULT 'pending',
                ignored     INTEGER NOT NULL DEFAULT 0,
                synced_at   TEXT
            )
            """);
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS shows (
                id             TEXT PRIMARY KEY,
                folderName     TEXT NOT NULL UNIQUE,
                source         TEXT NOT NULL DEFAULT 'plex',
                source_ref     TEXT,
                title          TEXT NOT NULL,
                year           INTEGER,
                sourcePath     TEXT,
                status         TEXT NOT NULL DEFAULT 'pending',
                ignored        INTEGER NOT NULL DEFAULT 0,
                synced_at      TEXT,
                plex_has_theme INTEGER NOT NULL DEFAULT 0
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
        MigrateMoviesTableV3(conn);
        MigrateMoviesTableV4(conn);
        PruneDeadSettings(conn);
    }

    // These keys were written on every server save but read by nothing -- the live
    // server list (and its token) lives in `plex_servers`. plex_server_token in
    // particular is a redundant copy of a Plex credential, so dropping them from
    // legacy installs is a small hygiene win, not just tidiness.
    private static void PruneDeadSettings(SqliteConnection conn) =>
        conn.Execute(
            "DELETE FROM settings WHERE key IN ('plex_server_url', 'plex_server_token', 'plex_server_name')");

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

    private static void MigrateMoviesTableV3(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(movies)";
        var columns = new HashSet<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));
        if (!columns.Contains("synced_at"))
            conn.Execute("ALTER TABLE movies ADD COLUMN synced_at TEXT");
    }

    /// <summary>
    /// Re-keys movies from Plex identifiers to their local folder.
    ///
    /// Runs in a transaction: the earlier rebuild-style migration in this file renames
    /// the table before recreating it, so a failure partway would leave an install with
    /// no movies table at all. SQLite supports transactional DDL, so a failure here rolls
    /// back and the table is never left half-migrated — the upgrade can simply be retried
    /// against intact data.
    /// </summary>
    private static void MigrateMoviesTableV4(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(movies)";
        var columns = new HashSet<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));

        if (columns.Contains("source") || !columns.Contains("plex_rating_key")) return;

        // Everything below — including the read of the pre-migration rows — runs inside
        // the transaction so there is a consistent snapshot of "movies" for the duration
        // of the migration, not just for the destructive DDL that follows it.
        using var tx = conn.BeginTransaction();

        // old id → new id, for rewriting history afterwards
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        var raw = new List<(string NewId, string Folder, string Source, string SourceRef,
                             string Title, object? Year, string SourcePath, string Status,
                             long Ignored, string? SyncedAt)>();

        conn.Query(
            "SELECT id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status, ignored, synced_at FROM movies",
            r =>
            {
                while (r.Read())
                {
                    var oldId  = r.GetString(0);
                    var folder = r.IsDBNull(6) ? "" : r.GetString(6);
                    // Pre-resolution rows have no folder, so they cannot be acted on.
                    if (string.IsNullOrEmpty(folder)) continue;

                    var newId = MediaFolderId.For(folder);
                    remap[oldId] = newId;

                    raw.Add((newId, folder, "plex", $"{r.GetString(1)}:{r.GetString(2)}",
                              r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4),
                              r.IsDBNull(5) ? "" : r.GetString(5),
                              r.GetString(7), r.IsDBNull(8) ? 0L : r.GetInt64(8),
                              r.IsDBNull(9) ? null : r.GetString(9)));
                }
            });

        // Two folders differing only by trailing separators normalize to one id; the first
        // row wins for the display fields (status is re-derived from disk regardless), but
        // if any collapsed row was ignored the user's choice must not be silently dropped.
        var ignoredByFolder = raw
            .GroupBy(x => x.NewId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Any(x => x.Ignored != 0), StringComparer.Ordinal);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(string NewId, string Folder, string Source, string SourceRef,
                             string Title, object? Year, string SourcePath, string Status,
                             long Ignored, string? SyncedAt)>();
        foreach (var row in raw)
        {
            if (!seenIds.Add(row.NewId)) continue;
            rows.Add(row with { Ignored = ignoredByFolder[row.NewId] ? 1L : 0L });
        }

        conn.Execute("ALTER TABLE movies RENAME TO movies_v4_old");
        conn.Execute("""
            CREATE TABLE movies (
                id          TEXT PRIMARY KEY,
                folderName  TEXT NOT NULL UNIQUE,
                source      TEXT NOT NULL DEFAULT 'plex',
                source_ref  TEXT,
                title       TEXT NOT NULL,
                year        INTEGER,
                sourcePath  TEXT,
                status      TEXT NOT NULL DEFAULT 'pending',
                ignored     INTEGER NOT NULL DEFAULT 0,
                synced_at   TEXT
            )
            """);

        foreach (var row in rows)
            conn.Execute("""
                INSERT INTO movies (id, folderName, source, source_ref, title, year, sourcePath, status, ignored, synced_at)
                VALUES (@id, @f, @src, @ref, @t, @y, @sp, @s, @ig, COALESCE(@sa, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')))
                """,
                ("@id", row.NewId), ("@f", row.Folder), ("@src", row.Source), ("@ref", row.SourceRef),
                ("@t", row.Title), ("@y", row.Year ?? (object)DBNull.Value), ("@sp", row.SourcePath),
                ("@s", row.Status), ("@ig", row.Ignored), ("@sa", (object?)row.SyncedAt ?? DBNull.Value));

        // History rows already carry title and year, so any that fail to remap still
        // display correctly rather than going blank.
        foreach (var (oldId, newId) in remap)
            conn.Execute("UPDATE theme_history SET movie_id = @new WHERE movie_id = @old",
                ("@new", newId), ("@old", oldId));

        conn.Execute("DROP TABLE movies_v4_old");
        tx.Commit();
    }

    private static void MigrateMoviesTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(movies)";
        var columns = new HashSet<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));

        // Already on the modern (source-keyed) schema. Post-V4 the table has neither
        // plex_server_id nor plex_rating_key, so without this guard every subsequent
        // startup would think a legacy migration is still needed and would rename the
        // table, recreate the OLD schema, and copy only id/title/year/folderName/status —
        // silently dropping ignored flags, source_ref (Plex identity), and sourcePath.
        if (columns.Contains("source")) return;

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
            conn.Query("SELECT id, title, year, folderName, status FROM movies_legacy", r2 =>
            {
                while (r2.Read())
                {
                    var legacyId = r2.GetString(0);
                    conn.Execute(
                        "INSERT INTO movies (id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status) VALUES (@id, 'legacy', @rk, @t, @y, '', @f, @s)",
                        ("@id", $"legacy:{legacyId}"), ("@rk", legacyId),
                        ("@t", r2.GetString(1)), ("@y", r2.IsDBNull(2) ? null : r2.GetInt32(2)),
                        ("@f", r2.GetString(3)), ("@s", r2.GetString(4)));
                }
            });
        }
        conn.Execute("DROP TABLE movies_legacy");
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    public string GetSetting(string key, string @default = "")
    {
        using var conn = Open();
        var result = @default;
        conn.Query("SELECT value FROM settings WHERE key = @k",
            r => { if (r.Read()) result = r.GetString(0); }, ("@k", key));
        return result;
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

    // Same servers but with the Plex access token blanked, for echoing back in GET
    // responses — the token is write-only and must never leave the server in JSON.
    public List<Dictionary<string, object?>> GetPlexServersRedacted() =>
        GetPlexServers()
            .Select(srv =>
            {
                var copy = new Dictionary<string, object?>(srv) { ["token"] = "" };
                return copy;
            })
            .ToList();

    // Persists an incoming server list while preserving any stored token for a server
    // whose incoming token is blank. Lets the UI load redacted servers and save them
    // back without wiping the token it was never shown.
    //
    // The stored token is only ever carried forward when the incoming url matches the
    // url it was stored against for that id. Matching on id alone would let a caller
    // POST { id: <existing id>, url: <attacker host>, token: "" } and have the real
    // token re-attached to a URL the server never issued it to — PlexLibrarySource.CheckAsync
    // (reachable from the unauthenticated /health endpoint) would then hand the real
    // token to that host. If the url doesn't match and no token was supplied, the server
    // ends up with no token; the existing health check already reports that Plex
    // rejected the credential and the user should sign in again, which is the correct,
    // safe outcome here too.
    public void SetPlexServersMergingTokens(List<Dictionary<string, object?>> incoming)
    {
        var storedTokens = GetPlexServersDict();
        var merged = incoming.Select(srv =>
        {
            var copy = new Dictionary<string, object?>(srv);
            var token = copy.GetValueOrDefault("token")?.ToString() ?? "";
            if (string.IsNullOrEmpty(token))
            {
                var id  = copy.GetValueOrDefault("id")?.ToString() ?? "";
                var url = copy.GetValueOrDefault("url")?.ToString() ?? "";
                if (!string.IsNullOrEmpty(id) && storedTokens.TryGetValue(id, out var s) &&
                    !string.IsNullOrEmpty(s.Token) && UrlsMatch(url, s.Url))
                    copy["token"] = s.Token;
            }
            return copy;
        }).ToList();
        SetPlexServers(merged);
    }

    /// <summary>
    /// Points a stored Plex server at <paramref name="url"/> from an authenticated operator
    /// action, keeping the existing token bound to the new address. Deliberately NOT
    /// SetPlexServersMergingTokens: that path drops the token on a url change to stay safe for
    /// the unauthenticated /health endpoint; this one is only reachable bearer-only, so it
    /// rebinds directly. Returns false when no server has this id.
    /// </summary>
    public bool UpdatePlexServerUrl(string serverId, string url)
    {
        var servers = GetPlexServers();
        var matched = false;
        foreach (var srv in servers)
        {
            if ((srv.GetValueOrDefault("id")?.ToString() ?? "") != serverId) continue;
            srv["url"]  = url;
            srv["urls"] = new List<string> { url };
            matched = true;
        }
        if (matched) SetPlexServers(servers);
        return matched;
    }

    // Ordinal comparison after trimming a single trailing slash — enough to treat
    // "http://host:32400" and "http://host:32400/" as the same server without being
    // lenient about anything that would actually change the destination (scheme, host,
    // port, or case).
    private static bool UrlsMatch(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

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
        {
            if (string.IsNullOrEmpty(m.Folder)) continue;
            var id = MediaFolderId.For(m.Folder);
            conn.Execute("""
                INSERT INTO movies (id, folderName, source, source_ref, title, year, sourcePath, status, synced_at)
                VALUES (@id, @f, @src, @ref, @t, @y, @sp, 'pending',
                        COALESCE((SELECT synced_at FROM movies WHERE id = @id), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')))
                ON CONFLICT(id) DO UPDATE SET
                    folderName = excluded.folderName,
                    source     = excluded.source,
                    source_ref = excluded.source_ref,
                    title      = excluded.title,
                    year       = excluded.year,
                    sourcePath = excluded.sourcePath,
                    synced_at  = COALESCE(movies.synced_at, excluded.synced_at)
                """,
                ("@id", id), ("@f", m.Folder), ("@src", m.Source), ("@ref", m.SourceRef),
                ("@t", m.Title), ("@y", (object?)m.Year ?? DBNull.Value), ("@sp", m.SourcePath));
        }
        tx.Commit();
    }

    /// <summary>
    /// Deletes movies whose folder was not in the most recent sync. Callers MUST only
    /// invoke this after a sync that both succeeded and returned results — pruning on a
    /// failed or empty sync would empty the library. Rows with <c>ignored = 1</c> are
    /// never deleted, even when absent from the kept set: an ignored movie reflects an
    /// explicit user decision, and silently reversing that (only for the movie to
    /// re-sync as pending and get auto-downloaded into a folder the user opted out of)
    /// is worse than leaving a harmless phantom row behind. Returns the number removed.
    /// </summary>
    public int PruneMoviesExcept(IEnumerable<string> keptFolders)
    {
        // Build the kept set using derived IDs, not raw folder strings. folderName is stored
        // verbatim (with or without trailing separators), but identity is MediaFolderId.For(folder)
        // which normalizes those separators away. Comparing raw strings would incorrectly
        // delete a kept folder if the caller passes it with a different trailing-separator state.
        var keep = keptFolders
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => MediaFolderId.For(f))
            .ToHashSet(StringComparer.Ordinal);
        if (keep.Count == 0) return 0;

        using var conn = Open();
        var doomed = new List<string>();
        conn.Query("SELECT id, ignored FROM movies", r =>
        {
            while (r.Read())
                if (!keep.Contains(r.GetString(0)) && r.GetInt64(1) == 0) doomed.Add(r.GetString(0));
        });

        using var tx = conn.BeginTransaction();
        foreach (var id in doomed)
            conn.Execute("DELETE FROM movies WHERE id = @id", ("@id", id));
        tx.Commit();
        return doomed.Count;
    }

    public List<Dictionary<string, object?>> GetAllMovies()
    {
        using var conn = Open();
        var result = new List<Dictionary<string, object?>>();
        conn.Query("SELECT id, folderName, source, source_ref, title, year, sourcePath, status, ignored FROM movies ORDER BY status, title", r =>
        {
            while (r.Read())
            {
                var row = ReadMediaRow(r);
                if (row != null) result.Add(row);
            }
        });
        return result;
    }

    public Dictionary<string, object?>? GetMovie(string id)
    {
        using var conn = Open();
        Dictionary<string, object?>? result = null;
        conn.Query(
            "SELECT id, folderName, source, source_ref, title, year, sourcePath, status, ignored FROM movies WHERE id = @id",
            r => { if (r.Read()) result = ReadMediaRow(r); }, ("@id", id));
        return result;
    }

    /// <summary>
    /// Movies whose STORED status is 'pending' (never successfully downloaded), excluding
    /// ignored ones. Unlike <see cref="GetAllMovies"/> this does NOT stat the filesystem:
    /// it is the cheap pre-filter the auto-download worker runs every tick, so an idle,
    /// fully-downloaded library costs one indexed query instead of a per-movie disk scan.
    /// A caller that needs disk-verified state must still check each returned folder — a
    /// row here may already have a theme added out-of-band (worker reconciles that).
    /// </summary>
    public List<Dictionary<string, object?>> GetPendingMovies()
    {
        using var conn = Open();
        var result = new List<Dictionary<string, object?>>();
        conn.Query(
            "SELECT id, folderName, source, source_ref, title, year, sourcePath FROM movies WHERE status = 'pending' AND ignored = 0 ORDER BY title",
            r =>
            {
                while (r.Read())
                    result.Add(new Dictionary<string, object?>
                    {
                        ["id"]         = r.GetString(0),
                        ["folderName"] = r.IsDBNull(1) ? "" : r.GetString(1),
                        ["source"]     = r.GetString(2),
                        ["sourceRef"]  = r.IsDBNull(3) ? null : r.GetString(3),
                        ["title"]      = r.GetString(4),
                        ["year"]       = r.IsDBNull(5) ? null : r.GetInt32(5),
                        ["sourcePath"] = r.IsDBNull(6) ? null : r.GetString(6),
                    });
            });
        return result;
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

    // ── Shows ───────────────────────────────────────────────────────────────────

    public void UpsertShows(IEnumerable<ShowRecord> shows)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var s in shows)
        {
            if (string.IsNullOrEmpty(s.Folder)) continue;
            var id = MediaFolderId.For(s.Folder);
            conn.Execute("""
                INSERT INTO shows (id, folderName, source, source_ref, title, year, sourcePath, status, synced_at, plex_has_theme)
                VALUES (@id, @f, @src, @ref, @t, @y, @sp, 'pending',
                        COALESCE((SELECT synced_at FROM shows WHERE id = @id), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                        @pht)
                ON CONFLICT(id) DO UPDATE SET
                    folderName     = excluded.folderName,
                    source         = excluded.source,
                    source_ref     = excluded.source_ref,
                    title          = excluded.title,
                    year           = excluded.year,
                    sourcePath     = excluded.sourcePath,
                    plex_has_theme = excluded.plex_has_theme,
                    synced_at      = COALESCE(shows.synced_at, excluded.synced_at)
                """,
                ("@id", id), ("@f", s.Folder), ("@src", s.Source), ("@ref", s.SourceRef),
                ("@t", s.Title), ("@y", (object?)s.Year ?? DBNull.Value), ("@sp", s.SourcePath),
                ("@pht", s.HasPlexTheme ? 1 : 0));
        }
        tx.Commit();
    }

    /// <summary>Deletes shows whose folder was not in the most recent sync; never deletes
    /// ignored ones. Same contract as <see cref="PruneMoviesExcept"/>. Returns the count removed.</summary>
    public int PruneShowsExcept(IEnumerable<string> keptFolders)
    {
        var keep = keptFolders.Where(f => !string.IsNullOrEmpty(f)).Select(MediaFolderId.For)
                              .ToHashSet(StringComparer.Ordinal);
        if (keep.Count == 0) return 0;

        using var conn = Open();
        var doomed = new List<string>();
        conn.Query("SELECT id, ignored FROM shows", r =>
        {
            while (r.Read())
                if (!keep.Contains(r.GetString(0)) && r.GetInt64(1) == 0) doomed.Add(r.GetString(0));
        });
        using var tx = conn.BeginTransaction();
        foreach (var id in doomed) conn.Execute("DELETE FROM shows WHERE id = @id", ("@id", id));
        tx.Commit();
        return doomed.Count;
    }

    public List<Dictionary<string, object?>> GetAllShows()
    {
        using var conn = Open();
        var result = new List<Dictionary<string, object?>>();
        conn.Query("SELECT id, folderName, source, source_ref, title, year, sourcePath, status, ignored FROM shows ORDER BY status, title",
            r => { while (r.Read()) { var row = ReadMediaRow(r); if (row != null) result.Add(row); } });
        return result;
    }

    public Dictionary<string, object?>? GetShow(string id)
    {
        using var conn = Open();
        Dictionary<string, object?>? result = null;
        conn.Query("SELECT id, folderName, source, source_ref, title, year, sourcePath, status, ignored FROM shows WHERE id = @id",
            r => { if (r.Read()) result = ReadMediaRow(r); }, ("@id", id));
        return result;
    }

    /// <summary>Shows whose stored status is 'pending', not ignored, and that Plex does not
    /// already theme. Cheap pre-filter for the show auto-download worker (no filesystem stat).</summary>
    public List<Dictionary<string, object?>> GetPendingShows()
    {
        using var conn = Open();
        var result = new List<Dictionary<string, object?>>();
        conn.Query(
            "SELECT id, folderName, source, source_ref, title, year, sourcePath FROM shows WHERE status = 'pending' AND ignored = 0 AND plex_has_theme = 0 ORDER BY title",
            r =>
            {
                while (r.Read())
                    result.Add(new Dictionary<string, object?>
                    {
                        ["id"] = r.GetString(0), ["folderName"] = r.IsDBNull(1) ? "" : r.GetString(1),
                        ["source"] = r.GetString(2), ["sourceRef"] = r.IsDBNull(3) ? null : r.GetString(3),
                        ["title"] = r.GetString(4), ["year"] = r.IsDBNull(5) ? null : r.GetInt32(5),
                        ["sourcePath"] = r.IsDBNull(6) ? null : r.GetString(6),
                    });
            });
        return result;
    }

    public void SetShowStatus(string id, string status)
    {
        using var conn = Open();
        conn.Execute("UPDATE shows SET status = @s WHERE id = @id", ("@s", status), ("@id", id));
    }

    public void SetShowIgnored(string id, bool ignored)
    {
        using var conn = Open();
        conn.Execute("UPDATE shows SET ignored = @v WHERE id = @id", ("@v", ignored ? 1 : 0), ("@id", id));
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public StatsResult GetStats()
    {
        using var conn = Open();

        // Movie counts: use filesystem-verified status (same logic as the movies page)
        // so that the dashboard numbers always match what's shown there.
        var allMovies  = GetAllMovies();
        int downloaded = allMovies.Count(m => m["status"]?.ToString() == "downloaded");
        int pending    = allMovies.Count(m => m["status"]?.ToString() == "pending");
        int ignored    = allMovies.Count(m => m["status"]?.ToString() == "ignored");

        // Total = entire Plex library (all rows, including ignored and movies whose
        // folders aren't yet mapped), so coverage reflects the full library, not just
        // the subset the app has processed.
        var total = 0;
        conn.Query("SELECT COUNT(*) FROM movies",
            r => { if (r.Read()) total = (int)r.GetInt64(0); });

        var coverage = total > 0 ? Math.Round(downloaded * 100.0 / total, 1) : 0.0;

        // Themes added in the last 7 days
        int addedThisWeek = 0;
        var weekAgo = DateTime.UtcNow.AddDays(-7).ToString("o");
        conn.Query("SELECT COUNT(*) FROM theme_history WHERE downloaded_at >= @w",
            r => { if (r.Read()) addedThisWeek = (int)r.GetInt64(0); }, ("@w", weekAgo));

        // Last 5 downloaded themes
        var recentActivity = new List<Dictionary<string, object?>>();
        conn.Query(
            "SELECT id, movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at FROM theme_history ORDER BY id DESC LIMIT 5",
            r =>
            {
                while (r.Read())
                    recentActivity.Add(new Dictionary<string, object?>
                    {
                        ["id"]           = r.GetInt64(0),
                        ["movieId"]      = r.GetString(1),
                        ["movieTitle"]   = r.GetString(2),
                        ["movieYear"]    = r.IsDBNull(3) ? null : r.GetInt32(3),
                        ["themeTitle"]   = r.IsDBNull(4) ? null : r.GetString(4),
                        ["sourceUrl"]    = r.IsDBNull(5) ? null : r.GetString(5),
                        ["downloadedAt"] = r.GetString(6),
                    });
            });

        // Last 5 recently-synced movies that are still pending (filesystem-verified).
        // Pull extra candidates from DB ordered by syncedAt, then cross-reference with
        // allMovies so only movies whose folders+files confirm 'pending' status are shown.
        var pendingIds = allMovies
            .Where(m => m["status"]?.ToString() == "pending")
            .Select(m => m["id"]?.ToString())
            .ToHashSet();

        var recentlyAdded = new List<Dictionary<string, object?>>();
        conn.Query("""
            SELECT id, source, source_ref, title, year, synced_at
            FROM movies
            WHERE ignored = 0 AND status = 'pending' AND synced_at IS NOT NULL
            ORDER BY synced_at DESC LIMIT 20
            """, r =>
        {
            while (r.Read() && recentlyAdded.Count < 5)
            {
                var id = r.GetString(0);
                if (!pendingIds.Contains(id)) continue;
                recentlyAdded.Add(new Dictionary<string, object?>
                {
                    ["id"]        = id,
                    ["source"]    = r.GetString(1),
                    ["sourceRef"] = r.IsDBNull(2) ? null : r.GetString(2),
                    ["title"]     = r.GetString(3),
                    ["year"]      = r.IsDBNull(4) ? null : r.GetInt32(4),
                    ["syncedAt"]  = r.IsDBNull(5) ? null : r.GetString(5),
                });
            }
        });

        return new StatsResult(total, downloaded, pending, ignored, coverage, addedThisWeek, recentActivity, recentlyAdded);
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
        var result = new List<Dictionary<string, object?>>();
        conn.Query(
            "SELECT id, movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at FROM theme_history ORDER BY id DESC LIMIT @lim",
            r =>
            {
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
            }, ("@lim", limit));
        return result;
    }

    private static Dictionary<string, object?>? ReadMediaRow(SqliteDataReader r)
    {
        var ignored = !r.IsDBNull(8) && r.GetInt32(8) == 1;
        var folder  = r.IsDBNull(1) ? "" : r.GetString(1);

        // Always return ignored movies so they can be unignored from the UI;
        // non-ignored movies with missing folders can't be used so filter them out.
        if (!ignored && (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)))
            return null;

        string status;
        if (ignored)
            status = "ignored";
        else
        {
            // A zero-byte/truncated theme.* is treated as not-downloaded so it gets
            // retried rather than being marked done forever (see ThemeFiles). Folder
            // existence was just confirmed above, so skip the redundant re-stat.
            status = ThemeFiles.HasUsableThemeInExistingFolder(folder) ? "downloaded" : "pending";
        }

        return new Dictionary<string, object?>
        {
            ["id"]         = r.GetString(0),
            ["folderName"] = folder,
            ["source"]     = r.GetString(2),
            ["sourceRef"]  = r.IsDBNull(3) ? null : r.GetString(3),
            ["title"]      = r.GetString(4),
            ["year"]       = r.IsDBNull(5) ? null : r.GetInt32(5),
            ["sourcePath"] = r.IsDBNull(6) ? null : r.GetString(6),
            ["status"]     = status,
            ["ignored"]    = ignored,
        };
    }
}

// ── Extension helpers ─────────────────────────────────────────────────────────

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

    // Callback form: the command and reader are disposed here, so no SqliteCommand
    // is leaked to the caller (the reader can't outlive its command anyway).
    public static void Query(
        this SqliteConnection conn, string sql, Action<SqliteDataReader> read,
        params (string name, object? value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql; // nosemgrep: csharp-sqli — literal SQL only; all values bound via SqliteParameter
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        read(r);
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record StatsResult(
    int Total,
    int Downloaded,
    int Pending,
    int Ignored,
    double Coverage,
    int AddedThisWeek,
    List<Dictionary<string, object?>> RecentActivity,
    List<Dictionary<string, object?>> RecentlyAdded);

/// <summary>
/// A movie as reported by a library source. There is no id: identity is the resolved
/// local folder, and the stored id is derived from it via <see cref="MediaFolderId"/>.
/// </summary>
public record MovieRecord(
    string Folder,
    string Source,
    string SourceRef,
    string Title,
    int? Year,
    string SourcePath);

/// <summary>
/// A TV show as reported by a library source. Identity is the resolved local (show
/// root) folder; the stored id is derived from it via <see cref="Themearr.API.Services.MediaFolderId"/>.
/// <paramref name="HasPlexTheme"/> is true when Plex already provides a theme for the
/// show (its `theme` attribute is present) — such shows are not download candidates.
/// </summary>
public record ShowRecord(
    string Folder, string Source, string SourceRef, string Title, int? Year, string SourcePath, bool HasPlexTheme);
