# B1: Source Seam & Folder Identity — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decouple Themearr's movie identity from Plex — a movie becomes its resolved local folder — behind an `ILibrarySource` seam, with **no user-visible change**.

**Architecture:** Path resolution moves out of `PlexService` into a shared `LocalFolderResolver`, because both current and future sources report paths from their own filesystem's perspective and only resolution makes two strings the same folder. `SyncService` stops knowing what Plex is: it asks a resolver for the active `ILibrarySource`, receives `DiscoveredMovie` records, and upserts them keyed by folder. The `movies` table is rebuilt with a folder-derived primary key, carrying every existing status, ignore flag and history row across.

**Tech Stack:** .NET 10 (ASP.NET Core), `Microsoft.Data.Sqlite`, xUnit, React 19 + Vite.

**Spec:** `docs/superpowers/specs/2026-07-19-radarr-library-source-design.md` (stage B1 only)

## Global Constraints

- **B1 must be invisible.** Same movies, same statuses, same ignore flags, same history, same posters, same 24h sync cadence. The success criterion is that a user cannot tell it shipped. Any behaviour change other than pruning is a bug.
- **Radarr is out of scope.** No Radarr client, no settings, no wizard changes, no `LibrarySourceCheck`, no source-aware posters. Those are stage B2. Build no abstraction member that B1 does not use.
- **The migration runs inside a transaction.** SQLite supports transactional DDL. A failure must roll back and leave the app on the old schema — never a renamed table with no replacement.
- **Prune only after a sync that both succeeded and returned a non-zero count.** An unguarded prune plus a failed sync empties the library.
- Movie ids are the first 16 hex characters of the SHA-256 of the normalised folder. Normalisation: trailing directory separator trimmed, ordinal comparison, **no case folding** (Themearr runs on Linux, where paths are case-sensitive).
- Target framework `net10.0`, nullable reference types enabled, primary constructors, matching the style of `src/Themearr.API/Services/`.
- Backend tests: `dotnet test` from the repository root. The suite currently has **148** tests, all passing.
- Frontend checks from `src/Themearr.Web`: `npx tsc --noEmit`, `npm run lint` (expect 0 errors and 3 pre-existing warnings — 1 in `src/app/login/page.tsx`, 2 in `src/lib/auth.tsx`), `npm run build`.

---

### Task 1: `MovieFolderId`

The identity function. Pure, no dependencies.

**Files:**
- Create: `src/Themearr.API/Services/MovieFolderId.cs`
- Test: `tests/Themearr.API.Tests/MovieFolderIdTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `MovieFolderId.For(string folder) -> string` (16 lowercase hex chars)

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/MovieFolderIdTests.cs`:

```csharp
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class MovieFolderIdTests
{
    [Fact]
    public void Is_stable_across_calls()
    {
        Assert.Equal(MovieFolderId.For("/movies/Heat (1995)"), MovieFolderId.For("/movies/Heat (1995)"));
    }

    [Fact]
    public void Is_sixteen_lowercase_hex_characters()
    {
        var id = MovieFolderId.For("/movies/Heat (1995)");

        Assert.Equal(16, id.Length);
        Assert.Matches("^[0-9a-f]{16}$", id);
    }

    [Fact]
    public void A_trailing_separator_does_not_change_the_id()
    {
        Assert.Equal(MovieFolderId.For("/movies/Heat (1995)"), MovieFolderId.For("/movies/Heat (1995)/"));
    }

    [Fact]
    public void Different_folders_get_different_ids()
    {
        Assert.NotEqual(MovieFolderId.For("/movies/Heat (1995)"), MovieFolderId.For("/movies/Ronin (1998)"));
    }

    [Fact]
    public void Case_is_significant_because_linux_paths_are()
    {
        Assert.NotEqual(MovieFolderId.For("/movies/heat (1995)"), MovieFolderId.For("/movies/Heat (1995)"));
    }

    [Fact]
    public void An_empty_folder_yields_an_empty_id()
    {
        Assert.Equal("", MovieFolderId.For(""));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~MovieFolderIdTests`
Expected: FAIL — `The name 'MovieFolderId' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `src/Themearr.API/Services/MovieFolderId.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Themearr.API.Services;

/// <summary>
/// Derives a movie's stable id from the local folder its theme lives in.
///
/// The folder is the real identity — it is what Themearr acts on, and every library
/// source can name it — but folders are not usable as ids directly: they appear in
/// URLs like /api/movies/{id}/theme, where a raw path needs escaping, reads badly,
/// and leaks the server's filesystem layout to the browser. Hashing keeps the id
/// short and URL-safe while staying derivable from the folder alone, so no mapping
/// table is ever stored.
/// </summary>
public static class MovieFolderId
{
    /// <summary>
    /// Case is significant: Themearr runs on Linux, where two folders differing only
    /// in case are genuinely different folders.
    /// </summary>
    public static string For(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return "";

        var normalised = folder.TrimEnd('/', '\\');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~MovieFolderIdTests`
Expected: PASS — 6 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/MovieFolderId.cs tests/Themearr.API.Tests/MovieFolderIdTests.cs
git commit -m "feat(sources): derive stable movie ids from the local folder"
```

---

### Task 2: `LocalFolderResolver`

Extracts path resolution out of `PlexService`. **The highest-risk task in this plan** — every existing user's movies resolve through this code.

**Files:**
- Create: `src/Themearr.API/Services/LocalFolderResolver.cs`
- Modify: `src/Themearr.API/Services/PlexService.cs` (delete three private methods, call the resolver)
- Test: `tests/Themearr.API.Tests/LocalFolderResolverTests.cs`

**Interfaces:**
- Consumes: `Database.GetPathMappings()`, `Database.GetLibraryPaths()`, `Database.GetSetting(key, default)`, `PlexPath.{ParentDir,ApplyMapping,Segments}`
- Produces: `LocalFolderResolver.Resolve(string sourceFilePath) -> (string folder, string mode)` where `mode` is `direct` | `mapping` | `suffix` | `unresolved`

**Why the tests come first even though this is a move:** the tests are written from reading the current implementation, before the new class exists. That is what catches a transcription slip during the move — the exact failure mode this task risks. Do not extract first and test after.

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/LocalFolderResolverTests.cs`:

```csharp
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class LocalFolderResolverTests
{
    private static (LocalFolderResolver Resolver, Database Db) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return (new LocalFolderResolver(db), db);
    }

    [Fact]
    public void A_path_that_exists_resolves_directly()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);

        var (folder, mode) = resolver.Resolve(Path.Combine(movieDir, "heat.mkv"));

        Assert.Equal(movieDir, folder);
        Assert.Equal("direct", mode);
    }

    [Fact]
    public void A_configured_mapping_is_applied_when_the_reported_path_does_not_exist()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetPathMappings([new Dictionary<string, string>
        {
            ["source"] = "/mnt/plex/Movies",
            ["target"] = dir.Path,
        }]);

        var (folder, mode) = resolver.Resolve("/mnt/plex/Movies/Heat (1995)/heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("mapping", mode);
    }

    [Fact]
    public void A_windows_style_path_is_mapped_despite_backslashes()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetPathMappings([new Dictionary<string, string>
        {
            ["source"] = @"P:\Movies",
            ["target"] = dir.Path,
        }]);

        var (folder, mode) = resolver.Resolve(@"P:\Movies\Heat (1995)\heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("mapping", mode);
    }

    [Fact]
    public void With_no_mapping_the_folder_is_found_by_suffix_under_a_library_path()
    {
        using var dir = new TempDir();
        var (resolver, db) = New(dir);
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        db.SetLibraryPaths([dir.Path]);

        var (folder, mode) = resolver.Resolve("/somewhere/else/Heat (1995)/heat.mkv");

        Assert.Equal(movieDir, folder);
        Assert.Equal("suffix", mode);
    }

    [Fact]
    public void An_unknown_path_with_nothing_configured_is_unresolved()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);

        var (folder, mode) = resolver.Resolve("/mnt/nowhere/Heat (1995)/heat.mkv");

        Assert.Equal("", folder);
        Assert.Equal("unresolved", mode);
    }

    [Fact]
    public void An_empty_path_is_unresolved_rather_than_throwing()
    {
        using var dir = new TempDir();
        var (resolver, _) = New(dir);

        var (folder, mode) = resolver.Resolve("");

        Assert.Equal("", folder);
        Assert.Equal("unresolved", mode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~LocalFolderResolverTests`
Expected: FAIL — `The type or namespace name 'LocalFolderResolver' could not be found`.

- [ ] **Step 3: Create the resolver by moving the code verbatim**

Create `src/Themearr.API/Services/LocalFolderResolver.cs`. The three method bodies below are copied unchanged from `PlexService` — do not "improve" them while moving:

```csharp
using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Turns a path reported by a library source into a folder on Themearr's own
/// filesystem.
///
/// This is not a Plex concern. Any tool that reports paths — Plex on Windows, Radarr
/// in a container — sees a different filesystem than Themearr does, so its paths must
/// be translated before they mean anything here. It also underpins movie identity:
/// two sources describe the same movie with different path strings, and only after
/// resolution do those strings become the same folder.
/// </summary>
public class LocalFolderResolver(Database db)
{
    /// <summary>
    /// Returns the local folder and how it was found: <c>direct</c>, <c>mapping</c>,
    /// <c>suffix</c>, or <c>unresolved</c> with an empty folder.
    /// </summary>
    public (string folder, string mode) Resolve(string sourceFilePath)
    {
        // Normalize '\' → '/' so a Windows Plex server's paths resolve when Themearr
        // runs in a Linux container (otherwise the parent dir comes back empty).
        var parent = PlexPath.ParentDir(sourceFilePath);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            return (parent, "direct");

        var mapped = ApplyPathMappings(sourceFilePath);
        if (!string.IsNullOrEmpty(mapped) && Directory.Exists(mapped))
            return (mapped, "mapping");

        var suffix = FindBySuffix(sourceFilePath);
        if (!string.IsNullOrEmpty(suffix)) return (suffix, "suffix");

        return ("", "unresolved");
    }

    private string ApplyPathMappings(string sourceFilePath)
    {
        var sourceParent = PlexPath.ParentDir(sourceFilePath);
        foreach (var mapping in db.GetPathMappings())
        {
            var mapped = PlexPath.ApplyMapping(
                sourceParent,
                mapping.GetValueOrDefault("source", ""),
                mapping.GetValueOrDefault("target", ""));
            if (!string.IsNullOrEmpty(mapped)) return mapped;
        }
        return "";
    }

    private string FindBySuffix(string sourceFilePath)
    {
        var roots = db.GetLibraryPaths().Where(Directory.Exists).ToList();
        if (roots.Count == 0) return "";

        var sourceParts = PlexPath.Segments(PlexPath.ParentDir(sourceFilePath));
        if (sourceParts.Length == 0) return "";

        var maxSuffix = Math.Min(6, sourceParts.Length);
        foreach (var root in roots)
            for (var size = maxSuffix; size > 0; size--)
            {
                var candidate = Path.Combine(new[] { root }.Concat(sourceParts[^size..]).ToArray());
                if (Directory.Exists(candidate)) return candidate;
            }

        var target = sourceParts[^1].ToLower();
        var maxDirs = int.Parse(db.GetSetting("max_search_dirs", "20000"));
        var maxDepth = int.Parse(db.GetSetting("search_depth", "4"));
        var visited = 0;

        foreach (var root in roots)
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (++visited > maxDirs) return "";
                var depth = dir[root.Length..].Count(c => c == Path.DirectorySeparatorChar);
                if (depth > maxDepth) continue;
                if (Path.GetFileName(dir).ToLower() == target) return dir;
            }
        return "";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LocalFolderResolverTests`
Expected: PASS — 6 tests passed.

- [ ] **Step 5: Delete the originals from `PlexService` and delegate**

In `src/Themearr.API/Services/PlexService.cs`:

Change the class declaration from:

```csharp
public class PlexService(HttpClient http, Database db)
```

to:

```csharp
public class PlexService(HttpClient http, Database db, LocalFolderResolver folders)
```

Delete the three private methods `ResolveLocalFolder`, `ApplyPathMappings` and `FindBySuffix` in their entirety (they sit under the `// ── Path resolution ──` comment; delete that comment too).

In `FetchMoviesAsync`, change the call site from:

```csharp
                    var (folder, mode) = ResolveLocalFolder(filePath);
```

to:

```csharp
                    var (folder, mode) = folders.Resolve(filePath);
```

- [ ] **Step 6: Register the resolver**

In `src/Themearr.API/Program.cs`, immediately after the line `builder.Services.AddSingleton<Database>(_ => new Database(dbPath));` add:

```csharp
// Shared by every library source: the tool reporting paths sees a different
// filesystem than Themearr does.
builder.Services.AddSingleton<LocalFolderResolver>();
```

- [ ] **Step 7: Build and run the whole suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded with 0 warnings; **154 tests passing** (148 + 6).

- [ ] **Step 8: Commit**

```bash
git add src/Themearr.API/Services/LocalFolderResolver.cs src/Themearr.API/Services/PlexService.cs \
        src/Themearr.API/Program.cs tests/Themearr.API.Tests/LocalFolderResolverTests.cs
git commit -m "refactor(sources): extract path resolution out of PlexService"
```

---

### Task 3: The schema migration

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (add `MigrateMoviesTableV4`, call it from `Init`, update the `CREATE TABLE movies` statement)
- Test: `tests/Themearr.API.Tests/MoviesMigrationTests.cs`

**Interfaces:**
- Consumes: `MovieFolderId.For(string)` (Task 1)
- Produces: the `movies` table shape every later task reads — `id`, `folderName`, `source`, `source_ref`, `title`, `year`, `sourcePath`, `status`, `ignored`, `synced_at`

Follow the existing convention in this file: each migration is a `private static void MigrateMoviesTableVn(SqliteConnection conn)` that detects its own applicability with `PRAGMA table_info(movies)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/MoviesMigrationTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~MoviesMigrationTests`
Expected: FAIL — the migration does not exist, so `GetMovie(MovieFolderId.For(...))` returns null and the first assertion fails on `Assert.NotNull`.

- [ ] **Step 3: Update the `CREATE TABLE movies` statement**

In `src/Themearr.API/Data/Database.cs`, replace the existing `CREATE TABLE IF NOT EXISTS movies (...)` block with:

```csharp
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
```

- [ ] **Step 4: Add the migration**

In `src/Themearr.API/Data/Database.cs`, add this method beside the other `MigrateMoviesTable*` methods:

```csharp
    /// <summary>
    /// Re-keys movies from Plex identifiers to their local folder.
    ///
    /// Runs in a transaction: the earlier rebuild-style migration in this file renames
    /// the table before recreating it, so a failure partway would leave an install with
    /// no movies table at all. SQLite supports transactional DDL, so a failure here
    /// rolls back and the app starts on the old schema instead.
    /// </summary>
    private static void MigrateMoviesTableV4(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(movies)";
        var columns = new HashSet<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(1));

        if (columns.Contains("source") || !columns.Contains("plex_rating_key")) return;

        // old id → new id, for rewriting history afterwards
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<(string NewId, string Folder, string Source, string SourceRef,
                             string Title, object? Year, string SourcePath, string Status, long Ignored)>();
        var seenFolders = new HashSet<string>(StringComparer.Ordinal);

        conn.Query(
            "SELECT id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status, ignored FROM movies",
            r =>
            {
                while (r.Read())
                {
                    var oldId  = r.GetString(0);
                    var folder = r.IsDBNull(6) ? "" : r.GetString(6);
                    // Pre-resolution rows have no folder, so they cannot be acted on.
                    if (string.IsNullOrEmpty(folder)) continue;

                    var newId = MovieFolderId.For(folder);
                    remap[oldId] = newId;
                    // Two cuts in one folder are one movie here; first wins.
                    if (!seenFolders.Add(folder)) continue;

                    rows.Add((newId, folder, "plex", $"{r.GetString(1)}:{r.GetString(2)}",
                              r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4),
                              r.IsDBNull(5) ? "" : r.GetString(5),
                              r.GetString(7), r.IsDBNull(8) ? 0L : r.GetInt64(8)));
                }
            });

        using var tx = conn.BeginTransaction();

        conn.Execute("DROP TABLE IF EXISTS movies_v4_old");
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
                VALUES (@id, @f, @src, @ref, @t, @y, @sp, @s, @ig, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                """,
                ("@id", row.NewId), ("@f", row.Folder), ("@src", row.Source), ("@ref", row.SourceRef),
                ("@t", row.Title), ("@y", row.Year ?? (object)DBNull.Value), ("@sp", row.SourcePath),
                ("@s", row.Status), ("@ig", row.Ignored));

        // History rows already carry title and year, so any that fail to remap still
        // display correctly rather than going blank.
        foreach (var (oldId, newId) in remap)
            conn.Execute("UPDATE theme_history SET movie_id = @new WHERE movie_id = @old",
                ("@new", newId), ("@old", oldId));

        conn.Execute("DROP TABLE movies_v4_old");
        tx.Commit();
    }
```

- [ ] **Step 5: Call it from `Init`**

In `src/Themearr.API/Data/Database.cs`, find:

```csharp
        MigrateMoviesTableV3(conn);
```

and add immediately after it:

```csharp
        MigrateMoviesTableV4(conn);
```

- [ ] **Step 6: Run the migration tests**

Run: `dotnet test --filter FullyQualifiedName~MoviesMigrationTests`
Expected: FAIL on the tests that read `source`/`sourceRef`/`movieId` through `GetMovie`, `GetAllMovies` and `GetThemeHistory` — those readers still select the old columns. Task 4 fixes them. The re-keying tests that only check `status`, `ignored` and `folderName` may already pass.

Do not fix the readers here; commit the migration and move on.

- [ ] **Step 7: Commit**

```bash
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/MoviesMigrationTests.cs
git commit -m "feat(db): re-key movies by local folder in a transactional migration"
```

---

### Task 4: Update the `Database` movie API

Brings every reader and writer onto the new columns, turning Task 3's tests green.

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs`

**Interfaces:**
- Consumes: `MovieFolderId.For(string)` (Task 1)
- Produces:
  - `record MovieRecord(string Folder, string Source, string SourceRef, string Title, int? Year, string SourcePath)` — **no `Id` field; the id is derived from `Folder`**
  - `UpsertMovies(IEnumerable<MovieRecord>)` — keyed on `folderName`
  - `PruneMoviesExcept(IEnumerable<string> keptFolders) -> int`
  - movie dictionaries now expose `source` and `sourceRef` instead of `plexServerId` and `plexRatingKey`

- [ ] **Step 1: Replace `MovieRecord`**

In `src/Themearr.API/Data/Database.cs`, replace:

```csharp
public record MovieRecord(
    string Id,
    string PlexServerId,
    string PlexRatingKey,
    string Title,
    int? Year,
    string SourcePath,
    string FolderName);
```

with:

```csharp
/// <summary>
/// A movie as reported by a library source. There is no id: identity is the resolved
/// local folder, and the stored id is derived from it via <see cref="MovieFolderId"/>.
/// </summary>
public record MovieRecord(
    string Folder,
    string Source,
    string SourceRef,
    string Title,
    int? Year,
    string SourcePath);
```

- [ ] **Step 2: Rewrite `UpsertMovies`**

Replace the whole `UpsertMovies` method with:

```csharp
    public void UpsertMovies(IEnumerable<MovieRecord> movies)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var m in movies)
        {
            if (string.IsNullOrEmpty(m.Folder)) continue;
            var id = MovieFolderId.For(m.Folder);
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
    /// failed or empty sync would empty the library. Returns the number removed.
    /// </summary>
    public int PruneMoviesExcept(IEnumerable<string> keptFolders)
    {
        var keep = keptFolders.Where(f => !string.IsNullOrEmpty(f)).ToHashSet(StringComparer.Ordinal);
        if (keep.Count == 0) return 0;

        using var conn = Open();
        var doomed = new List<string>();
        conn.Query("SELECT id, folderName FROM movies", r =>
        {
            while (r.Read())
                if (!keep.Contains(r.GetString(1))) doomed.Add(r.GetString(0));
        });

        using var tx = conn.BeginTransaction();
        foreach (var id in doomed)
            conn.Execute("DELETE FROM movies WHERE id = @id", ("@id", id));
        tx.Commit();
        return doomed.Count;
    }
```

- [ ] **Step 3: Update every movie reader**

In the same file, change the three movie `SELECT` statements to the new columns.

In `GetAllMovies`, replace the SQL string with:

```csharp
"SELECT id, folderName, source, source_ref, title, year, sourcePath, status, ignored FROM movies ORDER BY status, title"
```

In `GetMovie`, replace the SQL string with:

```csharp
"SELECT id, folderName, source, source_ref, title, year, sourcePath, status, ignored FROM movies WHERE id = @id"
```

Then update `ReadMovieRow` so the dictionary it builds exposes `source` and `sourceRef` in place of `plexServerId` and `plexRatingKey`, keeping every other key (`id`, `folderName`, `title`, `year`, `sourcePath`, `status`, `ignored`) exactly as it is. Read the method and adjust the ordinals to match the SELECT above.

In `GetStats`, replace the `recentlyAdded` query and its reader block so it selects `id, source, source_ref, title, year, synced_at` and emits `["source"]` and `["sourceRef"]` instead of `["plexServerId"]` and `["plexRatingKey"]`.

- [ ] **Step 4: Run the full suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: this will FAIL to build until Task 5 updates `PlexService`, which still constructs the old `MovieRecord`. That is expected — proceed to Task 5 and build there.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Data/Database.cs
git commit -m "feat(db): key the movie API on folders and add guarded pruning"
```

---

### Task 5: The `ILibrarySource` seam

**Files:**
- Create: `src/Themearr.API/Services/Sources/ILibrarySource.cs`
- Create: `src/Themearr.API/Services/Sources/PlexLibrarySource.cs`
- Create: `src/Themearr.API/Services/Sources/LibrarySourceResolver.cs`
- Modify: `src/Themearr.API/Services/PlexService.cs` (build the new `MovieRecord`)
- Modify: `src/Themearr.API/Program.cs`
- Test: `tests/Themearr.API.Tests/LibrarySourceResolverTests.cs`

**Interfaces:**
- Consumes: `MovieRecord` (Task 4), `LocalFolderResolver` (Task 2)
- Produces:
  - `ILibrarySource` with `string Name`, `TimeSpan SyncInterval`, `Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct)`
  - `LibrarySourceResolver.Active` → the configured `ILibrarySource`

`ILibrarySource` deliberately carries **only** what B1 uses. `FetchPosterAsync` and `CheckAsync` from the spec arrive in B2 alongside the components that call them — adding them now would be unused members.

- [ ] **Step 1: Write the failing test**

Create `tests/Themearr.API.Tests/LibrarySourceResolverTests.cs`:

```csharp
using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class LibrarySourceResolverTests
{
    private sealed class FakeSource(string name) : ILibrarySource
    {
        public string   Name         => name;
        public TimeSpan SyncInterval => TimeSpan.FromHours(24);

        public Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MovieRecord>>([]);
    }

    private static LibrarySourceResolver New(TempDir dir, string? configured, out Database db)
    {
        db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        if (configured is not null) db.SetSetting("library_source", configured);
        return new LibrarySourceResolver(db, [new FakeSource("plex"), new FakeSource("radarr")]);
    }

    [Fact]
    public void Defaults_to_plex_when_nothing_is_configured()
    {
        using var dir = new TempDir();
        var resolver = New(dir, null, out _);

        Assert.Equal("plex", resolver.Active.Name);
    }

    [Fact]
    public void Uses_the_configured_source()
    {
        using var dir = new TempDir();
        var resolver = New(dir, "radarr", out _);

        Assert.Equal("radarr", resolver.Active.Name);
    }

    [Fact]
    public void An_unknown_configured_source_falls_back_to_plex_rather_than_throwing()
    {
        using var dir = new TempDir();
        var resolver = New(dir, "jellyfin", out _);

        Assert.Equal("plex", resolver.Active.Name);
    }

    [Fact]
    public void The_setting_is_read_each_time_so_a_change_takes_effect_without_a_restart()
    {
        using var dir = new TempDir();
        var resolver = New(dir, "plex", out var db);

        db.SetSetting("library_source", "radarr");

        Assert.Equal("radarr", resolver.Active.Name);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~LibrarySourceResolverTests`
Expected: FAIL — `The type or namespace name 'Sources' does not exist in the namespace 'Themearr.API.Services'`.

- [ ] **Step 3: Write the interface and resolver**

Create `src/Themearr.API/Services/Sources/ILibrarySource.cs`:

```csharp
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Something Themearr can read a movie library from. Implementations own their own
/// API and their own path quirks, and hand back movies already resolved to local
/// folders — the folder being the identity Themearr keys everything on.
/// </summary>
public interface ILibrarySource
{
    /// <summary>Stable key stored in the <c>library_source</c> setting.</summary>
    string Name { get; }

    /// <summary>
    /// How often a full sync is worth running. This is a property of the source, not
    /// of Themearr: scanning Plex is expensive, so it is measured in hours.
    /// </summary>
    TimeSpan SyncInterval { get; }

    Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct);
}
```

Create `src/Themearr.API/Services/Sources/LibrarySourceResolver.cs`:

```csharp
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Picks the configured library source. Exactly one source is active at a time, so
/// there is no merging or precedence to reason about.
/// </summary>
public class LibrarySourceResolver(Database db, IEnumerable<ILibrarySource> sources)
{
    private readonly IReadOnlyList<ILibrarySource> _sources = sources.ToList();

    /// <summary>
    /// The active source. Read fresh each time so changing the setting takes effect
    /// without a restart. An unrecognised value falls back to Plex rather than
    /// throwing — a bad setting must not take the app down.
    /// </summary>
    public ILibrarySource Active
    {
        get
        {
            var configured = db.GetSetting("library_source", "plex");
            return _sources.FirstOrDefault(s => s.Name == configured)
                ?? _sources.First(s => s.Name == "plex");
        }
    }
}
```

- [ ] **Step 4: Write the Plex source and update `PlexService`**

Create `src/Themearr.API/Services/Sources/PlexLibrarySource.cs`:

```csharp
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Plex as a library source. A thin adapter: all of the Plex API work stays in
/// <see cref="PlexService"/>, which is left untouched apart from the record it builds.
/// </summary>
public class PlexLibrarySource(PlexService plex) : ILibrarySource
{
    public string Name => "plex";

    /// <summary>Scanning a Plex library is expensive, so once a day.</summary>
    public TimeSpan SyncInterval => TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
        await plex.FetchMoviesAsync(log);
}
```

In `src/Themearr.API/Services/PlexService.cs`, inside `FetchMoviesAsync`, replace:

```csharp
                    result.Add(new MovieRecord(movieId, serverId, ratingKey, title, year, filePath, folder));
```

with:

```csharp
                    // source_ref keeps BOTH identifiers: PlexImageUrl needs the server as
                    // well as the rating key, so a rating key alone would break posters
                    // for anyone running more than one Plex server.
                    result.Add(new MovieRecord(folder, "plex", movieId, title, year, filePath));
```

`movieId` is already `$"{serverId}:{ratingKey}"`, which is exactly the pair posters need. The now-unused `seen` de-duplication on `movieId` stays as it is — it prevents fetching the same Plex item twice.

- [ ] **Step 5: Register everything**

In `src/Themearr.API/Program.cs`, immediately after the `AddSingleton<LocalFolderResolver>()` line added in Task 2, add:

```csharp
builder.Services.AddSingleton<Themearr.API.Services.Sources.PlexLibrarySource>();
builder.Services.AddSingleton<Themearr.API.Services.Sources.ILibrarySource>(
    sp => sp.GetRequiredService<Themearr.API.Services.Sources.PlexLibrarySource>());
builder.Services.AddSingleton<Themearr.API.Services.Sources.LibrarySourceResolver>();
```

- [ ] **Step 6: Build and run the seam tests**

Run: `dotnet build src/Themearr.API && dotnet test --filter FullyQualifiedName~LibrarySourceResolverTests`
Expected: Build succeeded, 0 warnings; 4 tests passed.

- [ ] **Step 7: Commit**

```bash
git add src/Themearr.API/Services/Sources/ src/Themearr.API/Services/PlexService.cs src/Themearr.API/Program.cs \
        tests/Themearr.API.Tests/LibrarySourceResolverTests.cs
git commit -m "feat(sources): add the ILibrarySource seam with Plex as the first source"
```

---

### Task 6: Sync through the seam, with guarded pruning

**Files:**
- Modify: `src/Themearr.API/Services/SyncService.cs`
- Modify: `src/Themearr.API/Services/AutoSyncService.cs` (take the interval from the source)
- Test: `tests/Themearr.API.Tests/PruneTests.cs`

**Interfaces:**
- Consumes: `LibrarySourceResolver.Active` (Task 5), `Database.PruneMoviesExcept` (Task 4)
- Produces: no new public API

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/PruneTests.cs`:

```csharp
using Themearr.API.Data;

namespace Themearr.API.Tests;

public class PruneTests
{
    private static Database Seeded(TempDir dir, params string[] folders)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.UpsertMovies(folders.Select(f =>
            new MovieRecord(f, "plex", "srv1:1", Path.GetFileName(f), 1995, f + "/file.mkv")));
        return db;
    }

    [Fact]
    public void Movies_absent_from_the_latest_sync_are_removed()
    {
        using var dir = new TempDir();
        var db = Seeded(dir, "/movies/Heat (1995)", "/movies/Ronin (1998)");

        var removed = db.PruneMoviesExcept(["/movies/Heat (1995)"]);

        Assert.Equal(1, removed);
        var kept = Assert.Single(db.GetAllMovies());
        Assert.Equal("/movies/Heat (1995)", kept["folderName"]?.ToString());
    }

    [Fact]
    public void An_empty_kept_set_removes_nothing()
    {
        using var dir = new TempDir();
        var db = Seeded(dir, "/movies/Heat (1995)", "/movies/Ronin (1998)");

        var removed = db.PruneMoviesExcept([]);

        Assert.Equal(0, removed);
        Assert.Equal(2, db.GetAllMovies().Count);
    }

    [Fact]
    public void Pruning_with_everything_present_removes_nothing()
    {
        using var dir = new TempDir();
        var db = Seeded(dir, "/movies/Heat (1995)", "/movies/Ronin (1998)");

        Assert.Equal(0, db.PruneMoviesExcept(["/movies/Heat (1995)", "/movies/Ronin (1998)"]));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PruneTests`
Expected: PASS if Task 4 is complete — `PruneMoviesExcept` already exists. If they pass, that is correct; these tests lock the guard behaviour for the reviewer. If they fail, fix `PruneMoviesExcept` rather than the tests.

- [ ] **Step 3: Sync through the resolver and prune**

In `src/Themearr.API/Services/SyncService.cs`, change the class declaration from:

```csharp
public class SyncService(Database db, PlexService plex, ILogger<SyncService> log)
```

to:

```csharp
public class SyncService(Database db, LibrarySourceResolver sources, ILogger<SyncService> log)
```

Add `using Themearr.API.Services.Sources;` to the file's usings.

Then replace the body of the `try` block in `RunAsync` with:

```csharp
            var source = sources.Active;
            AddLog($"Starting {source.Name} sync...");
            var movies = await source.FetchAsync(AddLog, CancellationToken.None);
            AddLog($"Upserting {movies.Count} matched movies into the local database");
            db.UpsertMovies(movies);
            _synced = movies.Count;

            // Only prune after a sync that actually returned something: identity is the
            // folder now, so a mapping change re-keys everything and would otherwise
            // leave the old rows as permanent phantoms. Pruning on an empty result
            // would instead delete the entire library.
            if (movies.Count > 0)
            {
                var removed = db.PruneMoviesExcept(movies.Select(m => m.Folder));
                if (removed > 0) AddLog($"Removed {removed} movies no longer in the library");
            }

            AddLog($"Sync complete. {movies.Count} movies available locally.");
```

- [ ] **Step 4: Take the sync interval from the source**

In `src/Themearr.API/Services/AutoSyncService.cs`, add `using Themearr.API.Services.Sources;` and add `LibrarySourceResolver sources` to the primary constructor parameters.

Replace the field:

```csharp
    private static readonly TimeSpan SyncInterval  = TimeSpan.FromHours(24);
```

with:

```csharp
    // How often a sync is due is a property of the source, not of Themearr.
    private TimeSpan SyncInterval => sources.Active.SyncInterval;
```

The existing `registry.Register(SyncTaskId, "Sync Library", SyncInterval)` call now reads the source's value; with Plex as the only source this is still 24 hours, so behaviour is unchanged.

- [ ] **Step 5: Build and run the whole suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded with 0 warnings; **all tests passing**, including every `MoviesMigrationTests` case from Task 3.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Services/SyncService.cs src/Themearr.API/Services/AutoSyncService.cs \
        tests/Themearr.API.Tests/PruneTests.cs
git commit -m "feat(sync): sync through the source seam and prune removed movies"
```

---

### Task 7: Posters and the frontend movie type

**Files:**
- Modify: `src/Themearr.API/Controllers/PosterController.cs`
- Modify: `src/Themearr.Web/src/lib/types.ts`

**Interfaces:**
- Consumes: the `source` / `sourceRef` movie dictionary keys (Task 4)
- Produces: no new API

`PosterController` stays Plex-specific in B1 — making it source-aware is B2's job, alongside the Radarr source that needs it. All that changes here is where it reads the Plex identifiers from.

- [ ] **Step 1: Read the identifiers out of `source_ref`**

In `src/Themearr.API/Controllers/PosterController.cs`, replace:

```csharp
        var movie = db.GetMovie(id);
        var serverId = movie?.GetValueOrDefault("plexServerId")?.ToString() ?? "";
        var ratingKey = movie?.GetValueOrDefault("plexRatingKey")?.ToString() ?? "";
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(ratingKey))
            return NotFound();
```

with:

```csharp
        var movie = db.GetMovie(id);
        if (movie?.GetValueOrDefault("source")?.ToString() != "plex") return NotFound();

        // Plex stores "{serverId}:{ratingKey}" in source_ref because PlexImageUrl needs
        // both. The field is opaque to everything except the source that issued it.
        var parts = (movie.GetValueOrDefault("sourceRef")?.ToString() ?? "").Split(':', 2);
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty)) return NotFound();
        var (serverId, ratingKey) = (parts[0], parts[1]);
```

- [ ] **Step 2: Update the frontend type**

In `src/Themearr.Web/src/lib/types.ts`, in the `Movie` interface replace:

```ts
  plexServerId: string
  plexRatingKey: string
```

with:

```ts
  source: string
  sourceRef: string
```

No component reads these fields — they are declared only. Confirm with:

```bash
cd /Users/devlin/Documents/GitHub/themearr && grep -rn "plexServerId\|plexRatingKey" src/Themearr.Web/src/
```
Expected: no matches after the edit.

- [ ] **Step 3: Verify backend and frontend**

Run from the repository root:
```bash
dotnet build src/Themearr.API && dotnet test
cd src/Themearr.Web && npx tsc --noEmit && npm run lint && npm run build
```
Expected: build clean, all tests passing, typecheck clean, lint 0 errors with 3 pre-existing warnings, build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.API/Controllers/PosterController.cs src/Themearr.Web/src/lib/types.ts
git commit -m "refactor(posters): read Plex identifiers from source_ref"
```

---

### Task 8: End-to-end verification against a real upgrade

B1's whole claim is that nothing changed. That has to be demonstrated on a database created by the *previous* release, not by the new code.

**Files:** none — this task produces evidence, not code.

- [ ] **Step 1: Build a library on the old schema**

```bash
cd /Users/devlin/Documents/GitHub/themearr
SCRATCH=$(mktemp -d)
git stash list >/dev/null
git worktree add "$SCRATCH/old" v1.40.1
cd "$SCRATCH/old" && dotnet publish src/Themearr.API/Themearr.API.csproj -c Release -o "$SCRATCH/oldapp"
mkdir -p "$SCRATCH/movies/Heat (1995)" "$SCRATCH/movies/Ronin (1998)"
THEMEARR_AUTH_TOKEN=b1-verify-token-abcdef DB_PATH="$SCRATCH/themearr.db" \
  ASPNETCORE_URLS=http://127.0.0.1:5182 dotnet "$SCRATCH/oldapp/Themearr.API.dll" &
sleep 8
sqlite3 "$SCRATCH/themearr.db" \
  "INSERT OR REPLACE INTO settings (key,value) VALUES ('setup_complete','1');" \
  "INSERT INTO movies (id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status, ignored) VALUES ('srv1:101','srv1','101','Heat',1995,'/plex/Heat.mkv','$SCRATCH/movies/Heat (1995)','downloaded',0);" \
  "INSERT INTO movies (id, plex_server_id, plex_rating_key, title, year, sourcePath, folderName, status, ignored) VALUES ('srv1:102','srv1','102','Ronin',1998,'/plex/Ronin.mkv','$SCRATCH/movies/Ronin (1998)','pending',1);" \
  "INSERT INTO theme_history (movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at) VALUES ('srv1:101','Heat',1995,'Heat Theme','https://example.test/x','2026-01-01T00:00:00Z');"
kill %1
```

- [ ] **Step 2: Upgrade that database with the new build and compare**

```bash
cd /Users/devlin/Documents/GitHub/themearr
dotnet publish src/Themearr.API/Themearr.API.csproj -c Release -o "$SCRATCH/newapp"
THEMEARR_AUTH_TOKEN=b1-verify-token-abcdef DB_PATH="$SCRATCH/themearr.db" \
  ASPNETCORE_URLS=http://127.0.0.1:5183 dotnet "$SCRATCH/newapp/Themearr.API.dll" &
sleep 8
curl -s -H "Authorization: Bearer b1-verify-token-abcdef" http://127.0.0.1:5183/api/movies | python3 -m json.tool | head -40
curl -s -H "Authorization: Bearer b1-verify-token-abcdef" http://127.0.0.1:5183/api/history | python3 -m json.tool | head -20
kill %1
```

Expected, and all four must hold:
- Two movies are returned.
- Heat's `status` is still `downloaded`; Ronin's `ignored` is still true.
- Each `sourceRef` is `srv1:101` / `srv1:102`, and `source` is `plex`.
- The history entry's `movieId` equals the new folder-derived id for Heat, and its title and year are intact.

- [ ] **Step 3: Confirm the migration is idempotent and clean up**

```bash
sqlite3 "$SCRATCH/themearr.db" "PRAGMA table_info(movies);" | cut -d'|' -f2 | tr '\n' ' '
sqlite3 "$SCRATCH/themearr.db" "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '%_old' OR name LIKE '%_legacy';"
cd /Users/devlin/Documents/GitHub/themearr && git worktree remove "$SCRATCH/old" --force && rm -rf "$SCRATCH"
```

Expected: the column list is `id folderName source source_ref title year sourcePath status ignored synced_at`, and the second query returns **nothing** — no leftover rename artefacts.

- [ ] **Step 4: Record the result**

If any expectation above failed, stop and report it rather than adjusting the test — a migration that loses user data is the one failure this whole plan exists to prevent.

---

## Self-review notes

**Spec coverage (B1 rows only).** `ILibrarySource` + `LibrarySourceResolver` → Task 5; `PlexLibrarySource` → Task 5; `LocalFolderResolver` extraction → Task 2; folder identity → Task 1; schema migration → Tasks 3–4; pruning → Tasks 4 and 6; poster `source_ref` handling → Task 7; upgrade verification → Task 8.

**One deliberate deviation from the spec.** The spec lists `TaskRegistry.UpdateInterval` under B1. It is **not** in this plan: B1 has exactly one source, so the interval never changes, and an unused method is dead code a reviewer would rightly flag. It belongs in B2 with the second source that makes it meaningful. `AutoSyncService` now reads the interval from the active source (Task 6), which is the part B1 genuinely needs.

**Two spec members deliberately deferred.** `ILibrarySource.FetchPosterAsync` and `CheckAsync` are omitted for the same reason — nothing in B1 calls them. They arrive in B2 with `PosterController`'s source-awareness and `LibrarySourceCheck`.

**A known intermediate break.** Task 4 leaves the build red (it changes `MovieRecord` while `PlexService` still constructs the old shape), and Task 5 fixes it. This is called out in both tasks. It is the one place the plan cannot keep every commit independently green without merging two tasks that a reviewer should be able to judge separately.

**Type consistency.** `MovieRecord(Folder, Source, SourceRef, Title, Year, SourcePath)` is used identically in Tasks 4, 5 and 6. The dictionary keys `source` / `sourceRef` are produced in Task 4 and consumed in Tasks 7 (poster) and 3 (migration tests). `MovieFolderId.For` is defined in Task 1 and used in Tasks 3 and 4.
