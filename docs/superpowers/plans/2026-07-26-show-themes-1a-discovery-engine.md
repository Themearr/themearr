# Show Themes — Phase 1a (Discovery Engine) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Discover TV shows from selected Plex show libraries and persist them in a `shows` table with disk-derived status, so a later slice can download themes and show them in the UI. No downloads, no UI in this plan.

**Architecture:** A parallel show stack alongside movies — a new `shows` table, show-specific `Database` methods, a Plex show-fetch, and a `ShowSyncService` — reusing the generic primitives (`ThemeFiles`, `LocalFolderResolver`, `MediaFolderId`, `TaskRegistry`) unchanged. Movie code is untouched except one mechanical rename.

**Tech Stack:** .NET 10 Web API, `Microsoft.Data.Sqlite`, xUnit.

## Global Constraints

- **.NET 10**; tests are xUnit in `tests/Themearr.API.Tests/`. Follow existing code style.
- **Parallel, not generalized:** duplicate movie DB methods / sync against a `shows` table; do NOT generalize `ILibrarySource`, `Database`'s movie methods, or `SyncService`.
- **Status is disk-derived** exactly like movies: a non-empty `theme.*` (not `.part`/`.ytdl`) in the show's folder ⇒ `downloaded`, else `pending`, unless `ignored`.
- **Candidate rule:** a show is a theme candidate only when it has no local theme AND Plex reports no theme. The Plex-has-theme signal is stored as `shows.plex_has_theme` and excluded by `GetPendingShows`.
- **Show root folder** comes from Plex's `<Location path>` (a show's on-disk root), resolved through path-mappings via the existing `LocalFolderResolver` using the Radarr dummy-filename trick (`Resolve(root + "/placeholder.mkv")`).
- **Opt-in:** shows are only synced from libraries the operator has selected (`plex_selected_show_libraries` setting); an empty selection means no show sync runs.

---

### Task 1: Rename `MovieFolderId` → `MediaFolderId`

Mechanical, compiler-checked rename so both movies and shows share one folder-hash identity. Pure refactor — no behavior change.

**Files:**
- Rename: `src/Themearr.API/Services/MovieFolderId.cs` → `MediaFolderId.cs` (class `MovieFolderId` → `MediaFolderId`)
- Modify: every call site of `MovieFolderId.For(` (compiler will list them — includes `Database.cs`, and any service/controller using it)
- Rename test: `tests/Themearr.API.Tests/MovieFolderIdTests.cs` → `MediaFolderIdTests.cs` (class + references)

- [ ] **Step 1: Rename the class + file**

Rename the file and change `public static class MovieFolderId` to `public static class MediaFolderId`. Keep the body identical.

- [ ] **Step 2: Update all call sites**

Run `grep -rn "MovieFolderId" src tests --include='*.cs'` and replace every `MovieFolderId` with `MediaFolderId`. Rename `MovieFolderIdTests.cs` → `MediaFolderIdTests.cs`, class `MovieFolderIdTests` → `MediaFolderIdTests`.

- [ ] **Step 3: Build + full suite (the rename is verified by the compiler + existing tests)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS — same count as before, zero build warnings. A pure rename changes no behavior.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor: rename MovieFolderId to MediaFolderId (shared by movies + shows)"
```

---

### Task 2: `shows` table + `ShowRecord`

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (add the `CREATE TABLE shows` in `Init()`; add the `ShowRecord` DTO near `MovieRecord`)
- Test: `tests/Themearr.API.Tests/ShowsSchemaTests.cs` (create)

**Interfaces:**
- Produces: `public record ShowRecord(string Folder, string Source, string SourceRef, string Title, int? Year, string SourcePath, bool HasPlexTheme);`
- Produces: a `shows` table with columns `id, folderName, source, source_ref, title, year, sourcePath, status, ignored, synced_at, plex_has_theme`.

- [ ] **Step 1: Write the failing test** (`ShowsSchemaTests.cs`)

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowsSchemaTests"`
Expected: FAIL — `PRAGMA table_info(shows)` returns no columns (table missing).

- [ ] **Step 3: Add the table + DTO**

In `Database.Init()`, after the `movies` CREATE TABLE block, add:

```csharp
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
```

Near the `MovieRecord` record at the bottom of `Database.cs`, add:

```csharp
/// <summary>
/// A TV show as reported by a library source. Identity is the resolved local (show
/// root) folder; the stored id is derived from it via <see cref="Themearr.API.Services.MediaFolderId"/>.
/// <paramref name="HasPlexTheme"/> is true when Plex already provides a theme for the
/// show (its `theme` attribute is present) — such shows are not download candidates.
/// </summary>
public record ShowRecord(
    string Folder, string Source, string SourceRef, string Title, int? Year, string SourcePath, bool HasPlexTheme);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowsSchemaTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/ShowsSchemaTests.cs
git commit -m "feat: add shows table and ShowRecord"
```

---

### Task 3: Show `Database` methods

Parallel to the movie methods, against the `shows` table. Includes disk-derived status (mirroring `ReadMovieRow`) and the candidate filter.

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (add a `// ── Shows ──` region)
- Test: `tests/Themearr.API.Tests/ShowStoreTests.cs` (create)

**Interfaces:**
- Consumes: `ShowRecord` (Task 2), `MediaFolderId.For` (Task 1).
- Produces: `UpsertShows(IEnumerable<ShowRecord>)`, `PruneShowsExcept(IEnumerable<string> keptFolders) -> int`, `GetAllShows() -> List<Dictionary<string,object?>>`, `GetShow(string id) -> Dictionary<string,object?>?`, `GetPendingShows() -> List<Dictionary<string,object?>>`, `SetShowStatus(string id, string status)`, `SetShowIgnored(string id, bool ignored)`.

- [ ] **Step 1: Write the failing tests** (`ShowStoreTests.cs`)

```csharp
using Themearr.API.Data;

namespace Themearr.API.Tests;

public class ShowStoreTests
{
    private static Database New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    private static ShowRecord Rec(string folder, string title, bool hasPlexTheme = false) =>
        new(folder, "plex", "srv1:1", title, 2008, folder, hasPlexTheme);

    [Fact]
    public void UpsertShows_then_GetShow_roundtrips_identity_and_fields()
    {
        using var dir = new TempDir();
        var db = New(dir);
        var folder = Path.Combine(dir.Path, "Breaking Bad");
        Directory.CreateDirectory(folder);

        db.UpsertShows([Rec(folder, "Breaking Bad")]);

        var id = Themearr.API.Services.MediaFolderId.For(folder);
        var show = db.GetShow(id);
        Assert.NotNull(show);
        Assert.Equal("Breaking Bad", show!["title"]);
        Assert.Equal("pending", show["status"]);   // no theme.* on disk yet
    }

    [Fact]
    public void GetPendingShows_excludes_ignored_and_plex_provided_and_downloaded()
    {
        using var dir = new TempDir();
        var db = New(dir);
        var needs   = Path.Combine(dir.Path, "Needs");   Directory.CreateDirectory(needs);
        var plexHas = Path.Combine(dir.Path, "PlexHas"); Directory.CreateDirectory(plexHas);
        var onDisk  = Path.Combine(dir.Path, "OnDisk");  Directory.CreateDirectory(onDisk);
        File.WriteAllText(Path.Combine(onDisk, "theme.mp3"), "x");   // already downloaded on disk

        db.UpsertShows([Rec(needs, "Needs"), Rec(plexHas, "PlexHas", hasPlexTheme: true), Rec(onDisk, "OnDisk")]);

        var pending = db.GetPendingShows();
        Assert.Contains(pending, s => (string)s["title"]! == "Needs");
        Assert.DoesNotContain(pending, s => (string)s["title"]! == "PlexHas");   // Plex provides a theme
        // OnDisk stays status='pending' in the column (status is disk-derived at read time),
        // but GetPendingShows is the worker pre-filter keyed off the stored column, so it is
        // still listed here — the worker verifies the disk before acting (mirrors movies).
    }

    [Fact]
    public void PruneShowsExcept_removes_absent_shows_but_keeps_ignored()
    {
        using var dir = new TempDir();
        var db = New(dir);
        var keep    = Path.Combine(dir.Path, "Keep");    Directory.CreateDirectory(keep);
        var drop    = Path.Combine(dir.Path, "Drop");    Directory.CreateDirectory(drop);
        var ignored = Path.Combine(dir.Path, "Ignored"); Directory.CreateDirectory(ignored);
        db.UpsertShows([Rec(keep, "Keep"), Rec(drop, "Drop"), Rec(ignored, "Ignored")]);
        db.SetShowIgnored(Themearr.API.Services.MediaFolderId.For(ignored), true);

        var removed = db.PruneShowsExcept([keep]);

        Assert.Equal(1, removed);                                   // only Drop
        Assert.NotNull(db.GetShow(Themearr.API.Services.MediaFolderId.For(keep)));
        Assert.NotNull(db.GetShow(Themearr.API.Services.MediaFolderId.For(ignored)));  // ignored kept
        Assert.Null(db.GetShow(Themearr.API.Services.MediaFolderId.For(drop)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowStoreTests"`
Expected: FAIL to compile — the `Database` show methods don't exist.

- [ ] **Step 3: Implement the show methods** (in `Database.cs`, new `// ── Shows ──` region, mirroring the movie methods)

```csharp
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
```

`GetAllShows`/`GetShow` reuse `ReadMovieRow`'s disk-derived-status logic. Rename the existing private `ReadMovieRow` to **`ReadMediaRow`** (it is media-type-agnostic — it reads the shared column layout and derives status from disk) and update its two movie call sites. The `shows` SELECT column order matches what `ReadMediaRow` expects (id, folderName, source, source_ref, title, year, sourcePath, status, ignored).

- [ ] **Step 4: Run tests to verify they pass (+ full suite, since `ReadMovieRow`→`ReadMediaRow` touches movie reads)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS — new show tests green AND all movie tests still green (the rename is behavior-preserving).

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/ShowStoreTests.cs
git commit -m "feat: show Database methods (upsert/prune/get/pending/status/ignored)"
```

---

### Task 4: Plex show parsing + library-type listing + selected-show-libraries setting

Brings the show-detection parser onto `main` (extended to read the show's root folder from `<Location>`), generalizes `ListLibrariesAsync` to any library type, and adds accessors for the selected show libraries.

**Files:**
- Create: `src/Themearr.API/Services/PlexShowThemes.cs`
- Modify: `src/Themearr.API/Services/PlexService.cs` (`ListLibrariesAsync` gains `libraryType`)
- Modify: `src/Themearr.API/Data/Database.cs` (selected-show-libraries accessors)
- Test: `tests/Themearr.API.Tests/PlexShowThemesTests.cs` (create)

**Interfaces:**
- Produces: `public record PlexShow(string RatingKey, string Title, int? Year, bool HasTheme, string RootFolder);`
- Produces: `public static class PlexShowThemes { public static IReadOnlyList<PlexShow> Parse(string sectionXml); }`
- Produces: `ListLibrariesAsync(List<string> serverUrls, string serverToken, string libraryType = "movie")`
- Produces: `Database.GetSelectedShowLibraries() -> Dictionary<string, List<string>>`, `Database.SetSelectedShowLibraries(Dictionary<string, List<string>>)`.

- [ ] **Step 1: Write the failing test** (`PlexShowThemesTests.cs`)

```csharp
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexShowThemesTests
{
    // A Plex show-section (/library/sections/{key}/all?type=2) returns shows as <Directory>
    // elements. Each has a <Location path> (the show root folder) and, when Plex has a theme,
    // a `theme` attribute.
    private const string TwoShows = """
        <MediaContainer size="2">
          <Directory ratingKey="45" type="show" title="Breaking Bad" year="2008"
                     theme="/library/metadata/45/theme/1699999999">
            <Location id="1" path="/tv/Breaking Bad" />
          </Directory>
          <Directory ratingKey="46" type="show" title="The Wire" year="2002">
            <Location id="2" path="/tv/The Wire" />
          </Directory>
        </MediaContainer>
        """;

    [Fact]
    public void Parse_reads_root_folder_title_year_and_theme_presence()
    {
        var shows = PlexShowThemes.Parse(TwoShows);

        var bb = shows.Single(s => s.Title == "Breaking Bad");
        Assert.Equal("/tv/Breaking Bad", bb.RootFolder);
        Assert.Equal(2008, bb.Year);
        Assert.True(bb.HasTheme);

        var wire = shows.Single(s => s.Title == "The Wire");
        Assert.Equal("/tv/The Wire", wire.RootFolder);
        Assert.False(wire.HasTheme);
    }

    [Fact]
    public void Parse_uses_the_first_location_when_a_show_has_several()
    {
        const string merged = """
            <MediaContainer size="1">
              <Directory ratingKey="9" type="show" title="Doctor Who" year="2005">
                <Location id="1" path="/tv/Doctor Who (2005)" />
                <Location id="2" path="/tv2/Doctor Who" />
              </Directory>
            </MediaContainer>
            """;
        Assert.Equal("/tv/Doctor Who (2005)", PlexShowThemes.Parse(merged).Single().RootFolder);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~PlexShowThemesTests"`
Expected: FAIL to compile — `PlexShowThemes`/`PlexShow` don't exist.

- [ ] **Step 3: Implement the parser + generalize `ListLibrariesAsync` + add setting accessors**

Create `PlexShowThemes.cs`:

```csharp
using System.Xml.Linq;

namespace Themearr.API.Services;

/// <summary>One TV show from a Plex show-library section: its root folder, and whether
/// Plex already has a theme (`theme` attribute present — Plex-Pass-fetched or a local file).</summary>
public record PlexShow(string RatingKey, string Title, int? Year, bool HasTheme, string RootFolder);

/// <summary>Parses a Plex show listing (/library/sections/{key}/all?type=2) into
/// <see cref="PlexShow"/> records. Pure/HTTP-free so it can be fixture-tested.</summary>
public static class PlexShowThemes
{
    public static IReadOnlyList<PlexShow> Parse(string sectionXml)
    {
        var root = XDocument.Parse(sectionXml).Root
            ?? throw new InvalidOperationException("Plex section response had no root element.");
        return root.Elements("Directory").Select(d =>
        {
            var ratingKey = d.Attribute("ratingKey")?.Value?.Trim() ?? "";
            var title     = d.Attribute("title")?.Value?.Trim() ?? "";
            var year      = int.TryParse(d.Attribute("year")?.Value, out var y) ? y : (int?)null;
            var hasTheme  = !string.IsNullOrWhiteSpace(d.Attribute("theme")?.Value);
            // The show's on-disk root(s) come as <Location path>; use the first.
            var rootFolder = d.Elements("Location").FirstOrDefault()?.Attribute("path")?.Value?.Trim() ?? "";
            return new PlexShow(ratingKey, title, year, hasTheme, rootFolder);
        }).ToList();
    }
}
```

In `PlexService.ListLibrariesAsync`, add the `libraryType` parameter (default preserves movie behavior):

```csharp
public async Task<List<Dictionary<string, object>>> ListLibrariesAsync(
    List<string> serverUrls, string serverToken, string libraryType = "movie")
```

and change the two hardcoded `"movie"` usages inside it to `libraryType`, with a type-aware title fallback:

```csharp
    .Where(d => d.Attribute("type")?.Value?.ToLower() == libraryType)
    .Select(d => new Dictionary<string, object>
    {
        ["key"]   = d.Attribute("key")?.Value ?? "",
        ["title"] = d.Attribute("title")?.Value ?? (libraryType == "movie" ? "Movies" : "TV Shows"),
        ["type"]  = libraryType,
    })
```

In `Database.cs`, next to `GetSelectedLibraries`/`SetSelectedLibraries`, add:

```csharp
public Dictionary<string, List<string>> GetSelectedShowLibraries() =>
    GetJsonSetting("plex_selected_show_libraries", new Dictionary<string, List<string>>());

public void SetSelectedShowLibraries(Dictionary<string, List<string>> libs) =>
    SetJsonSetting("plex_selected_show_libraries", libs);
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS (parser tests green; movie library-listing unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/PlexShowThemes.cs src/Themearr.API/Services/PlexService.cs src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/PlexShowThemesTests.cs
git commit -m "feat: Plex show parser (root folder + theme) + library-type listing"
```

---

### Task 5: `PlexService.FetchShowsAsync` → `ShowRecord`s

Fetches shows from the **selected** Plex show libraries, resolving each show's root folder through path-mappings, and returns `ShowRecord`s ready for `UpsertShows`. Mirrors `FetchMoviesAsync`.

**Files:**
- Modify: `src/Themearr.API/Services/PlexService.cs`
- Test: `tests/Themearr.API.Tests/PlexFetchShowsTests.cs` (create)

**Interfaces:**
- Consumes: `PlexShowThemes.Parse` (Task 4), `ShowRecord` (Task 2), `LocalFolderResolver`, `Database.GetSelectedShowLibraries` (Task 4).
- Produces: `public async Task<List<ShowRecord>> FetchShowsAsync(Action<string>? logFn = null)`.

- [ ] **Step 1: Write the failing test** (`PlexFetchShowsTests.cs`)

```csharp
using System.Net;
using System.Text;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexFetchShowsTests
{
    private const string ServerUrl = "http://plex.local:32400";

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }
    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    [Fact]
    public async Task Fetches_shows_from_selected_libraries_with_resolved_root_and_theme_flag()
    {
        using var dir = new TempDir();
        // A real local show root so LocalFolderResolver resolves it "direct".
        var showRoot = Path.Combine(dir.Path, "Breaking Bad");
        Directory.CreateDirectory(showRoot);

        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetSetting("plex_access_token", "tok");
        db.SetSetting("plex_client_identifier", "client-1");
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"] = "srv1", ["name"] = "Tower", ["url"] = ServerUrl,
            ["urls"] = new List<string> { ServerUrl }, ["token"] = "tok",
        }]);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var sections = """
            <MediaContainer size="1"><Directory key="3" type="show" title="TV" /></MediaContainer>
            """;
        var items = $"""
            <MediaContainer size="1" totalSize="1">
              <Directory ratingKey="45" type="show" title="Breaking Bad" year="2008"
                         theme="/library/metadata/45/theme/1">
                <Location id="1" path="{showRoot}" />
              </Directory>
            </MediaContainer>
            """;
        var handler = new RoutingHandler(req =>
        {
            var p = req.RequestUri!.AbsolutePath;
            if (p == "/library/sections") return Xml(sections);
            if (p == "/library/sections/3/all") return Xml(items);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var shows = await plex.FetchShowsAsync();

        var s = Assert.Single(shows);
        Assert.Equal("Breaking Bad", s.Title);
        Assert.Equal(showRoot, s.Folder);   // resolved local root
        Assert.True(s.HasPlexTheme);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~PlexFetchShowsTests"`
Expected: FAIL to compile — `FetchShowsAsync` doesn't exist.

- [ ] **Step 3: Implement `FetchShowsAsync`** (in `PlexService.cs`, mirroring `FetchMoviesAsync` but for shows)

```csharp
public async Task<List<ShowRecord>> FetchShowsAsync(Action<string>? logFn = null)
{
    var accessToken = db.GetSetting("plex_access_token").Trim();
    var clientId    = db.GetSetting("plex_client_identifier").Trim();
    if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(clientId))
        throw new InvalidOperationException("Plex sign-in has not been completed");

    var servers  = db.GetPlexServers();
    var libMap   = db.GetSelectedShowLibraries();
    var result   = new List<ShowRecord>();
    var seen     = new HashSet<string>();
    var unresolvedCount = 0;
    var unresolvedSample = "";

    foreach (var srv in servers)
    {
        var serverId    = srv.GetValueOrDefault("id", "")?.ToString()?.Trim() ?? "";
        var serverName  = srv.GetValueOrDefault("name", "")?.ToString()?.Trim() ?? "";
        var primaryUrl  = srv.GetValueOrDefault("url", "")?.ToString()?.Trim() ?? "";
        var urlList     = srv.GetValueOrDefault("urls") is JsonElement je && je.ValueKind == JsonValueKind.Array
            ? je.EnumerateArray().Select(u => u.GetString() ?? "").Where(u => !string.IsNullOrEmpty(u)).ToList()
            : new List<string> { primaryUrl };
        var serverToken = srv.GetValueOrDefault("token", "")?.ToString()?.Trim() ?? "";
        if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(serverToken)) continue;

        var selectedKeys = libMap.GetValueOrDefault(serverId, []);
        if (selectedKeys.Count == 0) continue;   // opt-in: nothing selected → skip this server

        var libs = await ListLibrariesAsync(urlList, serverToken, "show");
        libs = libs.Where(l => selectedKeys.Contains(l["key"]?.ToString() ?? "")).ToList();
        logFn?.Invoke($"Scanning {libs.Count} show libraries on {serverName}");

        foreach (var lib in libs)
        {
            var sectionKey = lib["key"]?.ToString() ?? "";
            foreach (var show in await FetchShowsForSectionAsync(urlList, sectionKey, serverToken, clientId))
            {
                if (string.IsNullOrEmpty(show.RatingKey)) continue;
                var showId = $"{serverId}:{show.RatingKey}";
                if (!seen.Add(showId)) continue;
                if (string.IsNullOrEmpty(show.RootFolder))
                {
                    logFn?.Invoke($"Skipping {show.Title} — no folder reported by Plex");
                    continue;
                }
                // Reuse the file-path resolver by appending a dummy filename, so the show's
                // ROOT folder is resolved through path-mappings (same trick as RadarrLibrarySource).
                var (folder, _) = folders.Resolve(show.RootFolder.TrimEnd('/', '\\') + "/placeholder.mkv");
                if (string.IsNullOrEmpty(folder))
                {
                    unresolvedCount++;
                    if (unresolvedSample.Length == 0) unresolvedSample = show.RootFolder;
                    logFn?.Invoke($"Skipping {show.Title} — unresolved path: {show.RootFolder}  (add a Path Mapping)");
                    continue;
                }
                result.Add(new ShowRecord(folder, "plex", showId, show.Title, show.Year, show.RootFolder, show.HasTheme));
            }
        }
    }

    db.SetSetting("last_show_sync_unresolved_count", unresolvedCount.ToString());
    db.SetSetting("last_show_sync_unresolved_sample", unresolvedSample);
    return result;
}

private async Task<List<PlexShow>> FetchShowsForSectionAsync(
    List<string> serverUrls, string sectionKey, string serverToken, string clientId)
{
    var shows = new List<PlexShow>();
    var pageSize = 200; var start = 0; var activeUrl = serverUrls[0];
    while (true)
    {
        var url = $"{activeUrl.TrimEnd('/')}/library/sections/{sectionKey}/all?" +
            BuildQuery(ClientParams(clientId),
                ("type", "2"), ("X-Plex-Token", serverToken),
                ("X-Plex-Container-Start", start.ToString()), ("X-Plex-Container-Size", pageSize.ToString()));
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in ClientHeaders(clientId, serverToken)) req.Headers.TryAddWithoutValidation(k, v);
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        shows.AddRange(PlexShowThemes.Parse(body));
        var root = XDocument.Parse(body).Root!;
        var size = int.Parse(root.Attribute("size")?.Value ?? "0");
        var totalSize = int.Parse(root.Attribute("totalSize")?.Value ?? size.ToString());
        if (size <= 0 || start + size >= totalSize) break;
        start += size;
    }
    return shows;
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/PlexService.cs tests/Themearr.API.Tests/PlexFetchShowsTests.cs
git commit -m "feat: PlexService.FetchShowsAsync (selected libraries, resolved show root)"
```

---

### Task 6: `ShowSyncService`

The show sync engine: fetch Plex shows and upsert/prune the `shows` table, mirroring `SyncService`'s fetch → upsert → prune-except shape and prune-safety. Opt-in: does nothing when no show libraries are selected. The background scheduler + "Run now" trigger + `syncShows` task registration are deferred to plan 1b (where they land alongside the trigger endpoint), so this plan delivers the fully-testable `RunOnceAsync` engine.

**Files:**
- Create: `src/Themearr.API/Services/ShowSyncService.cs`
- Modify: `src/Themearr.API/Program.cs` (register `ShowSyncService` in DI)
- Test: `tests/Themearr.API.Tests/ShowSyncServiceTests.cs` (create)

**Interfaces:**
- Consumes: `PlexService.FetchShowsAsync` (Task 5), `Database.UpsertShows`/`PruneShowsExcept`/`GetSelectedShowLibraries` (Tasks 3–4).
- Produces: `ShowSyncService.RunOnceAsync(CancellationToken) -> Task<int>` (returns shows synced).

- [ ] **Step 1: Write the failing test** (`ShowSyncServiceTests.cs`)

```csharp
using System.Net;
using System.Text;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowSyncServiceTests
{
    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }
    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    [Fact]
    public async Task RunOnce_upserts_selected_plex_shows_into_the_shows_table()
    {
        using var dir = new TempDir();
        var showRoot = Path.Combine(dir.Path, "The Wire"); Directory.CreateDirectory(showRoot);
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        db.SetSetting("plex_access_token", "tok"); db.SetSetting("plex_client_identifier", "c1");
        db.SetPlexServers([new Dictionary<string, object?> {
            ["id"] = "srv1", ["name"] = "Tower", ["url"] = "http://plex.local:32400",
            ["urls"] = new List<string> { "http://plex.local:32400" }, ["token"] = "tok" }]);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/library/sections" => Xml("""<MediaContainer size="1"><Directory key="3" type="show" title="TV"/></MediaContainer>"""),
            "/library/sections/3/all" => Xml($"""
                <MediaContainer size="1" totalSize="1">
                  <Directory ratingKey="46" type="show" title="The Wire" year="2002">
                    <Location id="1" path="{showRoot}"/>
                  </Directory>
                </MediaContainer>"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var sut = new ShowSyncService(db, plex, NullLogger<ShowSyncService>.Instance);

        var synced = await sut.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, synced);
        Assert.Contains(db.GetAllShows(), s => (string)s["title"]! == "The Wire");
    }

    [Fact]
    public async Task RunOnce_does_nothing_when_no_show_libraries_are_selected()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        db.SetSetting("plex_access_token", "tok"); db.SetSetting("plex_client_identifier", "c1");
        // no SetSelectedShowLibraries → opt-out
        var handler = new RoutingHandler(_ => throw new InvalidOperationException("should not call Plex"));
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var sut = new ShowSyncService(db, plex, NullLogger<ShowSyncService>.Instance);

        var synced = await sut.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, synced);
        Assert.Empty(db.GetAllShows());
    }
}
```

(Add `using Microsoft.Extensions.Logging.Abstractions;` for `NullLogger`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowSyncServiceTests"`
Expected: FAIL to compile — `ShowSyncService` doesn't exist.

- [ ] **Step 3: Implement `ShowSyncService`** (`ShowSyncService.cs`)

```csharp
using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Syncs TV shows from the operator's selected Plex show libraries into the `shows`
/// table. Opt-in: when no show libraries are selected it fetches nothing and prunes
/// nothing. Mirrors <see cref="SyncService"/>'s fetch → upsert → prune-except shape,
/// with the same "only prune after a non-empty, fully-resolved sync" safety.
/// </summary>
public class ShowSyncService(Database db, PlexService plex, ILogger<ShowSyncService> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (db.GetSelectedShowLibraries().Values.Sum(v => v.Count) == 0)
            return 0;   // opt-in: nothing selected

        var shows = await plex.FetchShowsAsync(msg => log.LogInformation("{Msg}", msg));
        db.UpsertShows(shows);

        var unresolved = int.TryParse(db.GetSetting("last_show_sync_unresolved_count", "0"), out var n) ? n : 0;
        if (shows.Count > 0 && unresolved == 0)
        {
            var removed = db.PruneShowsExcept(shows.Select(s => s.Folder));
            if (removed > 0) log.LogInformation("Removed {N} shows no longer in the library", removed);
        }
        return shows.Count;
    }
}
```

Register in `Program.cs` (near the existing `SyncService` registration):

```csharp
builder.Services.AddScoped<ShowSyncService>();
```

- [ ] **Step 4: Run tests to verify pass (+ full suite)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/ShowSyncService.cs src/Themearr.API/Program.cs tests/Themearr.API.Tests/ShowSyncServiceTests.cs
git commit -m "feat: ShowSyncService engine (opt-in Plex show sync)"
```

---

## Final verification

- [ ] `dotnet test tests/Themearr.API.Tests` — whole suite green, no new warnings.
- [ ] Manual (maintainer's box, live Plex, once a UI/endpoint exists in 1b/1c): select a show library, run the show sync, confirm shows appear with correct pending/downloaded/plex-provided status.

## Self-review notes

- **Spec coverage (discovery slice):** `shows` table + identity (Tasks 1–2), disk-derived status + candidate rule via `plex_has_theme`/`GetPendingShows` (Tasks 2–3), Plex show sourcing with `<Location>` root + `theme` detection + path-mapping resolution (Tasks 4–5), opt-in selected libraries (Tasks 4–6), parallel sync with prune safety (Task 6). Downloads, the ShowsController API, and the Shows UI are explicitly deferred to plans 1b/1c.
- **Type consistency:** `ShowRecord(Folder, Source, SourceRef, Title, Year, SourcePath, HasPlexTheme)`, `PlexShow(RatingKey, Title, Year, HasTheme, RootFolder)`, `MediaFolderId.For`, and the `Get*Shows`/`Set*Show*` method names are used identically across tasks.
- **Not in this plan:** the show-sync background scheduler + "Run now" trigger + `syncShows` task registration, `theme_history` `media_type`, `YoutubeService` param rename, download routing, `ShowsController`, all frontend — those belong to 1b (downloads + API + trigger) and 1c (UI).
