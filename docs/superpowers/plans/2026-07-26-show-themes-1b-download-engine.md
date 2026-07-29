# Show Themes — Phase 1b (Download Engine) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make discovered TV shows (from Phase 1a) actually get `theme.mp3` downloaded — auto-download for themeless shows — and synced on a schedule / on demand.

**Architecture:** Reuse the shared download machinery by giving `DownloadService` media-type-aware routing (the security-critical SSRF/quota/atomic-write logic is NOT duplicated); add a parallel show auto-download worker and a show sync scheduler (both mirror their movie counterparts, per the parallel-stack decision). Movie behavior is preserved via default `mediaType = "movie"` parameters.

**Tech Stack:** .NET 10 Web API, `Microsoft.Data.Sqlite`, xUnit, hosted `BackgroundService`s.

## Global Constraints

- **.NET 10**; xUnit tests in `tests/Themearr.API.Tests/`. Follow existing style.
- **Movie behavior must not change:** every new `mediaType` parameter defaults to `"movie"`, and existing movie callers stay untouched. Run the FULL suite after each task.
- **Parallel workers, shared download core:** `DownloadService` is generalized by media type (spec-endorsed — it's an id-keyed job runner, and its SSRF/quota/atomic-write code must not be copied). The auto-download worker and sync scheduler are DUPLICATED per media type (the show search query differs — drop the year, use "theme song").
- **Show theme search query** is `"{title} theme song"` (NO year) — a show isn't per-year.
- **Show download** writes `theme.mp3` into the show ROOT folder, sets `shows.status='downloaded'`, and records history with `media_type='show'`.
- **`ShowSyncService` is `AddScoped`** — the scheduler must resolve it via `CreateScope()` (never capture it in the singleton `BackgroundService`), and must wrap `RunOnceAsync` in try/catch (it has none).

---

### Task 1: Generalize `YoutubeService.SearchAsync` params

Rename the movie-specific parameter names so shows can reuse the search + scoring unchanged.

**Files:**
- Modify: `src/Themearr.API/Services/YoutubeService.cs` (params `movieTitle`/`movieYear` → `title`/`year`)
- Modify: `src/Themearr.API/Controllers/MoviesController.cs` (2 call sites), `src/Themearr.API/Services/AutoDownloadService.cs` (1 call site)

**Interfaces:**
- Produces: `Task<List<Dictionary<string,object?>>> SearchAsync(string query, int maxResults = 8, string? title = null, int? year = null)`

- [ ] **Step 1: Rename the parameters**

In `YoutubeService.SearchAsync`, rename `movieTitle` → `title` and `movieYear` → `year` (signature + the `Score(...)` call inside). The `Score` method's own parameter names can stay or be renamed to `title`/`year` — either way keep the body identical.

- [ ] **Step 2: Update the three call sites**

`grep -rn "movieTitle:\|movieYear:" src` finds them: `MoviesController.SearchYoutube`, `MoviesController.AutoDownload`, `AutoDownloadService.TryAutoDownloadOne`. Change `movieTitle:`/`movieYear:` named args to `title:`/`year:`.

- [ ] **Step 3: Build + full suite (rename is compiler-checked, behavior-neutral)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS, same count, no warnings.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor: generalize YoutubeService.SearchAsync params (title/year)"
```

---

### Task 2: `theme_history.media_type` column

Let history carry show entries and label them.

**Files:**
- Modify: `src/Themearr.API/Data/Database.cs` (migration in `Init`; `AddThemeHistory`/`GetThemeHistory`)
- Test: `tests/Themearr.API.Tests/ThemeHistoryMediaTypeTests.cs` (create)

**Interfaces:**
- Produces: `void AddThemeHistory(string mediaId, string title, int? year, string? themeTitle, string? sourceUrl, string mediaType = "movie")`
- Produces: `GetThemeHistory` rows include `["mediaType"]`.

- [ ] **Step 1: Write the failing test** (`ThemeHistoryMediaTypeTests.cs`)

```csharp
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

        db.AddThemeHistory("m1", "A Movie", 2001, "Theme", "http://x", /* default movie */ null!);
        db.AddThemeHistory("s1", "A Show", 2010, "Intro", "http://y", "show");

        var rows = db.GetThemeHistory();
        Assert.Equal("show",  rows.Single(r => (string)r["mediaId"]! == "s1")["mediaType"]);
        Assert.Equal("movie", rows.Single(r => (string)r["mediaId"]! == "m1")["mediaType"]);
    }
}
```

(If the compiler rejects `null!` for a defaulted string param, call the 5-arg overload for the movie row instead: `db.AddThemeHistory("m1", "A Movie", 2001, "Theme", "http://x");`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ThemeHistoryMediaTypeTests"`
Expected: FAIL — `mediaType` key missing / `media_type` column absent.

- [ ] **Step 3: Add the migration + generalize the methods**

In `Database.Init()`, add a migration call alongside `MigrateHistoryTable(conn);`:

```csharp
MigrateHistoryTableV2(conn);
```

and add the method (mirroring `MigrateHistoryTable`):

```csharp
private static void MigrateHistoryTableV2(SqliteConnection conn)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA table_info(theme_history)";
    var columns = new HashSet<string>();
    using (var r = cmd.ExecuteReader())
        while (r.Read()) columns.Add(r.GetString(1));
    if (!columns.Contains("media_type"))
        conn.Execute("ALTER TABLE theme_history ADD COLUMN media_type TEXT NOT NULL DEFAULT 'movie'");
}
```

Change `AddThemeHistory` to accept and store `mediaType`:

```csharp
public void AddThemeHistory(string movieId, string movieTitle, int? movieYear,
    string? themeTitle, string? sourceUrl, string mediaType = "movie")
{
    using var conn = Open();
    conn.Execute(
        "INSERT INTO theme_history (movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at, media_type) VALUES (@mid, @t, @y, @tt, @url, @dt, @mt)",
        ("@mid", movieId), ("@t", movieTitle),
        ("@y",   (object?)movieYear  ?? DBNull.Value),
        ("@tt",  (object?)themeTitle ?? DBNull.Value),
        ("@url", (object?)sourceUrl  ?? DBNull.Value),
        ("@dt",  DateTime.UtcNow.ToString("o")),
        ("@mt",  mediaType));
}
```

Change `GetThemeHistory`'s SELECT to include `media_type` and add `["mediaType"] = r.GetString(7)` to the row dict (append `media_type` last in the SELECT so existing indices are unchanged):

```csharp
"SELECT id, movie_id, movie_title, movie_year, theme_title, source_url, downloaded_at, media_type FROM theme_history ORDER BY id DESC LIMIT @lim"
```
```csharp
["mediaType"] = r.GetString(7),
```

- [ ] **Step 4: Run tests to verify pass (+ full suite — history reads changed)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Data/Database.cs tests/Themearr.API.Tests/ThemeHistoryMediaTypeTests.cs
git commit -m "feat: theme_history media_type column (movie default, show-aware)"
```

---

### Task 3: `DownloadService` media-type routing

Route the four movie-coupled points by media type so a show download writes to the show folder and updates the `shows` table. Job state is namespaced by `{mediaType}:{id}` so movie and show ids can never collide (the shared `MediaFolderId` hash space caution from 1a's final review).

**Files:**
- Modify: `src/Themearr.API/Services/DownloadService.cs`
- Test: `tests/Themearr.API.Tests/DownloadServiceShowTests.cs` (create)

**Interfaces:**
- Consumes: `Database.GetShow`/`SetShowStatus` (1a), `AddThemeHistory(..., mediaType)` (Task 2).
- Produces: `bool Start(string id, string youtubeUrl, string mediaType = "movie")`, `object GetStatus(string id, string mediaType = "movie")`.

- [ ] **Step 1: Write the failing test** (`DownloadServiceShowTests.cs`)

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class DownloadServiceShowTests
{
    private const string YtUrl = "https://www.youtube.com/watch?v=abc12345678";

    // Writes a valid theme file and reports a title, like the movie tests' RecordingProvider.
    private sealed class RecordingProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            File.WriteAllBytes(outputPath, new byte[] { 0x49, 0x44, 0x33, 9, 9, 9 });
            return Task.FromResult<string?>("Show Theme");
        }
    }
    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static object? Prop(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o);

    [Fact]
    public async Task Show_download_writes_theme_sets_show_status_and_records_show_history()
    {
        using var showDir = new TempDir();
        var dbDir = Path.Combine(Path.GetTempPath(), "themearr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        var db = new Database(Path.Combine(dbDir, "themearr.db")); db.Init();
        var showId = MediaFolderId.For(showDir.Path);
        db.UpsertShows([new ShowRecord(showDir.Path, "plex", "srv1:45", "Test Show", 2010, "/plex/Test Show", false)]);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Themearr:DownloadTimeoutSeconds"] = "900" }).Build();
        var svc = new DownloadService(new RecordingProvider(), db, new StubHttpClientFactory(), config, NullLogger<DownloadService>.Instance);

        Assert.True(svc.Start(showId, YtUrl, "show"));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        object status = svc.GetStatus(showId, "show");
        while (!(bool)Prop(status, "finished")! && DateTime.UtcNow < deadline)
        { await Task.Delay(100); status = svc.GetStatus(showId, "show"); }

        Assert.True((bool)Prop(status, "finished")!);
        Assert.Null((string?)Prop(status, "error"));
        Assert.True(File.Exists(Path.Combine(showDir.Path, "theme.mp3")));
        Assert.Equal("downloaded", db.GetShow(showId)!["status"]);
        Assert.Contains(db.GetThemeHistory(), h => (string)h["mediaId"]! == showId && (string)h["mediaType"]! == "show");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~DownloadServiceShowTests"`
Expected: FAIL to compile — `Start`/`GetStatus` have no `mediaType` param.

- [ ] **Step 3: Add media-type routing to `DownloadService`**

Add a job-key helper and thread `mediaType` through `Start` → `RunAsync`, and `GetStatus`:

```csharp
private static string JobKey(string mediaType, string id) => $"{mediaType}:{id}";

public bool Start(string id, string youtubeUrl, string mediaType = "movie")
{
    var key = JobKey(mediaType, id);
    if (_jobs.TryGetValue(key, out var existing) && existing.InProgress)
        return false;

    var url  = NormaliseYoutubeUrl(youtubeUrl.Trim());
    var logs = _jobLogs.GetOrAdd(key, _ => new ConcurrentQueue<string>());
    while (logs.TryDequeue(out _)) { }

    _jobs[key] = new JobState(true, false, null, DateTime.UtcNow);
    _ = Task.Run(() => RunAsync(id, url, mediaType));
    return true;
}

public object GetStatus(string id, string mediaType = "movie")
{
    var key = JobKey(mediaType, id);
    if (!_jobs.TryGetValue(key, out var state))
        return new { inProgress = false, finished = false, error = (string?)null, logs = Array.Empty<string>() };
    _jobLogs.TryGetValue(key, out var logQueue);
    var lines = logQueue?.ToArray() ?? [];
    if (lines.Length > 50) lines = lines[^50..];
    return new { inProgress = state.InProgress, finished = state.Finished, error = state.Error, logs = lines };
}
```

Update `AddLog` to take the job key (its callers already pass the id — change them to pass the key). Then in `RunAsync(string id, string url, string mediaType)`:
- compute `var key = JobKey(mediaType, id);` and use `key` for every `_jobs[...]`/`AddLog(...)` (replacing the old `movieId` keying);
- replace the item load: `var item = (mediaType == "show" ? db.GetShow(id) : db.GetMovie(id)) ?? throw new KeyNotFoundException($"{mediaType} not found: {id}");` (use `item["folderName"]`/`["title"]`/`["year"]` exactly as before);
- on success: `if (mediaType == "show") db.SetShowStatus(id, "downloaded"); else db.SetMovieStatus(id, "downloaded");` and `db.AddThemeHistory(id, title, year, themeTitle, url, mediaType);`.

Keep everything else (timeout, SSRF redirect, quota cooldown, atomic write, watchdog, `IsAnyInProgress`) unchanged — `IsAnyInProgress` stays global so movie and show downloads serialize together. Update the signature of `RunAsync` to `(string id, string url, string mediaType)` and its `catch` blocks to write `_jobs[key] = ...`.

- [ ] **Step 4: Run tests to verify pass (+ full suite — movie downloads use the defaults)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS — new show test green AND all movie `DownloadServiceTests` still green (they call `Start(movieId, url)`/`GetStatus(movieId)` with the `"movie"` default → key `movie:{id}`).

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/DownloadService.cs tests/Themearr.API.Tests/DownloadServiceShowTests.cs
git commit -m "feat: DownloadService media-type routing (shows write to the shows table)"
```

---

### Task 4: `ShowAutoDownloadService` (background worker)

A parallel of `AutoDownloadService` that fills themeless shows. Differs only in: pending source (`GetPendingShows`), search query (`"{title} theme song"`, no year), status setter (`SetShowStatus`), and `download.Start(id, url, "show")`.

**Files:**
- Create: `src/Themearr.API/Services/ShowAutoDownloadService.cs`
- Modify: `src/Themearr.API/Program.cs` (register as a hosted service)
- Test: `tests/Themearr.API.Tests/ShowAutoDownloadServiceTests.cs` (create)

**Interfaces:**
- Consumes: `Database.GetPendingShows`/`SetShowStatus` (1a), `DownloadService.Start(id, url, "show")` (Task 3), `YoutubeService.SearchAsync(query, 8, title, year)` (Task 1).
- Produces: `ShowAutoDownloadService.TryDownloadOnceAsync(CancellationToken) -> Task<string>` (returns the tick result string — testable without the timer).

- [ ] **Step 1: Write the failing test** (`ShowAutoDownloadServiceTests.cs`)

```csharp
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowAutoDownloadServiceTests
{
    [Fact]
    public async Task TryDownloadOnce_skips_when_auto_download_off()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        // auto_download defaults to false; a show is pending but must not be touched.
        var showDir = Path.Combine(dir.Path, "Show"); Directory.CreateDirectory(showDir);
        db.UpsertShows([new ShowRecord(showDir, "plex", "srv1:1", "Show", 2010, showDir, false)]);

        var sut = ShowAutoDownloadTestHarness.Build(db);   // see Step 3 for the harness note
        var result = await sut.TryDownloadOnceAsync(CancellationToken.None);

        Assert.Contains("auto_download is off", result);
    }

    [Fact]
    public async Task TryDownloadOnce_uses_a_year_free_show_query()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        db.SetSetting("auto_download", "true");
        db.MarkSetupComplete();
        var showDir = Path.Combine(dir.Path, "The Wire"); Directory.CreateDirectory(showDir);
        db.UpsertShows([new ShowRecord(showDir, "plex", "srv1:2", "The Wire", 2002, showDir, false)]);

        var (sut, capturedQuery) = ShowAutoDownloadTestHarness.BuildCapturingQuery(db);
        await sut.TryDownloadOnceAsync(CancellationToken.None);

        Assert.Equal("The Wire theme song", capturedQuery.Value);   // no year
    }
}
```

Note for the implementer: `AutoDownloadService` resolves its per-tick deps (`Database`, `YoutubeService`) from an `IServiceProvider` scope. Structure `ShowAutoDownloadService` the same way, and make `TryDownloadOnceAsync` the testable seam (the `ExecuteAsync` timer loop just calls it). Build the test harness with a real `Database`, a fake `YoutubeService` that captures the query and returns a `bestMatch:true` result (or one that returns no results so no real download starts), and a real `DownloadService` with a `CheckConfiguration()==null` fake provider. If a fake `YoutubeService` is impractical (it has no interface), assert on the tick-result string and the `download.IsAnyInProgress()`/cooldown behavior instead of the query — the query assertion may be dropped to a comment if `YoutubeService` cannot be substituted; keep the "auto_download off" and "no pending shows" tests which don't need YouTube.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowAutoDownloadServiceTests"`
Expected: FAIL to compile — `ShowAutoDownloadService` doesn't exist.

- [ ] **Step 3: Implement `ShowAutoDownloadService`**

Copy `AutoDownloadService.cs` to `ShowAutoDownloadService.cs` and adapt (this duplication is the spec's parallel-stack decision):
- class `ShowAutoDownloadService`; log messages say "ShowAutoDownload".
- Extract the per-tick body into `public async Task<string> TryDownloadOnceAsync(CancellationToken ct)` that returns the tick-result string; `ExecuteAsync` calls it and stores the result in `Tick`.
- `db.GetPendingShows()` instead of `GetPendingMovies()`; `db.SetShowStatus(...)` instead of `SetMovieStatus`; `db.GetShow(id)` for the last-started outcome check.
- query: `var query = $"{title} theme song";` (NO year).
- `download.Start(id, url, "show")`.
- Diagnostics `pendingCount` from `db.GetAllShows()`.

Register in `Program.cs` (near `AutoDownloadService`):

```csharp
builder.Services.AddHostedService<ShowAutoDownloadService>();
```

Keep `DownloadService`/`IThemeAudioProvider` singletons shared — both workers gate on the same `download.IsAnyInProgress()`, so movie and show auto-downloads naturally serialize.

- [ ] **Step 4: Run tests to verify pass (+ full suite)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/ShowAutoDownloadService.cs src/Themearr.API/Program.cs tests/Themearr.API.Tests/ShowAutoDownloadServiceTests.cs
git commit -m "feat: ShowAutoDownloadService (auto-fill themeless shows, year-free query)"
```

---

### Task 5: `ShowAutoSyncService` (schedule + Run-now trigger)

Drives `ShowSyncService.RunOnceAsync` on the Plex interval and registers a `syncShows` task so System → Tasks shows it and "Run now" triggers it — mirroring `AutoSyncService`. Scope-resolves the scoped `ShowSyncService` and wraps it in try/catch (1a final-review notes).

**Files:**
- Create: `src/Themearr.API/Services/ShowAutoSyncService.cs`
- Modify: `src/Themearr.API/Program.cs` (register as a hosted service)
- Test: `tests/Themearr.API.Tests/ShowAutoSyncServiceTests.cs` (create)

**Interfaces:**
- Consumes: `ShowSyncService.RunOnceAsync` (1a), `TaskRegistry`, `LibrarySourceResolver` (for `SyncInterval`).
- Produces: a hosted `ShowAutoSyncService` registering `TaskRegistry` id `"syncShows"`.

- [ ] **Step 1: Write the failing test** (`ShowAutoSyncServiceTests.cs`)

Because `ShowAutoSyncService` is a timer `BackgroundService`, test the one piece of logic worth pinning without the timer: extract the "should a sync run now?" + "run it in a scope" into a `public async Task RunScheduledAsync(bool forced, CancellationToken ct)` seam (mirroring `AutoSyncService.TryAutoSync`) and test that it (a) no-ops when `auto_sync` is off and not forced, and (b) runs `ShowSyncService` when forced.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;
// plus the services this needs registered (Database, PlexService, ShowSyncService, TaskRegistry, LibrarySourceResolver)

public class ShowAutoSyncServiceTests
{
    [Fact]
    public async Task Forced_run_with_no_show_libraries_selected_is_a_no_op_and_records_nothing_harmful()
    {
        // Build a ServiceProvider with a real Database (no show libraries selected),
        // a PlexService whose HttpClient throws if called, ShowSyncService, TaskRegistry.
        // Assert RunScheduledAsync(forced:true, ct) completes without throwing and the
        // shows table stays empty (ShowSyncService.RunOnceAsync's opt-in guard returns 0).
    }
}
```

The implementer writes the concrete provider wiring following the existing `AutoSyncService`/DI test patterns (`services.CreateScope()` usage). Keep the test focused on: forced run is safe + opt-in no-op holds; do NOT try to test the 30-minute timer loop.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themearr.API.Tests --filter "FullyQualifiedName~ShowAutoSyncServiceTests"`
Expected: FAIL to compile — `ShowAutoSyncService` doesn't exist.

- [ ] **Step 3: Implement `ShowAutoSyncService`**

Copy `AutoSyncService.cs` to `ShowAutoSyncService.cs` and adapt:
- `public const string SyncTaskId = "syncShows";` label `"Sync Shows"`.
- Extract the run body into `public async Task RunScheduledAsync(bool forced, CancellationToken ct)` (the testable seam) and have the loop call it.
- Inside, `using var scope = services.CreateScope();` then `var showSync = scope.ServiceProvider.GetRequiredService<ShowSyncService>();` and:
  ```csharp
  try { var count = await showSync.RunOnceAsync(ct); registry.RecordRun(SyncTaskId, startedAt, sw.Elapsed, $"synced {count} shows"); }
  catch (Exception ex) { registry.RecordFailure(SyncTaskId, "failed — see the application log"); log.LogWarning(ex, "Show sync failed"); }
  ```
- Gate on `auto_sync` + `IsSetupComplete()` + the interval-elapsed check exactly like `AutoSyncService`, using a `last_show_auto_sync_at` setting (its own timestamp key, so it doesn't clash with the movie sync's `last_auto_sync_at`).
- `SyncInterval => sources.Active.SyncInterval` (Plex 24h), same clamp/jitter loop.

Register in `Program.cs`:

```csharp
builder.Services.AddHostedService<ShowAutoSyncService>();
```

- [ ] **Step 4: Run tests to verify pass (+ full suite)**

Run: `dotnet test tests/Themearr.API.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/ShowAutoSyncService.cs src/Themearr.API/Program.cs tests/Themearr.API.Tests/ShowAutoSyncServiceTests.cs
git commit -m "feat: ShowAutoSyncService (scheduled show sync + Run-now via TaskRegistry)"
```

---

## Final verification

- [ ] `dotnet test tests/Themearr.API.Tests` — whole suite green, no warnings.
- [ ] Confirm both new hosted services are registered in `Program.cs` (`ShowAutoDownloadService`, `ShowAutoSyncService`) and `ShowSyncService` is `AddScoped`.
- [ ] Manual (maintainer's box, live Plex, once 1c/1d expose it): select a show library, trigger "Sync Shows", enable auto-download, confirm a themeless show gets `theme.mp3` and `shows.status` flips to downloaded; a Plex-Pass-themed show is left alone.

## Self-review notes

- **Spec coverage (download slice):** YoutubeService reuse (Task 1); history labels shows (Task 2); shows download to the show folder + shows table + show history (Task 3); auto-fill themeless shows with a year-free query (Task 4); scheduled + Run-now show sync (Task 5). The `ShowsController` manual API + posters and the frontend are the next slices (1c API, 1d UI).
- **Type consistency:** `Start(id, url, mediaType="movie")` / `GetStatus(id, mediaType="movie")`, `AddThemeHistory(..., mediaType="movie")`, `TryDownloadOnceAsync`, `RunScheduledAsync`, and the `"show"` discriminator are used identically across tasks.
- **Movie behavior preserved:** every new param defaults to `"movie"`; existing movie call sites are untouched; the full suite gates each task.
- **Not in this plan:** `ShowsController` (list/search/download/ignore/status/theme-audio endpoints) + show posters → plan 1c; the Shows page/queue/settings/nav → plan 1d.
- **Known test-concreteness caveat (Tasks 4–5):** `YoutubeService` has no interface, so the auto-download worker's *happy path* (searched → best match → started) can't be cleanly faked; and both new services are timer `BackgroundService`s. So Tasks 4–5 pin the **guard conditions** (auto-download off, setup incomplete, no pending shows, opt-in no-op) via the extracted `TryDownloadOnceAsync`/`RunScheduledAsync` seams — NOT the full download happy path. That mirrors how the existing movie `AutoDownloadService`/`AutoSyncService` are tested (guard/seam level, not end-to-end), and the real happy path is verified by the manual live-Plex check. If deeper coverage is wanted, add an `IYoutubeSearch` interface first (a separate refactor, out of scope here). Implementers should write the guard-condition tests concretely and drop the query-capture assertion if `YoutubeService` can't be substituted.
