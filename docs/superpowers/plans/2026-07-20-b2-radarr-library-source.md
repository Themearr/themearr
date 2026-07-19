# B2: Radarr as a Selectable Library Source — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Themearr read its movie library from Radarr instead of Plex, so it works for people who run Jellyfin, Emby or Kodi — none of whom can complete setup today.

**Architecture:** B1 (shipped as v1.41.0) already identifies movies by their local folder behind an `ILibrarySource` seam with Plex as the only implementation. B2 adds the second implementation and everything needed to *choose* it: settings, a setup-wizard branch, source-aware posters and health. The interface grows two members (`FetchPosterAsync`, `CheckAsync`) that B1 deliberately omitted because nothing called them yet.

**Tech Stack:** .NET 10 (ASP.NET Core), `Microsoft.Data.Sqlite`, xUnit, React 19 + Vite, React Router.

**Spec:** `docs/superpowers/specs/2026-07-19-radarr-library-source-design.md` (stage B2)

## Global Constraints

- **The Radarr API key must never reach the browser.** Follow the existing `rapidapi` precedent in `SettingsController`: the GET returns only `{ configured: bool }` and never the secret itself. A blank key on save means "keep what you had", never "erase it".
- **No health message, task result, or log line may contain the API key or a raw exception message.** Hand-written messages only; `LogSanitizer` for logs. This rule already governs every existing check.
- **Existing Plex installs must be unaffected.** `library_source` defaults to `plex`, `setup_complete` stays `1`, and nobody is re-prompted or re-migrated.
- Radarr movies with `hasFile: false` are **skipped** — monitored but not downloaded means there is no film for a theme to accompany.
- Radarr's list endpoint is `GET {url}/api/v3/movie` authenticated with an **`X-Api-Key`** header. Relevant fields: `path` (the movie's folder), `title`, `year`, `hasFile`, `id`. Health uses `GET {url}/api/v3/system/status`; posters use `GET {url}/api/v3/mediacover/{id}/poster.jpg`.
- `source_ref` is **opaque to everything except the source that issued it**. Plex stores `"{serverId}:{ratingKey}"`; Radarr stores its numeric movie id. No component outside a source may parse it.
- Target framework `net10.0`, nullable reference types enabled, primary constructors, style matching `src/Themearr.API/Services/`.
- Backend tests: `dotnet test` from the repository root. The suite currently has **179** tests, all passing.
- Frontend checks from `src/Themearr.Web`: `npx tsc --noEmit`, `npm run lint` (expect 0 errors and 3 pre-existing warnings — 1 in `src/app/login/page.tsx`, 2 in `src/lib/auth.tsx`), `npm run build`.

---

### Task 1: `TaskRegistry.UpdateInterval`

B1 deferred this deliberately: with one source the interval never changed, so the method would have been dead code. B2 makes it live — and a review already flagged that `Register` snapshots the interval at startup, so the Tasks page would otherwise show a stale cadence after switching source.

**Files:**
- Modify: `src/Themearr.API/Services/TaskRegistry.cs`
- Test: `tests/Themearr.API.Tests/TaskRegistryTests.cs`

**Interfaces:**
- Produces: `TaskRegistry.UpdateInterval(string id, TimeSpan interval)` — updates the displayed interval without disturbing last-run state

- [ ] **Step 1: Write the failing tests**

Append to `tests/Themearr.API.Tests/TaskRegistryTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void UpdateInterval_changes_the_reported_interval()
    {
        var r = WithSync();

        r.UpdateInterval("syncLibrary", TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(r.Snapshot()).Interval);
    }

    [Fact]
    public void UpdateInterval_preserves_last_run_state()
    {
        var r = WithSync();
        var started = new DateTime(2026, 7, 20, 2, 0, 0, DateTimeKind.Utc);
        r.RecordRun("syncLibrary", started, TimeSpan.FromMilliseconds(1200), "42 movies synced");

        r.UpdateInterval("syncLibrary", TimeSpan.FromMinutes(15));

        var t = Assert.Single(r.Snapshot());
        Assert.Equal(started, t.LastRunUtc);
        Assert.Equal("42 movies synced", t.LastResult);
        // nextRunUtc is derived, so it must follow the NEW interval
        Assert.Equal(started.AddMinutes(15), t.NextRunUtc);
    }

    [Fact]
    public void UpdateInterval_on_an_unknown_id_does_nothing()
    {
        var r = WithSync();

        r.UpdateInterval("nope", TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromHours(24), Assert.Single(r.Snapshot()).Interval);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~TaskRegistryTests`
Expected: FAIL — `'TaskRegistry' does not contain a definition for 'UpdateInterval'`.

- [ ] **Step 3: Implement**

In `src/Themearr.API/Services/TaskRegistry.cs`, the nested `Entry` class declares `public required TimeSpan Interval { get; init; }`. Change `init` to `set` so the value can be updated after construction:

```csharp
        public required TimeSpan   Interval   { get; set; }
```

Then add this method next to `Register`:

```csharp
    /// <summary>
    /// Changes a task's displayed cadence without touching its run history.
    /// Re-registering would replace the entry and wipe last-run state, so this exists
    /// for the case where the interval is a property of something configurable — the
    /// active library source — rather than a constant.
    /// </summary>
    public void UpdateInterval(string id, TimeSpan interval)
    {
        if (_tasks.TryGetValue(id, out var e)) e.Interval = interval;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~TaskRegistryTests`
Expected: PASS — all `TaskRegistryTests` pass, including the three new ones.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/TaskRegistry.cs tests/Themearr.API.Tests/TaskRegistryTests.cs
git commit -m "feat(system): allow a task's interval to change without losing run history"
```

---

### Task 2: `ILibrarySource.CheckAsync` and source-aware health

Replaces `PlexReachableCheck` with one check that asks whichever source is active. Two checks where only one can ever apply would mislead.

**Files:**
- Modify: `src/Themearr.API/Services/Sources/ILibrarySource.cs`
- Modify: `src/Themearr.API/Services/Sources/PlexLibrarySource.cs`
- Create: `src/Themearr.API/Services/Health/LibrarySourceCheck.cs`
- Delete: `src/Themearr.API/Services/Health/PlexReachableCheck.cs`
- Modify: `src/Themearr.API/Program.cs`
- Modify: `tests/Themearr.API.Tests/PlexReachableCheckTests.cs` → rename to `LibrarySourceCheckTests.cs`
- Modify: `tests/Themearr.API.Tests/LibrarySourceResolverTests.cs` (its `FakeSource` must implement the new member)

**Interfaces:**
- Produces:
  - `ILibrarySource.CheckAsync(CancellationToken ct) -> Task<string?>` — `null` when healthy, otherwise a user-facing reason
  - `LibrarySourceCheck : IHealthCheck`, registered as `"librarySource"`
  - `PlexLibrarySource.ClientName` (const `"plex-health"`) — the named `HttpClient` carrying the short timeout

- [ ] **Step 1: Write the failing tests**

Rename `tests/Themearr.API.Tests/PlexReachableCheckTests.cs` to `tests/Themearr.API.Tests/LibrarySourceCheckTests.cs` and replace its contents:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Health;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class LibrarySourceCheckTests
{
    private sealed class FakeSource(string name, string? reason) : ILibrarySource
    {
        public string   Name         => name;
        public TimeSpan SyncInterval => TimeSpan.FromHours(24);

        public Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MovieRecord>>([]);

        public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct) =>
            Task.FromResult<Stream?>(null);

        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult(reason);
    }

    private static Task<HealthCheckResult> Run(TempDir dir, string? reason, bool setupComplete = true)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        if (setupComplete) db.MarkSetupComplete();
        var resolver = new LibrarySourceResolver(db, [new FakeSource("plex", reason)]);
        return new LibrarySourceCheck(db, resolver)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
    }

    [Fact]
    public async Task A_healthy_source_reports_healthy()
    {
        using var dir = new TempDir();
        Assert.Equal(HealthStatus.Healthy, (await Run(dir, reason: null)).Status);
    }

    [Fact]
    public async Task An_unhealthy_source_surfaces_its_reason()
    {
        using var dir = new TempDir();

        var result = await Run(dir, reason: "Radarr rejected the API key (401).");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("rejected the API key", result.Description);
    }

    [Fact]
    public async Task Before_setup_completes_it_is_healthy_even_if_the_source_is_broken()
    {
        using var dir = new TempDir();

        // A fresh install has nothing configured yet and is not broken.
        Assert.Equal(HealthStatus.Healthy,
            (await Run(dir, reason: "unreachable", setupComplete: false)).Status);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~LibrarySourceCheckTests`
Expected: FAIL — `LibrarySourceCheck` does not exist, and `ILibrarySource` has no `CheckAsync`/`FetchPosterAsync`.

- [ ] **Step 3: Widen the interface**

In `src/Themearr.API/Services/Sources/ILibrarySource.cs`, add these two members inside the interface, after `FetchAsync`:

```csharp
    /// <summary>
    /// Streams this source's poster for <paramref name="sourceRef"/>, or null when it
    /// has none. The caller proxies the bytes same-origin, so the source's credentials
    /// never reach the browser.
    /// </summary>
    Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct);

    /// <summary>Null when healthy, otherwise a user-facing reason. Never raw exception text.</summary>
    Task<string?> CheckAsync(CancellationToken ct);
```

- [ ] **Step 4: Implement both members on `PlexLibrarySource`**

Replace `src/Themearr.API/Services/Sources/PlexLibrarySource.cs` entirely:

```csharp
using System.Net;
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Plex as a library source. A thin adapter: all of the Plex API work stays in
/// <see cref="PlexService"/>.
/// </summary>
public class PlexLibrarySource(PlexService plex, Database db, IHttpClientFactory factory) : ILibrarySource
{
    /// <summary>Named client, configured in Program.cs with a short timeout.</summary>
    public const string ClientName = "plex-health";

    public string Name => "plex";

    /// <summary>Scanning a Plex library is expensive, so once a day.</summary>
    public TimeSpan SyncInterval => TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
        await plex.FetchMoviesAsync(log);

    public async Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct)
    {
        // Plex needs BOTH identifiers, so source_ref carries "{serverId}:{ratingKey}".
        var parts = (sourceRef ?? "").Split(':', 2);
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty)) return null;
        if (!db.GetPlexServersDict().TryGetValue(parts[0], out var srv)) return null;

        var height = (int)Math.Round(width * 1.5);   // 2:3 poster aspect
        var url = PlexImageUrl.Transcode(srv.Url, parts[1], srv.Token, width, height);

        var http = factory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) { resp.Dispose(); return null; }
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        var servers = db.GetPlexServersDict();
        if (servers.Count == 0) return null;   // nothing configured is not a fault

        var (url, token) = servers.First().Value;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return null;

        var http = factory.CreateClient(ClientName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/identity");
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return "Plex rejected the stored token (401). Sign in to Plex again in Settings.";
            if (!response.IsSuccessStatusCode)
                return $"The Plex server returned HTTP {(int)response.StatusCode}.";
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"The Plex server did not respond within {http.Timeout.TotalSeconds:0} seconds.";
        }
        catch (HttpRequestException)
        {
            return "The Plex server is unreachable. Check it is running and the URL in Settings is correct.";
        }
    }
}
```

- [ ] **Step 5: Write the health check**

Create `src/Themearr.API/Services/Health/LibrarySourceCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Services.Health;

/// <summary>
/// Reports on whichever library source is active. One check rather than one per source:
/// only the configured source can ever be relevant, so a second would be noise, and a
/// third source gets health coverage for free.
/// </summary>
public sealed class LibrarySourceCheck(Database db, LibrarySourceResolver sources) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Before setup there is nothing configured; a fresh install is not broken.
        if (!db.IsSetupComplete()) return HealthCheckResult.Healthy("Setup not complete");

        var source = sources.Active;
        var reason = await source.CheckAsync(cancellationToken);
        return reason is null
            ? HealthCheckResult.Healthy($"{source.Name} is reachable")
            : HealthCheckResult.Unhealthy(reason);
    }
}
```

Then delete `src/Themearr.API/Services/Health/PlexReachableCheck.cs`.

- [ ] **Step 6: Update the resolver test's fake and the registration**

In `tests/Themearr.API.Tests/LibrarySourceResolverTests.cs`, the nested `FakeSource` must satisfy the widened interface. Add these two members to it:

```csharp
        public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct) =>
            Task.FromResult<Stream?>(null);

        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
```

In `src/Themearr.API/Program.cs`, change the health-client registration and the check list. Replace:

```csharp
builder.Services.AddHttpClient(Themearr.API.Services.Health.PlexReachableCheck.ClientName,
```

with:

```csharp
builder.Services.AddHttpClient(Themearr.API.Services.Sources.PlexLibrarySource.ClientName,
```

and replace the line:

```csharp
    .AddCheck<Themearr.API.Services.Health.PlexReachableCheck>("plex")
```

with:

```csharp
    .AddCheck<Themearr.API.Services.Health.LibrarySourceCheck>("librarySource")
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded with 0 warnings; all tests passing. Report the total.

- [ ] **Step 8: Commit**

```bash
git add src/Themearr.API/Services/Sources/ src/Themearr.API/Services/Health/ src/Themearr.API/Program.cs tests/
git commit -m "feat(sources): report health for whichever library source is active"
```

---

### Task 3: `RadarrLibrarySource`

**Files:**
- Create: `src/Themearr.API/Services/Sources/RadarrLibrarySource.cs`
- Test: `tests/Themearr.API.Tests/RadarrLibrarySourceTests.cs`

**Interfaces:**
- Consumes: `LocalFolderResolver.Resolve(string) -> (string folder, string mode)`, `Database.GetSetting(key, default)`
- Produces: `RadarrLibrarySource : ILibrarySource` with `Name => "radarr"`, `SyncInterval => TimeSpan.FromMinutes(15)`, and `ClientName` (const `"radarr"`)

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/RadarrLibrarySourceTests.cs`:

```csharp
using System.Net;
using System.Text;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class RadarrLibrarySourceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static (RadarrLibrarySource Source, Database Db) New(TempDir dir, HttpMessageHandler handler)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();
        db.SetSetting("radarr_url", "http://radarr.local:7878");
        db.SetSetting("radarr_api_key", "secret-radarr-key");
        return (new RadarrLibrarySource(db, new LocalFolderResolver(db), new StubFactory(handler)), db);
    }

    [Fact]
    public async Task Fetches_movies_and_resolves_their_folders()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        var handler = new StubHandler(_ => Json($$"""
            [{"id":7,"title":"Heat","year":1995,"hasFile":true,"path":"{{movieDir.Replace("\\","/")}}"}]
            """));
        var (source, _) = New(dir, handler);

        var movies = await source.FetchAsync(_ => { }, CancellationToken.None);

        var m = Assert.Single(movies);
        Assert.Equal(movieDir, m.Folder);
        Assert.Equal("Heat", m.Title);
        Assert.Equal(1995, m.Year);
        Assert.Equal("radarr", m.Source);
        Assert.Equal("7", m.SourceRef);
    }

    [Fact]
    public async Task Sends_the_api_key_as_a_header_not_a_query_parameter()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("[]"));
        var (source, _) = New(dir, handler);

        await source.FetchAsync(_ => { }, CancellationToken.None);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("secret-radarr-key", Assert.Single(values!));
        Assert.DoesNotContain("secret-radarr-key", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task Movies_without_a_file_are_skipped()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        var handler = new StubHandler(_ => Json($$"""
            [{"id":7,"title":"Heat","year":1995,"hasFile":false,"path":"{{movieDir.Replace("\\","/")}}"}]
            """));
        var (source, _) = New(dir, handler);

        Assert.Empty(await source.FetchAsync(_ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Movies_whose_folder_cannot_be_resolved_are_skipped_and_counted()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("""
            [{"id":9,"title":"Ghost","year":1990,"hasFile":true,"path":"/mnt/nowhere/Ghost (1990)"}]
            """));
        var (source, db) = New(dir, handler);

        Assert.Empty(await source.FetchAsync(_ => { }, CancellationToken.None));
        Assert.Equal("1", db.GetSetting("last_sync_unresolved_count", "0"));
        Assert.Contains("Ghost", db.GetSetting("last_sync_unresolved_sample", ""));
    }

    [Fact]
    public async Task A_401_reports_a_rejected_key_without_leaking_it()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (source, _) = New(dir, handler);

        var reason = await source.CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("401", reason);
        Assert.DoesNotContain("secret-radarr-key", reason);
    }

    [Fact]
    public async Task An_unreachable_server_reports_cleanly_without_exception_text()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused to http://radarr.local:7878 key=secret-radarr-key"));
        var (source, _) = New(dir, handler);

        var reason = await source.CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.DoesNotContain("secret-radarr-key", reason);
        Assert.DoesNotContain("Connection refused", reason);
    }

    [Fact]
    public async Task An_unconfigured_radarr_reports_what_is_missing()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();
        var source = new RadarrLibrarySource(db, new LocalFolderResolver(db),
            new StubFactory(new StubHandler(_ => Json("[]"))));

        var reason = await source.CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("not configured", reason, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~RadarrLibrarySourceTests`
Expected: FAIL — `The type or namespace name 'RadarrLibrarySource' could not be found`.

- [ ] **Step 3: Implement**

Create `src/Themearr.API/Services/Sources/RadarrLibrarySource.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Radarr as a library source. Radarr knows every movie's folder, title, year and
/// whether the film is actually downloaded — everything Themearr needs — so a Radarr
/// user needs no Plex at all. Because theme.mp3 is read by Jellyfin, Emby and Kodi
/// too, this is what makes Themearr useful to them.
/// </summary>
public class RadarrLibrarySource(Database db, LocalFolderResolver folders, IHttpClientFactory factory)
    : ILibrarySource
{
    /// <summary>Named client, configured in Program.cs with a short timeout.</summary>
    public const string ClientName = "radarr";

    public string Name => "radarr";

    /// <summary>Radarr is local and cheap to poll, so a new import gets its theme quickly.</summary>
    public TimeSpan SyncInterval => TimeSpan.FromMinutes(15);

    private (string Url, string Key) Config() =>
        (db.GetSetting("radarr_url", "").TrimEnd('/'), db.GetSetting("radarr_api_key", ""));

    private HttpRequestMessage Request(string url, string key, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{url}{path}");
        // Header, never a query parameter — the key must not end up in a URL that could
        // be logged by a proxy.
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return request;
    }

    public async Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct)
    {
        var (url, key) = Config();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Radarr is not configured — set its URL and API key in Settings.");

        log($"Fetching movies from Radarr at {url}");

        var http = factory.CreateClient(ClientName);
        using var request = Request(url, key, "/api/v3/movie");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Radarr returned HTTP {(int)response.StatusCode} listing movies.");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var movies = new List<MovieRecord>();
        var unresolvedCount = 0;
        var unresolvedSample = "";

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // Monitored but not downloaded: a folder may exist, but there is no film for
            // a theme to accompany yet.
            if (!item.TryGetProperty("hasFile", out var hasFile) || !hasFile.GetBoolean()) continue;

            var reported = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(reported)) continue;

            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var year  = item.TryGetProperty("year", out var y) && y.TryGetInt32(out var yr) && yr > 0
                ? yr : (int?)null;
            var id    = item.TryGetProperty("id", out var i) ? i.GetRawText().Trim('"') : "";

            // Radarr reports paths from its own filesystem's perspective, exactly as Plex
            // does — a container may call it /movies where Themearr sees /mnt/media.
            var (folder, _) = folders.Resolve(reported + "/placeholder.mkv");
            if (string.IsNullOrEmpty(folder))
            {
                unresolvedCount++;
                if (unresolvedSample.Length == 0) unresolvedSample = reported;
                log($"Skipping {title} — unresolved path: {reported}  (add a Path Mapping from this path to where it's mounted in Themearr)");
                continue;
            }

            movies.Add(new MovieRecord(folder, "radarr", id, title, year, reported));
        }

        // Read by LibraryPathsCheck; overwritten every sync so a fixed mapping clears it.
        db.SetSetting("last_sync_unresolved_count",  unresolvedCount.ToString());
        db.SetSetting("last_sync_unresolved_sample", unresolvedSample);

        log($"Radarr reported {movies.Count} downloaded movies");
        return movies;
    }

    public async Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct)
    {
        var (url, key) = Config();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(sourceRef))
            return null;

        var http = factory.CreateClient(ClientName);
        using var request = Request(url, key, $"/api/v3/mediacover/{Uri.EscapeDataString(sourceRef)}/poster.jpg");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { response.Dispose(); return null; }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        var (url, key) = Config();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            return "Radarr is not configured — set its URL and API key in Settings.";

        var http = factory.CreateClient(ClientName);
        try
        {
            using var request = Request(url, key, "/api/v3/system/status");
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return "Radarr rejected the API key (401). Check the key in Settings → Library source.";
            if (!response.IsSuccessStatusCode)
                return $"Radarr returned HTTP {(int)response.StatusCode}.";
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Radarr did not respond within {http.Timeout.TotalSeconds:0} seconds.";
        }
        catch (HttpRequestException)
        {
            return "Radarr is unreachable. Check it is running and the URL in Settings is correct.";
        }
    }
}
```

Note the `folders.Resolve(reported + "/placeholder.mkv")` call: `LocalFolderResolver` takes a **file** path and returns its folder, but Radarr reports the folder directly. Appending a dummy filename reuses the existing resolver unchanged rather than duplicating its logic.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~RadarrLibrarySourceTests`
Expected: PASS — 7 tests passed.

- [ ] **Step 5: Register it**

In `src/Themearr.API/Program.cs`, after the existing `PlexLibrarySource` registrations, add:

```csharp
builder.Services.AddSingleton<Themearr.API.Services.Sources.RadarrLibrarySource>();
builder.Services.AddSingleton<Themearr.API.Services.Sources.ILibrarySource>(
    sp => sp.GetRequiredService<Themearr.API.Services.Sources.RadarrLibrarySource>());
builder.Services.AddHttpClient(Themearr.API.Services.Sources.RadarrLibrarySource.ClientName,
    c => c.Timeout = TimeSpan.FromSeconds(10));
```

- [ ] **Step 6: Verify the whole suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded, 0 warnings; all tests passing. Report the total.

- [ ] **Step 7: Commit**

```bash
git add src/Themearr.API/Services/Sources/RadarrLibrarySource.cs src/Themearr.API/Program.cs \
        tests/Themearr.API.Tests/RadarrLibrarySourceTests.cs
git commit -m "feat(sources): add Radarr as a library source"
```

---

### Task 4: Radarr settings API

**Files:**
- Modify: `src/Themearr.API/Controllers/SettingsController.cs`
- Test: `tests/Themearr.API.Tests/RadarrSettingsTests.cs`

**Interfaces:**
- Produces:
  - `GET  /api/settings/radarr` → `{ source, url, configured }` — **never the key itself**
  - `POST /api/settings/radarr` → body `{ source, url, apiKey }`; a blank `apiKey` keeps the stored one
  - `POST /api/settings/radarr/test` → `{ ok, detail }`, tests the submitted values without saving

Follow the existing `rapidapi` endpoints in this same controller as the pattern: the GET reports only whether a credential is present.

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/RadarrSettingsTests.cs`:

```csharp
using Themearr.API.Data;

namespace Themearr.API.Tests;

/// <summary>
/// The controller is thin; these lock the two rules that matter — the key never leaves
/// the server, and a blank key on save means "keep", never "erase".
/// </summary>
public class RadarrSettingsTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    [Fact]
    public void Saving_a_blank_key_keeps_the_existing_one()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("radarr_api_key", "original-key");

        // Mirrors SettingsController.SaveRadarr's rule.
        var incoming = "   ";
        if (!string.IsNullOrWhiteSpace(incoming)) db.SetSetting("radarr_api_key", incoming.Trim());

        Assert.Equal("original-key", db.GetSetting("radarr_api_key", ""));
    }

    [Fact]
    public void Saving_a_new_key_replaces_the_old_one()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("radarr_api_key", "original-key");

        var incoming = "new-key";
        if (!string.IsNullOrWhiteSpace(incoming)) db.SetSetting("radarr_api_key", incoming.Trim());

        Assert.Equal("new-key", db.GetSetting("radarr_api_key", ""));
    }

    [Fact]
    public void The_library_source_setting_defaults_to_plex()
    {
        using var dir = new TempDir();
        Assert.Equal("plex", NewDb(dir).GetSetting("library_source", "plex"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~RadarrSettingsTests`
Expected: PASS — these assert on `Database` behaviour that already exists. They are guard-rails for the rule the controller must follow, not a driver for it. Proceed to Step 3 and keep them green.

- [ ] **Step 3: Add the endpoints**

In `src/Themearr.API/Controllers/SettingsController.cs`, add these members at the end of the class, before its closing brace. Add `using Themearr.API.Services.Sources;` to the file's usings, and add `LibrarySourceResolver sources` and `IEnumerable<ILibrarySource> allSources` to the controller's primary constructor parameters.

```csharp
    [HttpGet("radarr")]
    public IActionResult GetRadarr() => Ok(new
    {
        source     = db.GetSetting("library_source", "plex"),
        url        = db.GetSetting("radarr_url", ""),
        // The key itself is never returned — same rule as the RapidAPI endpoint above.
        configured = !string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")),
    });

    [HttpPost("radarr")]
    [Consumes("application/json")]
    public IActionResult SaveRadarr([FromBody] RadarrPayload payload)
    {
        var source = (payload.Source ?? "plex").Trim();
        if (source is not ("plex" or "radarr"))
            return BadRequest(new { detail = "Library source must be 'plex' or 'radarr'." });

        if (source == "radarr")
        {
            if (string.IsNullOrWhiteSpace(payload.Url))
                return BadRequest(new { detail = "Radarr URL cannot be empty." });
            if (string.IsNullOrWhiteSpace(payload.ApiKey) &&
                string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")))
                return BadRequest(new { detail = "Radarr API key cannot be empty." });
        }

        db.SetSetting("library_source", source);
        db.SetSetting("radarr_url", (payload.Url ?? "").Trim().TrimEnd('/'));
        // A blank key means "keep what you had" — the UI never receives the stored key,
        // so submitting the form unchanged must not wipe it.
        if (!string.IsNullOrWhiteSpace(payload.ApiKey))
            db.SetSetting("radarr_api_key", payload.ApiKey.Trim());

        return Ok(new { source, configured = !string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")) });
    }

    [HttpPost("radarr/test")]
    [Consumes("application/json")]
    public async Task<IActionResult> TestRadarr([FromBody] RadarrPayload payload, CancellationToken ct)
    {
        // Test what the user is about to save, not what is stored, so a wrong key is
        // caught while they are still looking at the field.
        var previousUrl = db.GetSetting("radarr_url", "");
        var previousKey = db.GetSetting("radarr_api_key", "");
        try
        {
            db.SetSetting("radarr_url", (payload.Url ?? "").Trim().TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(payload.ApiKey)) db.SetSetting("radarr_api_key", payload.ApiKey.Trim());

            var radarr = allSources.First(s => s.Name == "radarr");
            var reason = await radarr.CheckAsync(ct);
            return Ok(new { ok = reason is null, detail = reason ?? "Radarr is reachable." });
        }
        finally
        {
            db.SetSetting("radarr_url", previousUrl);
            db.SetSetting("radarr_api_key", previousKey);
        }
    }

    public record RadarrPayload(string? Source, string? Url, string? ApiKey);
```

- [ ] **Step 4: Verify**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded, 0 warnings; all tests passing. Report the total.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Controllers/SettingsController.cs tests/Themearr.API.Tests/RadarrSettingsTests.cs
git commit -m "feat(settings): add Radarr configuration endpoints"
```

---

### Task 5: Source-aware posters

**Files:**
- Modify: `src/Themearr.API/Controllers/PosterController.cs`
- Modify: `src/Themearr.API/Controllers/MoviesController.cs`
- Modify: `src/Themearr.API/Controllers/StatsController.cs`

**Interfaces:**
- Consumes: `ILibrarySource.FetchPosterAsync(sourceRef, width, ct)` (Task 2), `LibrarySourceResolver.Active`

`PosterController` currently parses Plex's `"{serverId}:{ratingKey}"` itself, and `MoviesController`/`StatsController` each repeat that parse to decide whether to emit a poster URL. That duplication is now wrong: `source_ref` is opaque to everything but its issuing source.

- [ ] **Step 1: Route the fetch through the active source**

In `src/Themearr.API/Controllers/PosterController.cs`, add `using Themearr.API.Services.Sources;`, add `LibrarySourceResolver sources` to the primary constructor, and replace everything from `var movie = db.GetMovie(id);` down to the end of the Plex fetch with:

```csharp
        var movie = db.GetMovie(id);
        var source = sources.Active;
        // source_ref is opaque outside its own source, so the source fetches its own poster.
        if (movie?.GetValueOrDefault("source")?.ToString() != source.Name) return NotFound();

        var width = Math.Clamp(w ?? DefaultWidth, 40, MaxWidth);
        var sourceRef = movie.GetValueOrDefault("sourceRef")?.ToString() ?? "";

        try
        {
            await using var stream = await source.FetchPosterAsync(sourceRef, width, HttpContext.RequestAborted);
            if (stream is null) return NotFound();

            using var buffer = new MemoryStream();
            await StreamLimits.CopyWithLimitAsync(stream, buffer, StreamLimits.MaxPosterBytes);
            buffer.Position = 0;
            return File(buffer.ToArray(), "image/jpeg");
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Poster fetch failed for {Id}", LogSanitizer.Clean(id));
            return NotFound();
        }
```

Read the existing method first and keep its signature, the signature verification, and the `DefaultWidth`/`MaxWidth` constants exactly as they are. If the existing code returns the image with a different content type or caching header, preserve that rather than the `image/jpeg` above.

- [ ] **Step 2: Stop the other two controllers parsing `source_ref`**

In both `src/Themearr.API/Controllers/MoviesController.cs` and `src/Themearr.API/Controllers/StatsController.cs`, find where each decides whether to emit `posterUrl` by splitting `sourceRef` on a colon. Replace that condition with a check that the movie's `source` matches the active source and its `sourceRef` is non-empty. Add `using Themearr.API.Services.Sources;` and a `LibrarySourceResolver sources` constructor parameter to each.

The condition becomes, in both files:

```csharp
        var activeSource = sources.Active.Name;
        // ... per movie:
        var hasPoster = movie.GetValueOrDefault("source")?.ToString() == activeSource
                     && !string.IsNullOrEmpty(movie.GetValueOrDefault("sourceRef")?.ToString());
```

and `posterUrl` is emitted only when `hasPoster`. Keep the signed-URL construction exactly as it is.

- [ ] **Step 3: Verify**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded, 0 warnings; all tests passing.

Then confirm no controller parses a source ref any more:
```bash
grep -rn "sourceRef.*Split\|Split.*sourceRef" src/Themearr.API/Controllers/
```
Expected: no output.

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.API/Controllers/
git commit -m "feat(posters): fetch posters through the active library source"
```

---

### Task 6: Sync cadence follows the source

**Files:**
- Modify: `src/Themearr.API/Services/AutoSyncService.cs`

**Interfaces:**
- Consumes: `TaskRegistry.UpdateInterval(id, interval)` (Task 1), `LibrarySourceResolver.Active.SyncInterval`

- [ ] **Step 1: Keep the registered interval in step with the source**

In `src/Themearr.API/Services/AutoSyncService.cs`, find the loop in `ExecuteAsync`. Immediately before each `TryAutoSync` call, refresh the registered interval so the Tasks page reflects the active source:

```csharp
            // The interval is a property of the active source, which the user can change
            // at runtime. Register captured it once at startup, so refresh it each cycle
            // rather than re-registering, which would wipe the task's run history.
            registry.UpdateInterval(SyncTaskId, SyncInterval);
```

- [ ] **Step 2: Verify**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded, 0 warnings; all tests passing.

- [ ] **Step 3: Commit**

```bash
git add src/Themearr.API/Services/AutoSyncService.cs
git commit -m "feat(sync): keep the displayed sync cadence in step with the active source"
```

---

### Task 7: Settings UI for the library source

**Files:**
- Modify: `src/Themearr.Web/src/lib/types.ts`
- Modify: `src/Themearr.Web/src/lib/api.ts`
- Modify: `src/Themearr.Web/src/app/settings/page.tsx`

**Interfaces:**
- Consumes: the endpoints from Task 4
- Produces: `RadarrSettings` type and `radarrApi.{get,save,test}`

- [ ] **Step 1: Add the type and client**

Append to `src/Themearr.Web/src/lib/types.ts`:

```ts
export interface RadarrSettings {
  source: 'plex' | 'radarr'
  url: string
  /** The API key is never sent to the browser; this only says whether one is stored. */
  configured: boolean
}
```

In `src/Themearr.Web/src/lib/api.ts`, add `RadarrSettings` to the existing `import type` list and append:

```ts
// ── Library source (Radarr) ───────────────────────────────────────────────────

export const radarrApi = {
  get: () => request<RadarrSettings>('/api/settings/radarr'),
  save: (source: string, url: string, apiKey: string) =>
    request<{ source: string; configured: boolean }>('/api/settings/radarr', {
      method: 'POST',
      body: JSON.stringify({ source, url, apiKey }),
    }),
  test: (url: string, apiKey: string) =>
    request<{ ok: boolean; detail: string }>('/api/settings/radarr/test', {
      method: 'POST',
      body: JSON.stringify({ source: 'radarr', url, apiKey }),
    }),
}
```

- [ ] **Step 2: Add a Library source section to Settings**

Read `src/Themearr.Web/src/app/settings/page.tsx` and follow the structure of an existing section (the RapidAPI one is the closest analogue — it also handles a write-only credential). Add a **Library source** section containing:

- a two-option choice between **Plex** and **Radarr**
- when Radarr is selected: a **URL** field, an **API key** field (`type="password"`, placeholder `Leave blank to keep the current key` when `configured` is true), a **Test connection** button showing the returned `detail`, and a **Save** button
- when Plex is selected: no extra fields, just Save

Load current values with `radarrApi.get()` on mount. Use the page's existing state, loading and error conventions rather than inventing new ones.

- [ ] **Step 3: Verify**

Run from `src/Themearr.Web`:
```bash
npx tsc --noEmit && npm run lint && npm run build
```
Expected: typecheck clean, lint 0 errors with 3 pre-existing warnings, build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.Web/src/lib/types.ts src/Themearr.Web/src/lib/api.ts src/Themearr.Web/src/app/settings/page.tsx
git commit -m "feat(web): add library source settings with Radarr configuration"
```

---

### Task 8: Setup wizard source branch

Without this, a Radarr user still cannot finish setup — the wizard's first two steps are Plex-only — so the whole feature stays unreachable for the people it exists for.

**Files:**
- Modify: `src/Themearr.Web/src/components/setup/SetupWizard.tsx`
- Modify: `src/Themearr.API/Controllers/SetupController.cs`

**Interfaces:**
- Consumes: `radarrApi.{save,test}` (Task 7)
- Produces: `POST /api/setup/complete` — marks setup complete for a non-Plex install

- [ ] **Step 1: Add the completion endpoint**

The wizard's Plex branch reaches `setup_complete` through `POST /api/setup/plex/selection`. The Radarr branch needs its own way to finish. In `src/Themearr.API/Controllers/SetupController.cs`, add:

```csharp
    /// <summary>
    /// Marks setup complete for an install that is not using Plex. The Plex branch
    /// finishes via plex/selection; a Radarr user never touches those endpoints.
    /// </summary>
    [HttpPost("complete")]
    public IActionResult Complete()
    {
        if (db.GetSetting("library_source", "plex") != "radarr")
            return BadRequest(new { detail = "Only a non-Plex library source can complete setup this way." });
        if (string.IsNullOrWhiteSpace(db.GetSetting("radarr_url", "")) ||
            string.IsNullOrWhiteSpace(db.GetSetting("radarr_api_key", "")))
            return BadRequest(new { detail = "Configure Radarr before completing setup." });

        db.MarkSetupComplete();
        return Ok(new { setupComplete = true });
    }
```

Add a matching client function in `src/Themearr.Web/src/lib/api.ts` on the existing `setupApi` object:

```ts
  complete: () => request<{ setupComplete: boolean }>('/api/setup/complete', { method: 'POST' }),
```

- [ ] **Step 2: Add the source-select step**

In `src/Themearr.Web/src/components/setup/SetupWizard.tsx`, the `Step` type is currently:

```tsx
type Step = 'server-select' | 'library-select' | 'path-config'
```

Change it to:

```tsx
type Step = 'source-select' | 'server-select' | 'library-select' | 'radarr-connect' | 'path-config'
```

Make `source-select` the initial step. It offers Plex or Radarr:
- choosing **Plex** advances to `server-select`, leaving the existing flow untouched
- choosing **Radarr** advances to `radarr-connect`

`radarr-connect` collects a URL and API key with a **Test connection** button (using `radarrApi.test`), saves via `radarrApi.save('radarr', url, apiKey)`, and advances to `path-config`. Do not let it advance until a test has succeeded — a wrong key discovered at first sync is far worse than one discovered here.

`path-config` is already source-agnostic and is reused unchanged, except that when the source is Radarr its "Finish" action must call `setupApi.complete()` instead of the Plex selection endpoint.

Update the `STEPS` array that drives `StepIndicator` so it shows only the steps on the chosen branch — a Radarr user must not see "Select server" as a pending step they will never reach.

- [ ] **Step 3: Verify**

Run from `src/Themearr.Web`:
```bash
npx tsc --noEmit && npm run lint && npm run build
```
Expected: all clean.

Then from the repository root: `dotnet build src/Themearr.API && dotnet test` — expect all passing.

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.Web/src/components/setup/SetupWizard.tsx src/Themearr.Web/src/lib/api.ts \
        src/Themearr.API/Controllers/SetupController.cs
git commit -m "feat(setup): let a non-Plex install complete setup with Radarr"
```

---

### Task 9: End-to-end verification and README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Verify a Radarr install works end to end**

Run a real instance against a stub Radarr. From the repository root:

```bash
cd src/Themearr.Web && npm run build && cd ../..
SCRATCH=$(mktemp -d)
mkdir -p "$SCRATCH/movies/Heat (1995)" "$SCRATCH/movies/Ronin (1998)"
dotnet publish src/Themearr.API/Themearr.API.csproj -c Release -o "$SCRATCH/app"
cp -r src/Themearr.Web/out "$SCRATCH/app/wwwroot"

# Minimal stub Radarr on 7878
cat > "$SCRATCH/radarr.py" <<PY
import json, http.server
M = [{"id": 7, "title": "Heat", "year": 1995, "hasFile": True, "path": "$SCRATCH/movies/Heat (1995)"},
     {"id": 8, "title": "Ronin", "year": 1998, "hasFile": False, "path": "$SCRATCH/movies/Ronin (1998)"}]
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.headers.get("X-Api-Key") != "test-key": self.send_error(401); return
        body = json.dumps(M if self.path.startswith("/api/v3/movie") else {"version": "5.0"}).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def log_message(self, *a): pass
http.server.HTTPServer(("127.0.0.1", 7878), H).serve_forever()
PY
python3 "$SCRATCH/radarr.py" &
RADARR=$!

THEMEARR_AUTH_TOKEN=b2-verify-token-abcdef DB_PATH="$SCRATCH/themearr.db" \
  ASPNETCORE_URLS=http://127.0.0.1:5195 dotnet "$SCRATCH/app/Themearr.API.dll" &
sleep 8

AUTH='Authorization: Bearer b2-verify-token-abcdef'
curl -s -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"source\":\"radarr\",\"url\":\"http://127.0.0.1:7878\",\"apiKey\":\"test-key\"}" \
  http://127.0.0.1:5195/api/settings/radarr
echo
curl -s -X POST -H "$AUTH" -H 'Content-Type: application/json' \
  -d "{\"libraryPaths\":[\"$SCRATCH/movies\"]}" http://127.0.0.1:5195/api/settings >/dev/null
curl -s -X POST -H "$AUTH" http://127.0.0.1:5195/api/setup/complete; echo
curl -s -X POST -H "$AUTH" http://127.0.0.1:5195/api/system/tasks/syncLibrary/run; echo
sleep 6
echo "--- movies ---"; curl -s -H "$AUTH" http://127.0.0.1:5195/api/movies
echo; echo "--- key must NOT appear ---"; curl -s -H "$AUTH" http://127.0.0.1:5195/api/settings/radarr
kill $RADARR %2 2>/dev/null; rm -rf "$SCRATCH"
```

All of these must hold:
- `/api/setup/complete` returns `setupComplete: true`
- after the sync, `/api/movies` contains **Heat only** — Ronin has `hasFile: false` and must be skipped
- Heat's `source` is `radarr` and its `sourceRef` is `7`
- `/api/settings/radarr` reports `configured: true` and **does not contain `test-key` anywhere**

If the library-paths POST shape differs from the guess above, read `SettingsController.Save` and use the real one. Report exactly what you observed; if a check fails, report it rather than adjusting it.

- [ ] **Step 2: Update the README**

In `README.md`, change the opening line under the logo from `Automatic movie theme song downloader for Plex libraries.` to:

```
Automatic movie theme song downloader for Plex, Jellyfin, Emby and Kodi libraries.
```

Then add this section immediately before `## Updating`:

```markdown
## Library source: Plex or Radarr

Themearr can read your movie list from either **Plex** or **Radarr**, chosen during
setup and changeable later under **Settings → Library source**.

Radarr is what makes Themearr useful without Plex. It already knows every movie's
folder, title, year and whether the film is downloaded — and because `theme.mp3` is
read by Jellyfin, Emby and Kodi as well as Plex, a Radarr-sourced library serves all
of them. You need a Radarr URL and API key (Radarr → Settings → General → API Key).

Radarr is local and cheap to poll, so it syncs every 15 minutes rather than daily —
a newly imported movie usually has its theme within minutes.

Movies Radarr is monitoring but has not downloaded yet are skipped: there is no film
for a theme to accompany. Path Mappings work exactly as they do for Plex, and are
often needed, since Radarr in a container reports its own paths.

Switching source keeps everything. Both sources describe the same folders on disk,
and Themearr identifies a movie by its folder, so your download history and ignored
movies survive the change.
```

Do not alter the headings `### Library paths & path mappings` or `## Downloads require a RapidAPI key` — the in-app health checks link to their exact anchors, and renaming either silently breaks those links.

- [ ] **Step 3: Final verification**

```bash
dotnet test
cd src/Themearr.Web && npx tsc --noEmit && npm run lint && npm run build
```
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs(readme): document Radarr as a library source"
```

---

## Self-review notes

**Spec coverage (B2 rows).** `RadarrLibrarySource` → Task 3; settings and secret handling → Tasks 4 and 7; the setup-wizard branch → Task 8; source-aware posters → Task 5; `LibrarySourceCheck` → Task 2; `TaskRegistry.UpdateInterval` → Tasks 1 and 6; verification and docs → Task 9.

**One deliberate departure from the spec.** The spec says Radarr's key should be redacted "exactly like Plex tokens", i.e. via `GetPlexServersRedacted`/`SetPlexServersMergingTokens`. This plan instead follows the **RapidAPI** precedent in the same controller, where the GET returns only `{ configured: bool }` and the secret never leaves the server at all. That is strictly stronger than redaction and simpler, and it is the closer analogue — a single credential rather than a list of servers.

**Task 4's tests are guard-rails, not drivers.** They assert the keep-on-blank rule against `Database` directly, so they pass before the controller exists. That is called out in the task. There are no controller-level tests in this codebase to follow, and standing up `WebApplicationFactory` for three endpoints would be a larger change than the endpoints themselves; Task 9 covers them end to end instead.

**Known ordering constraint.** Task 2 widens `ILibrarySource`, so `PlexLibrarySource` and both test fakes must gain the new members in that same task or the build breaks. Task 3's `RadarrLibrarySource` then implements the full interface from the start.

**Type consistency.** `MovieRecord(Folder, Source, SourceRef, Title, Year, SourcePath)` is used identically in Tasks 3 and 5. `ILibrarySource`'s members are defined in Task 2 and consumed in Tasks 3, 5 and 6. The settings keys `library_source`, `radarr_url` and `radarr_api_key` are written in Task 4 and read in Tasks 3 and 8.
