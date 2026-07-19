# System Page (Health & Tasks) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an arr-style **System** page to Themearr with a Health tab (four checks) and a Tasks tab (library sync with *Run now*), plus an unauthenticated `/health` endpoint for external monitoring.

**Architecture:** Each health check is a standard ASP.NET Core `IHealthCheck`, so the framework provides parallel execution, per-check exception isolation and a free `/health` endpoint; a mapper reshapes `HealthReport` into Radarr's `{source, type, message, wikiUrl}` DTO for the UI. A `TaskRegistry` singleton decouples the API from background workers — workers push run state in and pull trigger signals out, and the controller does the mirror image, so neither holds a reference to the other.

**Tech Stack:** .NET 10 (ASP.NET Core), `Microsoft.Extensions.Diagnostics.HealthChecks` (in the shared framework — no package reference needed), `System.Threading.Channels`, xUnit, React 19 + Vite, React Router, Tailwind classes inline.

**Spec:** `docs/superpowers/specs/2026-07-19-system-health-tasks-design.md`

## Global Constraints

- **No health check may consume RapidAPI quota.** youtube-mp36 has no free quota endpoint, so probing it spends a real request. `RapidApiCheck` is passive: it reads `IThemeAudioProvider.CheckConfiguration()` and `DownloadService.IsQuotaCoolingDown()` only.
- **Never surface raw exception text from a check.** Plex requests carry `X-Plex-Token` and exception messages can echo request URLs. Every check returns a hand-written message; anything logged goes through `LogSanitizer.Clean()`.
- **No false alarms.** When `Database.IsSetupComplete()` is false, `LibraryPathsCheck`, `PlexReachableCheck` and `RapidApiCheck` return `Healthy`. When the `auto_download` setting is not `"true"`, `DownloadWorkerCheck` returns `Healthy`.
- **Additive in scope — no opportunistic refactoring.** Three existing files change, each because the feature requires it: `AutoSyncService`'s wait loop is restructured to race the sleep against a trigger (Task 2), `PlexService` gains unresolved-path counters (Task 3), and `AutoDownloadService`/`DownloadService` each gain a narrow interface declaration (Tasks 6–7). Nothing else may be reorganised, renamed, or "cleaned up". `ApiAuthMiddleware` is not modified — it guards only `/api/*`, and `/health` sits outside that prefix.
- Target framework is `net10.0` for both projects. C# nullable reference types are enabled; use primary constructors, matching surrounding style.
- Run backend tests with `dotnet test` from the repository root.

---

### Task 1: `TaskRegistry`

The decoupling seam. Pure in-memory, no dependencies, fully unit-testable.

**Files:**
- Create: `src/Themearr.API/Services/TaskRegistry.cs`
- Test: `tests/Themearr.API.Tests/TaskRegistryTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `record TaskState(string Id, string Name, TimeSpan Interval, DateTime? LastRunUtc, long? LastDurationMs, string? LastResult, DateTime? NextRunUtc, bool IsRunning)`
  - `TaskRegistry.Register(string id, string name, TimeSpan interval)`
  - `TaskRegistry.Exists(string id) -> bool`
  - `TaskRegistry.Trigger(string id) -> bool`
  - `TaskRegistry.WaitForTriggerAsync(string id, CancellationToken ct) -> Task`
  - `TaskRegistry.MarkRunning(string id, bool running)`
  - `TaskRegistry.RecordRun(string id, DateTime startedUtc, TimeSpan duration, string result)`
  - `TaskRegistry.Snapshot() -> IReadOnlyList<TaskState>`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/TaskRegistryTests.cs`:

```csharp
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class TaskRegistryTests
{
    private static TaskRegistry WithSync()
    {
        var r = new TaskRegistry();
        r.Register("syncLibrary", "Sync Library", TimeSpan.FromHours(24));
        return r;
    }

    [Fact]
    public void Exists_is_true_only_for_registered_ids()
    {
        var r = WithSync();
        Assert.True(r.Exists("syncLibrary"));
        Assert.False(r.Exists("nope"));
    }

    [Fact]
    public void Trigger_returns_false_for_unknown_id()
    {
        Assert.False(WithSync().Trigger("nope"));
    }

    [Fact]
    public async Task Trigger_wakes_a_waiter()
    {
        var r = WithSync();
        var waiter = r.WaitForTriggerAsync("syncLibrary", CancellationToken.None);

        Assert.True(r.Trigger("syncLibrary"));

        await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(waiter.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Five_rapid_triggers_coalesce_into_one_run()
    {
        var r = WithSync();

        // Capacity is 1 with DropWrite: the first write lands, the rest are dropped.
        Assert.True(r.Trigger("syncLibrary"));
        for (var i = 0; i < 4; i++) Assert.False(r.Trigger("syncLibrary"));

        // Exactly one wake is available.
        await r.WaitForTriggerAsync("syncLibrary", CancellationToken.None)
               .WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => r.WaitForTriggerAsync("syncLibrary", cts.Token));
    }

    [Fact]
    public void Snapshot_derives_nextRun_from_lastRun_plus_interval()
    {
        var r = WithSync();
        var started = new DateTime(2026, 7, 19, 2, 0, 0, DateTimeKind.Utc);

        r.RecordRun("syncLibrary", started, TimeSpan.FromMilliseconds(4210), "1451 movies");

        var t = Assert.Single(r.Snapshot());
        Assert.Equal(started, t.LastRunUtc);
        Assert.Equal(4210, t.LastDurationMs);
        Assert.Equal("1451 movies", t.LastResult);
        Assert.Equal(started.AddHours(24), t.NextRunUtc);
        Assert.False(t.IsRunning);
    }

    [Fact]
    public void Snapshot_has_no_nextRun_before_the_first_run()
    {
        var t = Assert.Single(WithSync().Snapshot());
        Assert.Null(t.LastRunUtc);
        Assert.Null(t.NextRunUtc);
    }

    [Fact]
    public void RecordRun_clears_the_running_flag()
    {
        var r = WithSync();
        r.MarkRunning("syncLibrary", true);
        Assert.True(Assert.Single(r.Snapshot()).IsRunning);

        r.RecordRun("syncLibrary", DateTime.UtcNow, TimeSpan.Zero, "done");
        Assert.False(Assert.Single(r.Snapshot()).IsRunning);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~TaskRegistryTests`
Expected: FAIL — compile error, `The type or namespace name 'TaskRegistry' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Themearr.API/Services/TaskRegistry.cs`:

```csharp
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Themearr.API.Services;

/// <summary>A scheduled task's state as shown on the System → Tasks tab.</summary>
public sealed record TaskState(
    string    Id,
    string    Name,
    TimeSpan  Interval,
    DateTime? LastRunUtc,
    long?     LastDurationMs,
    string?   LastResult,
    DateTime? NextRunUtc,
    bool      IsRunning);

/// <summary>
/// Decouples the System controller from the background workers. Workers push run
/// state in via <see cref="RecordRun"/> and pull wake-ups out via
/// <see cref="WaitForTriggerAsync"/>; the controller does the mirror image. Neither
/// side holds a reference to the other, so "Run now wakes the task" is testable
/// without a host or a timer.
/// </summary>
public sealed class TaskRegistry
{
    private sealed class Entry
    {
        public required string   Name     { get; init; }
        public required TimeSpan Interval { get; init; }

        public DateTime? LastRunUtc;
        public long?     LastDurationMs;
        public string?   LastResult;
        public bool      IsRunning;

        // Capacity 1 + DropWrite is the whole debounce: an impatient user clicking
        // "Run now" five times queues one run, not five library syncs.
        public readonly Channel<byte> Trigger = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    }

    private readonly ConcurrentDictionary<string, Entry> _tasks = new();

    public void Register(string id, string name, TimeSpan interval) =>
        _tasks[id] = new Entry { Name = name, Interval = interval };

    public bool Exists(string id) => _tasks.ContainsKey(id);

    /// <summary>True if a wake-up was queued; false for an unknown id or when one is already pending.</summary>
    public bool Trigger(string id) =>
        _tasks.TryGetValue(id, out var e) && e.Trigger.Writer.TryWrite(0);

    /// <summary>Completes when someone triggers this task. An unknown id waits forever (until cancelled).</summary>
    public async Task WaitForTriggerAsync(string id, CancellationToken ct)
    {
        if (!_tasks.TryGetValue(id, out var e))
        {
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }
        await e.Trigger.Reader.ReadAsync(ct);
    }

    public void MarkRunning(string id, bool running)
    {
        if (_tasks.TryGetValue(id, out var e)) e.IsRunning = running;
    }

    public void RecordRun(string id, DateTime startedUtc, TimeSpan duration, string result)
    {
        if (!_tasks.TryGetValue(id, out var e)) return;
        e.LastRunUtc     = startedUtc;
        e.LastDurationMs = (long)duration.TotalMilliseconds;
        e.LastResult     = result;
        e.IsRunning      = false;
    }

    public IReadOnlyList<TaskState> Snapshot() =>
        _tasks
            .Select(kv => new TaskState(
                kv.Key,
                kv.Value.Name,
                kv.Value.Interval,
                kv.Value.LastRunUtc,
                kv.Value.LastDurationMs,
                kv.Value.LastResult,
                kv.Value.LastRunUtc is { } last ? last + kv.Value.Interval : null,
                kv.Value.IsRunning))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~TaskRegistryTests`
Expected: PASS — 6 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/TaskRegistry.cs tests/Themearr.API.Tests/TaskRegistryTests.cs
git commit -m "feat(system): add TaskRegistry for scheduled task state and triggers"
```

---

### Task 2: Wire `AutoSyncService` to the registry

Report runs into the registry and race the sleep against a trigger, so *Run now* wakes the worker instead of waiting up to 30 minutes.

**Files:**
- Modify: `src/Themearr.API/Services/AutoSyncService.cs` (whole file rewritten below)

**Interfaces:**
- Consumes: `TaskRegistry` (Task 1) — `Register`, `RecordRun`, `MarkRunning`, `WaitForTriggerAsync`
- Produces: `AutoSyncService.SyncTaskId` (const `"syncLibrary"`), used by the controller and by Task 9's registration check

- [ ] **Step 1: Rewrite the service**

Replace the entire contents of `src/Themearr.API/Services/AutoSyncService.cs`:

```csharp
using System.Diagnostics;
using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Background service that triggers a Plex sync once per day when auto-sync is enabled.
/// Also serves the System → Tasks "Sync Library" row: it reports each run into the
/// <see cref="TaskRegistry"/> and wakes early when the user clicks "Run now".
/// </summary>
public class AutoSyncService(IServiceProvider services, TaskRegistry registry, ILogger<AutoSyncService> log)
    : BackgroundService
{
    public const string SyncTaskId = "syncLibrary";

    // Check every 30 minutes (±5 min jitter) whether a sync is due. Jitter keeps
    // retries from all firing on the same second after a Plex outage recovers.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JitterMax     = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SyncInterval  = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        registry.Register(SyncTaskId, "Sync Library", SyncInterval);
        SeedLastRunFromDatabase();

        // Delay startup by 2 minutes so the API is fully warmed up first
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        // A manual trigger forces a sync even when auto-sync is off or the 24h
        // interval has not elapsed — that is the entire point of "Run now".
        var forced = false;

        while (!ct.IsCancellationRequested)
        {
            try { await TryAutoSync(forced); }
            catch (Exception ex) { log.LogWarning(ex, "AutoSync check failed"); }

            forced = await WaitForNextAsync(ct);
        }
    }

    /// <summary>
    /// Restores "last run" across restarts from the timestamp auto-sync already
    /// persists, so the Tasks tab is not blank after every deploy.
    /// </summary>
    private void SeedLastRunFromDatabase()
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Database>();
            var raw = db.GetSetting("last_auto_sync_at", "");
            if (long.TryParse(raw, out var unix))
                registry.RecordRun(SyncTaskId,
                    DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
                    TimeSpan.Zero,
                    "completed on a previous run");
        }
        catch (Exception ex) { log.LogWarning(ex, "AutoSync: could not seed last-run state"); }
    }

    /// <summary>
    /// Sleeps until the next scheduled check OR until the task is triggered,
    /// whichever comes first. Returns true when woken by a trigger.
    /// The loser of the race is cancelled and awaited, so an abandoned reader can
    /// never sit on the trigger channel and swallow a later "Run now".
    /// </summary>
    private async Task<bool> WaitForNextAsync(CancellationToken ct)
    {
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(
            (int)-JitterMax.TotalMilliseconds,
            (int) JitterMax.TotalMilliseconds));

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trigger = registry.WaitForTriggerAsync(SyncTaskId, raceCts.Token);
        var delay   = Task.Delay(CheckInterval + jitter, raceCts.Token);

        await Task.WhenAny(trigger, delay);
        var wokenByTrigger = trigger.IsCompletedSuccessfully;

        await raceCts.CancelAsync();
        try { await Task.WhenAll(trigger, delay); }
        catch (OperationCanceledException) { /* expected: we cancelled the loser */ }

        return wokenByTrigger && !ct.IsCancellationRequested;
    }

    private async Task TryAutoSync(bool forced)
    {
        using var scope = services.CreateScope();
        var db   = scope.ServiceProvider.GetRequiredService<Database>();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();

        if (!forced && db.GetSetting("auto_sync", "false") != "true") return;

        // Never forced past setup — there is no Plex server to sync from yet.
        if (!db.IsSetupComplete())
        {
            if (forced) registry.RecordRun(SyncTaskId, DateTime.UtcNow, TimeSpan.Zero, "skipped: setup not complete");
            return;
        }

        if (!forced)
        {
            var lastSyncStr = db.GetSetting("last_auto_sync_at", "");
            if (!string.IsNullOrEmpty(lastSyncStr) &&
                long.TryParse(lastSyncStr, out var lastUnix))
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastUnix;
                if (age < (long)SyncInterval.TotalSeconds) return;
            }
        }

        log.LogInformation("AutoSync: starting {Kind} Plex sync", forced ? "manual" : "scheduled");

        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        registry.MarkRunning(SyncTaskId, true);

        var started = await sync.StartAsync();
        sw.Stop();

        if (started)
        {
            db.SetSetting("last_auto_sync_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            registry.RecordRun(SyncTaskId, startedAt, sw.Elapsed, "sync started");
        }
        else
        {
            log.LogInformation("AutoSync: sync already in progress, skipping");
            registry.RecordRun(SyncTaskId, startedAt, sw.Elapsed, "skipped: a sync was already running");
        }
    }
}
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build src/Themearr.API`
Expected: Build succeeded, 0 errors. (`TaskRegistry` is resolved by DI in Task 9; the build only needs the type to exist.)

- [ ] **Step 3: Run the full suite to confirm nothing regressed**

Run: `dotnet test`
Expected: PASS — all existing tests plus Task 1's 6 still pass.

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.API/Services/AutoSyncService.cs
git commit -m "feat(system): report sync runs to TaskRegistry and support Run now"
```

---

### Task 3: Record unresolved paths during sync

`FetchMoviesAsync` skips unresolved movies with `continue`, so they never reach the `movies` table. Without this task, `LibraryPathsCheck` has nothing to count.

**Files:**
- Modify: `src/Themearr.API/Services/PlexService.cs:212-273` (`FetchMoviesAsync`)

**Interfaces:**
- Consumes: `Database.SetSetting` (existing)
- Produces: settings keys `last_sync_unresolved_count` (integer as string) and `last_sync_unresolved_sample` (a Plex-reported file path), read by `LibraryPathsCheck` in Task 5

- [ ] **Step 1: Add the counters**

In `src/Themearr.API/Services/PlexService.cs`, inside `FetchMoviesAsync`, find this block near the top of the method:

```csharp
        var servers   = db.GetPlexServers();
        var libMap    = db.GetSelectedLibraries();
        var result    = new List<MovieRecord>();
        var seen      = new HashSet<string>();
```

Replace it with:

```csharp
        var servers   = db.GetPlexServers();
        var libMap    = db.GetSelectedLibraries();
        var result    = new List<MovieRecord>();
        var seen      = new HashSet<string>();

        // Movies skipped because no local folder could be resolved. Recorded into
        // settings at the end so LibraryPathsCheck can warn about a broken Path
        // Mapping — these movies never enter the DB, so they cannot be counted later.
        var unresolvedCount  = 0;
        var unresolvedSample = "";
```

- [ ] **Step 2: Increment on each skip**

Find this line (currently line 265):

```csharp
                    if (string.IsNullOrEmpty(folder)) { logFn?.Invoke($"Skipping {title} — unresolved path: {filePath}  (add a Path Mapping from this path's folder to where it's mounted in Themearr)"); continue; }
```

Replace it with:

```csharp
                    if (string.IsNullOrEmpty(folder))
                    {
                        unresolvedCount++;
                        if (unresolvedSample.Length == 0) unresolvedSample = filePath;
                        logFn?.Invoke($"Skipping {title} — unresolved path: {filePath}  (add a Path Mapping from this path's folder to where it's mounted in Themearr)");
                        continue;
                    }
```

- [ ] **Step 3: Persist before returning**

Find the end of the method:

```csharp
            }
        }
        return result;
    }
```

Replace it with:

```csharp
            }
        }

        // Overwritten every sync, so fixing a mapping clears the health warning
        // on the next run.
        db.SetSetting("last_sync_unresolved_count",  unresolvedCount.ToString());
        db.SetSetting("last_sync_unresolved_sample", unresolvedSample);

        return result;
    }
```

- [ ] **Step 4: Verify it builds and nothing regressed**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/PlexService.cs
git commit -m "feat(sync): record unresolved-path count and sample for health checks"
```

---

### Task 4: Health DTO and mapper

Reshapes the framework's `HealthReport` into Radarr's health DTO.

**Files:**
- Create: `src/Themearr.API/Services/Health/HealthDto.cs`
- Test: `tests/Themearr.API.Tests/HealthDtoTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport`
- Produces:
  - `record HealthItem(string Source, string Type, string Message, string? WikiUrl)`
  - `record HealthResponse(string Status, IReadOnlyList<HealthItem> Checks)`
  - `HealthDto.MapType(HealthStatus) -> string`
  - `HealthDto.From(HealthReport) -> HealthResponse`
  - `HealthDto.WikiUrlFor(string source) -> string?`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/HealthDtoTests.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class HealthDtoTests
{
    private static HealthReport Report(params (string Key, HealthStatus Status, string Desc)[] entries)
    {
        var dict = entries.ToDictionary(
            e => e.Key,
            e => new HealthReportEntry(e.Status, e.Desc, TimeSpan.Zero, exception: null, data: null));
        return new HealthReport(dict, TimeSpan.Zero);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy,   "ok")]
    [InlineData(HealthStatus.Degraded,  "warning")]
    [InlineData(HealthStatus.Unhealthy, "error")]
    public void MapType_maps_each_status_to_the_arr_type(HealthStatus status, string expected)
    {
        Assert.Equal(expected, HealthDto.MapType(status));
    }

    [Fact]
    public void Overall_status_is_the_worst_child()
    {
        var report = Report(
            ("a", HealthStatus.Healthy,  "fine"),
            ("b", HealthStatus.Degraded, "meh"),
            ("c", HealthStatus.Unhealthy, "broken"));

        Assert.Equal("error", HealthDto.From(report).Status);
    }

    [Fact]
    public void Healthy_entries_are_omitted_from_the_list()
    {
        var report = Report(
            ("a", HealthStatus.Healthy,  "fine"),
            ("b", HealthStatus.Degraded, "meh"));

        var response = HealthDto.From(report);

        var item = Assert.Single(response.Checks);
        Assert.Equal("b", item.Source);
        Assert.Equal("warning", item.Type);
        Assert.Equal("meh", item.Message);
    }

    [Fact]
    public void All_healthy_yields_ok_and_an_empty_list()
    {
        var response = HealthDto.From(Report(("a", HealthStatus.Healthy, "fine")));

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Checks);
    }

    [Fact]
    public void Known_sources_carry_a_wiki_link_and_unknown_ones_do_not()
    {
        var report = Report(
            ("libraryPaths", HealthStatus.Unhealthy, "bad path"),
            ("autoDownload", HealthStatus.Unhealthy, "stalled"));

        var response = HealthDto.From(report);

        var paths = response.Checks.Single(c => c.Source == "libraryPaths");
        Assert.NotNull(paths.WikiUrl);
        Assert.Contains("library-paths", paths.WikiUrl);

        Assert.Null(response.Checks.Single(c => c.Source == "autoDownload").WikiUrl);
    }

    [Fact]
    public void A_check_with_no_description_still_produces_a_message()
    {
        var report = Report(("a", HealthStatus.Unhealthy, null!));

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(HealthDto.From(report).Checks).Message));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~HealthDtoTests`
Expected: FAIL — `The type or namespace name 'Health' does not exist in the namespace 'Themearr.API.Services'`.

- [ ] **Step 3: Write the implementation**

Create `src/Themearr.API/Services/Health/HealthDto.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Themearr.API.Services.Health;

/// <summary>One health problem, shaped like Radarr's health API so the UI feels familiar.</summary>
public sealed record HealthItem(string Source, string Type, string Message, string? WikiUrl);

/// <summary>Overall status plus every non-healthy check.</summary>
public sealed record HealthResponse(string Status, IReadOnlyList<HealthItem> Checks);

public static class HealthDto
{
    private const string ReadmeBase = "https://github.com/Themearr/themearr#";

    // The README already documents the fix for these; a health message that links
    // straight to it is the support reply we would otherwise write by hand.
    private static readonly Dictionary<string, string> WikiAnchors = new(StringComparer.Ordinal)
    {
        ["libraryPaths"] = ReadmeBase + "library-paths--path-mappings",
        ["rapidapi"]     = ReadmeBase + "downloads-require-a-rapidapi-key",
    };

    public static string? WikiUrlFor(string source) => WikiAnchors.GetValueOrDefault(source);

    public static string MapType(HealthStatus status) => status switch
    {
        HealthStatus.Healthy  => "ok",
        HealthStatus.Degraded => "warning",
        _                     => "error",
    };

    /// <summary>
    /// Only non-healthy entries are listed, matching arr behaviour: the health page
    /// is a problem list, not an inventory. Overall status is already the worst child.
    /// </summary>
    public static HealthResponse From(HealthReport report)
    {
        var checks = report.Entries
            .Where(e => e.Value.Status != HealthStatus.Healthy)
            .Select(e => new HealthItem(
                e.Key,
                MapType(e.Value.Status),
                string.IsNullOrWhiteSpace(e.Value.Description) ? "Check failed" : e.Value.Description,
                WikiUrlFor(e.Key)))
            .OrderBy(c => c.Source, StringComparer.Ordinal)
            .ToList();

        return new HealthResponse(MapType(report.Status), checks);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~HealthDtoTests`
Expected: PASS — 8 tests passed (the `[Theory]` contributes 3).

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/Health/HealthDto.cs tests/Themearr.API.Tests/HealthDtoTests.cs
git commit -m "feat(system): map HealthReport to the arr-shaped health DTO"
```

---

### Task 5: `LibraryPathsCheck`

The highest-value check: it catches the misconfiguration that causes silent download failure.

**Files:**
- Create: `src/Themearr.API/Services/Health/LibraryPathsCheck.cs`
- Test: `tests/Themearr.API.Tests/LibraryPathsCheckTests.cs`

**Interfaces:**
- Consumes: `Database.IsSetupComplete()`, `Database.GetLibraryPaths()`, `Database.GetSetting(key, default)`, `ThemeFiles.IsDirectoryWritable(string)`, `TempDir` (test helper)
- Produces: `LibraryPathsCheck : IHealthCheck` registered under the name `"libraryPaths"`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/LibraryPathsCheckTests.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class LibraryPathsCheckTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    private static Task<HealthCheckResult> Run(Database db) =>
        new LibraryPathsCheck(db).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task Before_setup_completes_it_reports_healthy()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetLibraryPaths(["/definitely/does/not/exist"]);

        Assert.Equal(HealthStatus.Healthy, (await Run(db)).Status);
    }

    [Fact]
    public async Task No_configured_paths_is_an_error()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.MarkSetupComplete();

        var result = await Run(db);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("No library paths", result.Description);
    }

    [Fact]
    public async Task A_missing_path_is_an_error_naming_the_path()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.MarkSetupComplete();
        var missing = Path.Combine(dir.Path, "gone");
        db.SetLibraryPaths([missing]);

        var result = await Run(db);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(missing, result.Description);
    }

    [Fact]
    public async Task Unresolved_movies_from_the_last_sync_are_a_warning_with_a_sample()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.MarkSetupComplete();
        db.SetLibraryPaths([dir.Path]);
        db.SetSetting("last_sync_unresolved_count", "142");
        db.SetSetting("last_sync_unresolved_sample", @"P:\Movies\Heat (1995)\heat.mkv");

        var result = await Run(db);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("142", result.Description);
        Assert.Contains("Path Mappings", result.Description);
        Assert.Contains(@"P:\Movies", result.Description);
    }

    [Fact]
    public async Task A_good_writable_path_with_no_unresolved_movies_is_healthy()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.MarkSetupComplete();
        db.SetLibraryPaths([dir.Path]);
        db.SetSetting("last_sync_unresolved_count", "0");

        Assert.Equal(HealthStatus.Healthy, (await Run(db)).Status);
    }
}
```

> **Note on the read-only case:** the spec lists "path is not writable" as an error.
> It is not unit-tested here because making a directory genuinely unwritable is
> unreliable across platforms and fails outright when tests run as root (the normal
> case in CI containers). The behaviour is covered by `ThemeFilesWritableTests`,
> which already tests `ThemeFiles.IsDirectoryWritable` directly. Do not add a
> `chmod`-based test — it will pass locally and silently no-op in CI.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~LibraryPathsCheckTests`
Expected: FAIL — `The type or namespace name 'LibraryPathsCheck' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Themearr.API/Services/Health/LibraryPathsCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Catches the misconfiguration that silently breaks every download: a library path
/// that is missing, read-only, or unreachable from the paths Plex reports.
/// </summary>
public sealed class LibraryPathsCheck(Database db) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Before setup there is nothing configured yet; a fresh install is not broken.
        if (!db.IsSetupComplete())
            return Task.FromResult(HealthCheckResult.Healthy("Setup not complete"));

        var paths = db.GetLibraryPaths();
        if (paths.Count == 0)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No library paths are configured — Themearr has nowhere to write theme.mp3."));

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Library path {path} does not exist. Check the mount is present inside Themearr."));

            if (!ThemeFiles.IsDirectoryWritable(path))
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Library path {path} is not writable — every download will fail silently. " +
                    "Check the mount is not read-only and that the themearr user can write to it."));
        }

        var unresolved = int.TryParse(db.GetSetting("last_sync_unresolved_count", "0"), out var n) ? n : 0;
        if (unresolved > 0)
        {
            var sample  = db.GetSetting("last_sync_unresolved_sample", "");
            var message = $"{unresolved} movies could not be resolved to a local path — check Path Mappings.";
            if (!string.IsNullOrEmpty(sample)) message += $" Example: {sample}";
            return Task.FromResult(HealthCheckResult.Degraded(message));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"{paths.Count} library path(s) present and writable"));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LibraryPathsCheckTests`
Expected: PASS — 5 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/Health/LibraryPathsCheck.cs tests/Themearr.API.Tests/LibraryPathsCheckTests.cs
git commit -m "feat(system): add library paths health check"
```

---

### Task 6: `RapidApiCheck` and `DownloadWorkerCheck`

Both are passive — they read state the app already holds. Grouped because neither needs its own test fixture beyond a database.

**Files:**
- Create: `src/Themearr.API/Services/Health/RapidApiCheck.cs`
- Create: `src/Themearr.API/Services/Health/DownloadWorkerCheck.cs`
- Modify: `src/Themearr.API/Services/AutoDownloadService.cs` (add two public accessors)
- Test: `tests/Themearr.API.Tests/PassiveHealthCheckTests.cs`

**Interfaces:**
- Consumes: `IThemeAudioProvider.CheckConfiguration() -> string?` (null means configured), `DownloadService.IsQuotaCoolingDown(out DateTime untilUtc) -> bool`, `Database.GetSetting`
- Produces:
  - `RapidApiCheck : IHealthCheck` registered as `"rapidapi"`
  - `DownloadWorkerCheck : IHealthCheck` registered as `"autoDownload"`
  - `IDownloadWorkerStatus` with `DateTime? LastTickAt` and `string LastTickResult`, implemented by `AutoDownloadService`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/PassiveHealthCheckTests.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class PassiveHealthCheckTests
{
    private sealed class FakeProvider(string? configurationError) : IThemeAudioProvider
    {
        public string? CheckConfiguration() => configurationError;

        public Task<Stream> FetchAsync(string videoId, CancellationToken ct) =>
            throw new NotSupportedException("not used by health checks");
    }

    private sealed class FakeWorker(DateTime? lastTickAt, string lastResult) : IDownloadWorkerStatus
    {
        public DateTime? LastTickAt     => lastTickAt;
        public string    LastTickResult => lastResult;
    }

    private static Database NewDb(TempDir dir, bool setupComplete)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        if (setupComplete) db.MarkSetupComplete();
        return db;
    }

    private static Task<HealthCheckResult> Run(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    // ── RapidApiCheck ────────────────────────────────────────────────────────

    [Fact]
    public async Task RapidApi_before_setup_is_healthy_even_with_no_key()
    {
        using var dir = new TempDir();
        var check = new RapidApiCheck(NewDb(dir, setupComplete: false), new FakeProvider("no key"));

        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task RapidApi_without_a_key_is_an_error_carrying_the_reason()
    {
        using var dir = new TempDir();
        var check = new RapidApiCheck(NewDb(dir, setupComplete: true), new FakeProvider("RapidAPI key is not set"));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("RapidAPI key is not set", result.Description);
    }

    [Fact]
    public async Task RapidApi_configured_is_healthy()
    {
        using var dir = new TempDir();
        var check = new RapidApiCheck(NewDb(dir, setupComplete: true), new FakeProvider(null));

        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);
    }

    // ── DownloadWorkerCheck ──────────────────────────────────────────────────

    [Fact]
    public async Task Worker_is_healthy_when_auto_download_is_off_despite_a_stale_tick()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, setupComplete: true);
        db.SetSetting("auto_download", "false");
        var worker = new FakeWorker(DateTime.UtcNow.AddHours(-3), "skipped: auto_download is off");

        Assert.Equal(HealthStatus.Healthy, (await Run(new DownloadWorkerCheck(db, worker))).Status);
    }

    [Fact]
    public async Task Worker_with_a_stale_tick_while_enabled_is_an_error()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, setupComplete: true);
        db.SetSetting("auto_download", "true");
        var worker = new FakeWorker(DateTime.UtcNow.AddMinutes(-30), "started 'Heat' -> abc123");

        var result = await Run(new DownloadWorkerCheck(db, worker));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("30", result.Description);
    }

    [Fact]
    public async Task Worker_that_ticked_recently_is_healthy()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, setupComplete: true);
        db.SetSetting("auto_download", "true");
        var worker = new FakeWorker(DateTime.UtcNow.AddSeconds(-20), "skipped: no pending movies");

        Assert.Equal(HealthStatus.Healthy, (await Run(new DownloadWorkerCheck(db, worker))).Status);
    }

    [Fact]
    public async Task Worker_that_has_never_ticked_is_healthy_because_it_is_still_warming_up()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, setupComplete: true);
        db.SetSetting("auto_download", "true");

        Assert.Equal(HealthStatus.Healthy,
            (await Run(new DownloadWorkerCheck(db, new FakeWorker(null, "never run")))).Status);
    }
}
```

> **Before writing the fake:** open `src/Themearr.API/Services/IThemeAudioProvider.cs`
> and match `FakeProvider` to the real interface exactly — implement every member
> it declares. The `FetchAsync` signature above is a best guess; if it differs,
> use the real one and keep the `NotSupportedException` body, since health checks
> never call it.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PassiveHealthCheckTests`
Expected: FAIL — `RapidApiCheck`, `DownloadWorkerCheck` and `IDownloadWorkerStatus` do not exist.

- [ ] **Step 3: Add the worker status interface and accessors**

Create `src/Themearr.API/Services/Health/IDownloadWorkerStatus.cs`:

```csharp
namespace Themearr.API.Services.Health;

/// <summary>
/// The slice of <see cref="AutoDownloadService"/> that the health check needs.
/// Keeping it narrow means the check can be unit-tested without constructing a
/// BackgroundService, a service provider, or a timer.
/// </summary>
public interface IDownloadWorkerStatus
{
    DateTime? LastTickAt     { get; }
    string    LastTickResult { get; }
}
```

In `src/Themearr.API/Services/AutoDownloadService.cs`, change the class declaration from:

```csharp
public class AutoDownloadService(
    IServiceProvider services,
    DownloadService  download,
    IThemeAudioProvider provider,
    ILogger<AutoDownloadService> log) : BackgroundService
{
```

to:

```csharp
public class AutoDownloadService(
    IServiceProvider services,
    DownloadService  download,
    IThemeAudioProvider provider,
    ILogger<AutoDownloadService> log) : BackgroundService, Health.IDownloadWorkerStatus
{
```

Then, immediately after the diagnostic fields block (the four `private` fields ending with `private int _downloadsStarted;`), add:

```csharp
    // Exposed for DownloadWorkerCheck: "is the worker alive, and what did it last do".
    public DateTime? LastTickAt     => _lastTickAt;
    public string    LastTickResult => _lastTickResult;
```

- [ ] **Step 4: Write the two checks**

Create `src/Themearr.API/Services/Health/RapidApiCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Passive by design. youtube-mp36 has no free quota endpoint, so actively probing
/// "is RapidAPI healthy" would spend a request off the free tier — quota taken
/// straight from downloads. This reads only state Themearr already holds.
/// </summary>
public sealed class RapidApiCheck(Database db, IThemeAudioProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.IsSetupComplete())
            return Task.FromResult(HealthCheckResult.Healthy("Setup not complete"));

        if (provider.CheckConfiguration() is { } notReady)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Theme downloads are disabled: {notReady}"));

        return Task.FromResult(HealthCheckResult.Healthy("RapidAPI is configured"));
    }
}
```

Create `src/Themearr.API/Services/Health/DownloadWorkerCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Detects a wedged auto-download worker. The worker ticks every 30 seconds, so a
/// gap of minutes means it is stuck rather than idle.
/// </summary>
public sealed class DownloadWorkerCheck(Database db, IDownloadWorkerStatus worker) : IHealthCheck
{
    private static readonly TimeSpan MaxTickAge = TimeSpan.FromMinutes(5);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // A disabled worker does not tick. That is a setting, not a fault.
        if (db.GetSetting("auto_download", "false") != "true")
            return Task.FromResult(HealthCheckResult.Healthy("Auto-download is off"));

        if (worker.LastTickAt is not { } last)
            return Task.FromResult(HealthCheckResult.Healthy("Auto-download worker is starting up"));

        var age = DateTime.UtcNow - last;
        if (age > MaxTickAge)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"The auto-download worker has not run for {(int)age.TotalMinutes} minutes " +
                $"(it should run every 30 seconds). Last result: {worker.LastTickResult}"));

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Last tick {(int)age.TotalSeconds}s ago: {worker.LastTickResult}"));
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PassiveHealthCheckTests`
Expected: PASS — 7 tests passed.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Services/Health/RapidApiCheck.cs \
        src/Themearr.API/Services/Health/DownloadWorkerCheck.cs \
        src/Themearr.API/Services/Health/IDownloadWorkerStatus.cs \
        src/Themearr.API/Services/AutoDownloadService.cs \
        tests/Themearr.API.Tests/PassiveHealthCheckTests.cs
git commit -m "feat(system): add passive RapidAPI and download-worker health checks"
```

---

### Task 7: Quota cooldown warning

Separated from Task 6 because it needs a `DownloadService` instance, which has a wider constructor.

**Files:**
- Modify: `src/Themearr.API/Services/Health/RapidApiCheck.cs`
- Modify: `tests/Themearr.API.Tests/PassiveHealthCheckTests.cs`

**Interfaces:**
- Consumes: `DownloadService.IsQuotaCoolingDown(out DateTime untilUtc) -> bool`
- Produces: `RapidApiCheck(Database, IThemeAudioProvider, IQuotaStatus)` — a **third constructor parameter**, so Task 6's tests must be updated in the same commit

- [ ] **Step 1: Add a narrow quota interface**

Create `src/Themearr.API/Services/Health/IQuotaStatus.cs`:

```csharp
namespace Themearr.API.Services.Health;

/// <summary>
/// The slice of <see cref="DownloadService"/> the health check needs. Narrow so the
/// check can be tested without constructing the full download pipeline.
/// </summary>
public interface IQuotaStatus
{
    bool IsQuotaCoolingDown(out DateTime untilUtc);
}
```

In `src/Themearr.API/Services/DownloadService.cs`, add `Health.IQuotaStatus` to the class's base list. The existing declaration is a primary constructor ending in `)` followed by `{`; append the interface after the closing parenthesis, for example:

```csharp
public class DownloadService(
    /* existing parameters unchanged */) : Health.IQuotaStatus
```

The existing `public bool IsQuotaCoolingDown(out DateTime untilUtc)` already satisfies it — no new method body is needed.

- [ ] **Step 2: Write the failing test**

In `tests/Themearr.API.Tests/PassiveHealthCheckTests.cs`, add this fake alongside the others:

```csharp
    private sealed class FakeQuota(DateTime? coolingUntil) : IQuotaStatus
    {
        public bool IsQuotaCoolingDown(out DateTime untilUtc)
        {
            untilUtc = coolingUntil ?? DateTime.MinValue;
            return coolingUntil.HasValue;
        }
    }
```

Update the three existing `new RapidApiCheck(...)` calls to pass a third argument `new FakeQuota(null)`, then add:

```csharp
    [Fact]
    public async Task RapidApi_quota_cooldown_is_a_warning_naming_the_time()
    {
        using var dir = new TempDir();
        var until = new DateTime(2026, 7, 19, 14, 32, 0, DateTimeKind.Utc);
        var check = new RapidApiCheck(
            NewDb(dir, setupComplete: true), new FakeProvider(null), new FakeQuota(until));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("quota", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14:32", result.Description);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PassiveHealthCheckTests`
Expected: FAIL — `RapidApiCheck` does not take 3 arguments.

- [ ] **Step 4: Update the check**

In `src/Themearr.API/Services/Health/RapidApiCheck.cs`, change the class declaration to:

```csharp
public sealed class RapidApiCheck(Database db, IThemeAudioProvider provider, IQuotaStatus quota) : IHealthCheck
```

and insert this immediately before the final `return Task.FromResult(HealthCheckResult.Healthy("RapidAPI is configured"));`:

```csharp
        // A 429 sets a cooldown. Report it rather than probing, which would cost quota.
        if (quota.IsQuotaCoolingDown(out var until))
            return Task.FromResult(HealthCheckResult.Degraded(
                $"RapidAPI quota is exhausted — downloads are paused until {until:u}"));
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PassiveHealthCheckTests`
Expected: PASS — 8 tests passed.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Services/Health/IQuotaStatus.cs \
        src/Themearr.API/Services/Health/RapidApiCheck.cs \
        src/Themearr.API/Services/DownloadService.cs \
        tests/Themearr.API.Tests/PassiveHealthCheckTests.cs
git commit -m "feat(system): warn when RapidAPI quota is cooling down"
```

---

### Task 8: `PlexReachableCheck`

The only check that touches the network.

**Files:**
- Create: `src/Themearr.API/Services/Health/PlexReachableCheck.cs`
- Test: `tests/Themearr.API.Tests/PlexReachableCheckTests.cs`

**Interfaces:**
- Consumes: `Database.IsSetupComplete()`, `Database.GetPlexServersDict() -> Dictionary<string,(string Url, string Token)>`, `IHttpClientFactory`
- Produces: `PlexReachableCheck : IHealthCheck` registered as `"plex"`, and `PlexReachableCheck.ClientName` (const `"plex-health"`) — the named `HttpClient` Task 9 must configure with a 3-second timeout

- [ ] **Step 1: Write the failing tests**

Create `tests/Themearr.API.Tests/PlexReachableCheckTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class PlexReachableCheckTests
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

    private static Database NewDb(TempDir dir, bool withServer)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();
        if (withServer)
        {
            db.SetPlexServers([new Dictionary<string, object?>
            {
                ["id"]    = "srv1",
                ["name"]  = "Tower",
                ["url"]   = "http://plex.local:32400",
                ["token"] = "secret-token-value",
            }]);
        }
        return db;
    }

    private static Task<HealthCheckResult> Run(Database db, HttpMessageHandler handler) =>
        new PlexReachableCheck(db, new StubFactory(handler))
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task No_configured_server_is_healthy()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(HealthStatus.Healthy, (await Run(NewDb(dir, withServer: false), handler)).Status);
    }

    [Fact]
    public async Task A_reachable_server_is_healthy()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(HealthStatus.Healthy, (await Run(NewDb(dir, withServer: true), handler)).Status);
    }

    [Fact]
    public async Task A_401_reports_a_rejected_token()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await Run(NewDb(dir, withServer: true), handler);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("401", result.Description);
    }

    [Fact]
    public async Task A_timeout_reports_no_response()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));

        var result = await Run(NewDb(dir, withServer: true), handler);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("did not respond", result.Description);
    }

    [Fact]
    public async Task A_connection_failure_reports_unreachable()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused to http://plex.local:32400"));

        var result = await Run(NewDb(dir, withServer: true), handler);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unreachable", result.Description);
    }

    [Fact]
    public async Task The_token_never_appears_in_any_message_or_in_the_request_url()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new HttpRequestException("boom secret-token-value"));

        var result = await Run(NewDb(dir, withServer: true), handler);

        Assert.DoesNotContain("secret-token-value", result.Description);
        Assert.DoesNotContain("secret-token-value", handler.LastRequest?.RequestUri?.ToString() ?? "");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~PlexReachableCheckTests`
Expected: FAIL — `The type or namespace name 'PlexReachableCheck' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Themearr.API/Services/Health/PlexReachableCheck.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Pings the user's own Plex server. The token travels in a header, never the query
/// string, and no exception text is ever surfaced — a raw HttpRequestException can
/// echo the request and we will not risk leaking credentials into the UI.
/// </summary>
public sealed class PlexReachableCheck(Database db, IHttpClientFactory factory) : IHealthCheck
{
    /// <summary>Named client, configured in Program.cs with a 3-second timeout.</summary>
    public const string ClientName = "plex-health";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.IsSetupComplete())
            return HealthCheckResult.Healthy("Setup not complete");

        var servers = db.GetPlexServersDict();
        if (servers.Count == 0)
            return HealthCheckResult.Healthy("No Plex server configured");

        var (url, token) = servers.First().Value;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
            return HealthCheckResult.Healthy("No Plex server configured");

        var http = factory.CreateClient(ClientName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/identity");
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);

            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return HealthCheckResult.Unhealthy(
                    "Plex rejected the stored token (401). Sign in to Plex again in Settings.");

            if (!response.IsSuccessStatusCode)
                return HealthCheckResult.Unhealthy(
                    $"The Plex server returned HTTP {(int)response.StatusCode}.");

            return HealthCheckResult.Healthy("Plex server is reachable");
        }
        catch (TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("The Plex server did not respond within 3 seconds.");
        }
        catch (HttpRequestException)
        {
            return HealthCheckResult.Unhealthy(
                "The Plex server is unreachable. Check it is running and the URL in Settings is correct.");
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~PlexReachableCheckTests`
Expected: PASS — 6 tests passed.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/Health/PlexReachableCheck.cs tests/Themearr.API.Tests/PlexReachableCheckTests.cs
git commit -m "feat(system): add Plex reachability health check"
```

---

### Task 9: Cache, controller and registration

Wires everything into the app and exposes both endpoints.

**Files:**
- Create: `src/Themearr.API/Services/Health/HealthCache.cs`
- Create: `src/Themearr.API/Controllers/SystemController.cs`
- Modify: `src/Themearr.API/Program.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–8
- Produces: `GET /api/system/health`, `GET /api/system/tasks`, `POST /api/system/tasks/{id}/run`, `GET /health`

- [ ] **Step 1: Write the health cache**

Create `src/Themearr.API/Services/Health/HealthCache.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Themearr.API.Services.Health;

/// <summary>
/// Caches the health report server-side for 60 seconds. Without this, the sidebar
/// badge would ping the user's Plex server once per open browser tab per poll —
/// three tabs left open overnight would be thousands of probes. Caching here (not
/// in the client) collapses N tabs into one probe. Mirrors UpdateService's cache.
/// </summary>
public sealed class HealthCache(HealthCheckService health)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private HealthResponse? _cached;
    private DateTime _expiresAt = DateTime.MinValue;

    public async Task<HealthResponse> GetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTime.UtcNow < _expiresAt) return _cached;

            var report = await health.CheckHealthAsync(ct);
            _cached    = HealthDto.From(report);
            _expiresAt = DateTime.UtcNow.Add(Ttl);
            return _cached;
        }
        finally { _lock.Release(); }
    }
}
```

- [ ] **Step 2: Write the controller**

Create `src/Themearr.API/Controllers/SystemController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;
using Themearr.API.Services.Health;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController(HealthCache health, TaskRegistry tasks) : ControllerBase
{
    [HttpGet("health")]
    public Task<HealthResponse> Health(CancellationToken ct) => health.GetAsync(ct);

    [HttpGet("tasks")]
    public IReadOnlyList<TaskState> Tasks() => tasks.Snapshot();

    [HttpPost("tasks/{id}/run")]
    public IActionResult Run(string id)
    {
        if (!tasks.Exists(id))
            return NotFound(new { detail = "Unknown task" });

        var state = tasks.Snapshot().FirstOrDefault(t => t.Id == id);
        if (state?.IsRunning == true)
            return Conflict(new { detail = "That task is already running" });

        // Trigger() returning false means a run is already queued, which is the same
        // outcome the caller wanted — report success either way.
        tasks.Trigger(id);
        return Accepted(new { started = true });
    }
}
```

- [ ] **Step 3: Register everything in Program.cs**

In `src/Themearr.API/Program.cs`, immediately after the line:

```csharp
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoDownloadService>());
```

add:

```csharp

// ── System page: health checks + scheduled tasks ──────────────────────────────
builder.Services.AddSingleton<TaskRegistry>();
builder.Services.AddSingleton<Themearr.API.Services.Health.HealthCache>();
// The download worker's status is read by DownloadWorkerCheck through a narrow
// interface; resolve it from the same singleton the hosted service uses.
builder.Services.AddSingleton<Themearr.API.Services.Health.IDownloadWorkerStatus>(
    sp => sp.GetRequiredService<AutoDownloadService>());
builder.Services.AddSingleton<Themearr.API.Services.Health.IQuotaStatus>(
    sp => sp.GetRequiredService<DownloadService>());
// A short timeout matters: an unreachable Plex server is the expected case here,
// and without it the whole health page waits on a TCP hang.
builder.Services.AddHttpClient(Themearr.API.Services.Health.PlexReachableCheck.ClientName,
    c => c.Timeout = TimeSpan.FromSeconds(3));

builder.Services.AddHealthChecks()
    .AddCheck<Themearr.API.Services.Health.LibraryPathsCheck>("libraryPaths")
    .AddCheck<Themearr.API.Services.Health.PlexReachableCheck>("plex")
    .AddCheck<Themearr.API.Services.Health.RapidApiCheck>("rapidapi")
    .AddCheck<Themearr.API.Services.Health.DownloadWorkerCheck>("autoDownload");
```

- [ ] **Step 4: Map the monitoring endpoint**

In `src/Themearr.API/Program.cs`, find:

```csharp
app.MapControllers();
```

and insert immediately **before** it:

```csharp
// Unauthenticated monitoring endpoint for Uptime Kuma / Gatus. Deliberately
// detail-free: a single status word, no check names, no messages, no version.
// ApiAuthMiddleware guards only /api/*, so this needs no allowlist entry.
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { status = report.Status.ToString() });
    }
});

```

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build src/Themearr.API && dotnet test`
Expected: Build succeeded; all tests pass.

- [ ] **Step 6: Smoke-test both endpoints manually**

```bash
THEMEARR_AUTH_TOKEN=dev-token-at-least-16-chars DB_PATH=/tmp/themearr-smoke.db \
  dotnet run --project src/Themearr.API &
sleep 8
curl -s http://localhost:5000/health
curl -s -H "Authorization: Bearer dev-token-at-least-16-chars" http://localhost:5000/api/system/health
curl -s -H "Authorization: Bearer dev-token-at-least-16-chars" http://localhost:5000/api/system/tasks
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5000/api/system/health
kill %1
```

Expected:
- `/health` → `{"status":"Healthy"}`
- `/api/system/health` → `{"status":"ok","checks":[]}` (a fresh DB has setup incomplete, so every check is healthy)
- `/api/system/tasks` → `[]` initially — `AutoSyncService` registers its task at the start of `ExecuteAsync`, which the host runs at startup, so after ~1 second it returns the `syncLibrary` row
- the unauthenticated `/api/system/health` call → `401`

- [ ] **Step 7: Commit**

```bash
git add src/Themearr.API/Services/Health/HealthCache.cs \
        src/Themearr.API/Controllers/SystemController.cs \
        src/Themearr.API/Program.cs
git commit -m "feat(system): expose health and tasks endpoints plus /health for monitoring"
```

---

### Task 10: Frontend types and API client

**Files:**
- Modify: `src/Themearr.Web/src/lib/types.ts`
- Modify: `src/Themearr.Web/src/lib/api.ts`

**Interfaces:**
- Consumes: the endpoints from Task 9
- Produces: `HealthItem`, `HealthResponse`, `SystemTask` types and `systemApi.{health,tasks,runTask}`

- [ ] **Step 1: Add the types**

Append to `src/Themearr.Web/src/lib/types.ts`:

```ts
export type HealthType = 'ok' | 'warning' | 'error'

export interface HealthItem {
  source: string
  type: HealthType
  message: string
  wikiUrl: string | null
}

export interface HealthResponse {
  status: HealthType
  checks: HealthItem[]
}

export interface SystemTask {
  id: string
  name: string
  /** Serialized TimeSpan, e.g. "24:00:00". */
  interval: string
  lastRunUtc: string | null
  lastDurationMs: number | null
  lastResult: string | null
  nextRunUtc: string | null
  isRunning: boolean
}
```

- [ ] **Step 2: Add the API client**

In `src/Themearr.Web/src/lib/api.ts`, add `HealthResponse` and `SystemTask` to the existing `import type { ... } from './types'` list, then append at the end of the file:

```ts
// ── System (health + tasks) ───────────────────────────────────────────────────

export const systemApi = {
  health: () => request<HealthResponse>('/api/system/health'),
  tasks:  () => request<SystemTask[]>('/api/system/tasks'),
  runTask: (id: string) =>
    request<{ started: boolean }>(`/api/system/tasks/${encodeURIComponent(id)}/run`, {
      method: 'POST',
    }),
}
```

- [ ] **Step 3: Typecheck**

Run:
```bash
cd src/Themearr.Web && npx tsc --noEmit
```
Expected: no output (clean).

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.Web/src/lib/types.ts src/Themearr.Web/src/lib/api.ts
git commit -m "feat(web): add system health and tasks API client"
```

---

### Task 11: The System page

**Files:**
- Create: `src/Themearr.Web/src/app/system/page.tsx`
- Modify: `src/Themearr.Web/src/main.tsx`

**Interfaces:**
- Consumes: `systemApi` (Task 10), `AppShell`, `Button`, `Spinner`, `EmptyState` from `@/components/ui`
- Produces: default-exported `SystemPage`, routed at `/system`

- [ ] **Step 1: Write the page**

Create `src/Themearr.Web/src/app/system/page.tsx`:

```tsx
import { useEffect, useState } from 'react'
import { systemApi } from '@/lib/api'
import type { HealthResponse, SystemTask } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, Spinner } from '@/components/ui'

type Tab = 'health' | 'tasks'

const TYPE_STYLES: Record<string, { dot: string; text: string; label: string }> = {
  error:   { dot: '#F04438', text: '#FDA29B', label: 'Error' },
  warning: { dot: '#F79009', text: '#FEC84B', label: 'Warning' },
  ok:      { dot: '#12B76A', text: '#6CE9A6', label: 'OK' },
}

function relative(iso: string | null): string {
  if (!iso) return 'Never'
  const diffMs = Date.now() - new Date(iso).getTime()
  const mins = Math.round(Math.abs(diffMs) / 60000)
  const suffix = diffMs >= 0 ? 'ago' : 'from now'
  if (mins < 1) return 'Just now'
  if (mins < 60) return `${mins}m ${suffix}`
  const hours = Math.round(mins / 60)
  if (hours < 24) return `${hours}h ${suffix}`
  return `${Math.round(hours / 24)}d ${suffix}`
}

export default function SystemPage() {
  const [tab,    setTab]    = useState<Tab>('health')
  const [health, setHealth] = useState<HealthResponse | null>(null)
  const [tasks,  setTasks]  = useState<SystemTask[] | null>(null)
  const [running, setRunning] = useState<string | null>(null)
  const [error,   setError]   = useState('')

  function load() {
    systemApi.health().then(setHealth).catch(() => setHealth({ status: 'error', checks: [] }))
    systemApi.tasks().then(setTasks).catch(() => setTasks([]))
  }

  useEffect(() => { load() }, [])

  // Tasks change on a human timescale; the server caches health for 60s anyway.
  useEffect(() => {
    const id = setInterval(() => {
      systemApi.tasks().then(setTasks).catch(() => null)
    }, 10000)
    return () => clearInterval(id)
  }, [])

  async function runTask(id: string) {
    setRunning(id)
    setError('')
    try {
      await systemApi.runTask(id)
      setTasks(await systemApi.tasks())
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not start that task')
    } finally {
      setRunning(null)
    }
  }

  return (
    <AppShell>
      <div className="mb-6">
        <h1 className="text-xl font-semibold text-[#F9FAFB]">System</h1>
        <p className="mt-1 text-sm text-[#667085]">Health checks and scheduled tasks.</p>
      </div>

      <div className="mb-5 flex gap-1 border-b border-[#1D2939]">
        {(['health', 'tasks'] as Tab[]).map(t => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2 text-sm font-medium capitalize transition-colors ${
              tab === t
                ? 'border-b-2 border-[#BB0000] text-[#F9FAFB]'
                : 'text-[#667085] hover:text-[#D0D5DD]'
            }`}
          >
            {t}
          </button>
        ))}
      </div>

      {error && (
        <p className="mb-4 rounded-lg bg-[#F04438]/10 px-3 py-2 text-sm text-[#FDA29B]">{error}</p>
      )}

      {tab === 'health' && (
        health === null ? <Spinner /> :
        health.checks.length === 0 ? (
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] px-4 py-8 text-center">
            <p className="text-sm font-medium text-[#6CE9A6]">All health checks are passing</p>
            <p className="mt-1 text-xs text-[#667085]">
              Only problems are listed here, so an empty page is good news.
            </p>
          </div>
        ) : (
          <div className="space-y-2">
            {health.checks.map(c => {
              const style = TYPE_STYLES[c.type] ?? TYPE_STYLES.error
              return (
                <div
                  key={c.source}
                  className="flex items-start gap-3 rounded-xl border border-[#1D2939] bg-[#101828] px-4 py-3"
                >
                  <span
                    className="mt-1.5 h-2 w-2 flex-shrink-0 rounded-full"
                    style={{ background: style.dot }}
                  />
                  <div className="min-w-0 flex-1">
                    <p className="text-xs font-semibold uppercase tracking-wide" style={{ color: style.text }}>
                      {style.label} · {c.source}
                    </p>
                    <p className="mt-1 text-sm text-[#D0D5DD]">{c.message}</p>
                    {c.wikiUrl && (
                      <a
                        href={c.wikiUrl}
                        target="_blank"
                        rel="noreferrer"
                        className="mt-1.5 inline-block text-xs text-[#E07777] hover:underline"
                      >
                        How to fix this →
                      </a>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        )
      )}

      {tab === 'tasks' && (
        tasks === null ? <Spinner /> :
        tasks.length === 0 ? (
          <p className="text-sm text-[#667085]">No scheduled tasks are registered yet.</p>
        ) : (
          <div className="overflow-x-auto rounded-xl border border-[#1D2939]">
            <table className="w-full min-w-[640px] text-sm">
              <thead className="bg-[#101828] text-left text-xs uppercase tracking-wide text-[#667085]">
                <tr>
                  <th className="px-4 py-3 font-medium">Name</th>
                  <th className="px-4 py-3 font-medium">Interval</th>
                  <th className="px-4 py-3 font-medium">Last run</th>
                  <th className="px-4 py-3 font-medium">Next run</th>
                  <th className="px-4 py-3 font-medium" />
                </tr>
              </thead>
              <tbody>
                {tasks.map(t => (
                  <tr key={t.id} className="border-t border-[#1D2939]">
                    <td className="px-4 py-3">
                      <p className="text-[#F9FAFB]">{t.name}</p>
                      {t.lastResult && <p className="mt-0.5 text-xs text-[#667085]">{t.lastResult}</p>}
                    </td>
                    <td className="px-4 py-3 text-[#D0D5DD]">{t.interval}</td>
                    <td className="px-4 py-3 text-[#D0D5DD]">{relative(t.lastRunUtc)}</td>
                    <td className="px-4 py-3 text-[#D0D5DD]">{relative(t.nextRunUtc)}</td>
                    <td className="px-4 py-3 text-right">
                      <Button
                        size="sm"
                        variant="secondary"
                        loading={running === t.id}
                        disabled={t.isRunning || running === t.id}
                        onClick={() => runTask(t.id)}
                      >
                        {t.isRunning ? 'Running' : 'Run now'}
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      )}
    </AppShell>
  )
}
```

> **Check `Button` before using it:** open `src/Themearr.Web/src/components/ui/index.tsx`
> and confirm the `variant` values it accepts. `secondary` is assumed above; if the
> component defines a different set, use one that exists rather than adding a variant.

- [ ] **Step 2: Add the route**

In `src/Themearr.Web/src/main.tsx`, add the import alongside the other page imports:

```tsx
import SystemPage from '@/app/system/page'
```

and add this route immediately after the `/settings` route:

```tsx
          <Route path="/system" element={<SystemPage />} />
```

- [ ] **Step 3: Typecheck, lint and build**

Run:
```bash
cd src/Themearr.Web && npx tsc --noEmit && npm run lint && npm run build
```
Expected: typecheck clean, lint clean, build writes to `out/`.

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.Web/src/app/system/page.tsx src/Themearr.Web/src/main.tsx
git commit -m "feat(web): add System page with Health and Tasks tabs"
```

---

### Task 12: Sidebar entry and warning badge

The piece that makes a read-only mount surface without the user going looking for it.

**Files:**
- Modify: `src/Themearr.Web/src/components/layout/Sidebar.tsx`

**Interfaces:**
- Consumes: `systemApi.health()` (Task 10)
- Produces: nothing consumed elsewhere

- [ ] **Step 1: Add the nav entry**

In `src/Themearr.Web/src/components/layout/Sidebar.tsx`, add this object to the end of the `NAV` array, after the `/settings` entry:

```tsx
  {
    href: '/system',
    label: 'System',
    icon: (
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <rect x="2" y="4" width="20" height="14" rx="2" />
        <path d="M8 20h8M12 18v2" />
        <path d="M6 9h4M6 12h7" />
      </svg>
    ),
  },
```

- [ ] **Step 2: Poll health and render the badge**

Add `systemApi` to the existing api import:

```tsx
import { syncApi, versionApi, systemApi } from '@/lib/api'
```

Add this state alongside `syncing`:

```tsx
  const [healthIssues, setHealthIssues] = useState(0)
```

Add this effect after the existing sync-status effect:

```tsx
  // Health badge. The server caches the report for 60s, so this interval costs one
  // real probe per minute no matter how many tabs are open.
  useEffect(() => {
    function check() {
      systemApi.health()
        .then(h => setHealthIssues(h.checks.length))
        .catch(() => null)
    }
    check()
    const id = setInterval(check, 60000)
    return () => clearInterval(id)
  }, [])
```

Inside the `NAV.map` callback, add below the existing `showSyncBadge` line:

```tsx
          const showHealthBadge = label === 'System' && healthIssues > 0
```

and add this immediately after the `{showSyncBadge && <Spinner size={12} className="text-[#F79009]" />}` line:

```tsx
              {showHealthBadge && (
                <span className="rounded-full bg-[#F04438] px-1.5 py-0.5 text-[10px] font-semibold leading-none text-white">
                  {healthIssues}
                </span>
              )}
```

- [ ] **Step 3: Typecheck, lint and build**

Run:
```bash
cd src/Themearr.Web && npx tsc --noEmit && npm run lint && npm run build
```
Expected: all clean.

- [ ] **Step 4: Commit**

```bash
git add src/Themearr.Web/src/components/layout/Sidebar.tsx
git commit -m "feat(web): add System nav entry with health warning badge"
```

---

### Task 13: Manual verification and README

The checks only earn trust if they fire on a real misconfiguration.

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Verify a real failure is caught end to end**

```bash
cd src/Themearr.Web && npm run build && cd ../..
rm -f /tmp/themearr-verify.db
THEMEARR_AUTH_TOKEN=dev-token-at-least-16-chars DB_PATH=/tmp/themearr-verify.db \
  dotnet run --project src/Themearr.API &
sleep 8

# Simulate a completed setup pointing at a path that does not exist
sqlite3 /tmp/themearr-verify.db \
  "INSERT OR REPLACE INTO settings (key,value) VALUES ('setup_complete','1');" \
  "INSERT OR REPLACE INTO settings (key,value) VALUES ('library_paths','[\"/mnt/does-not-exist\"]');"

# Wait out the 60s health cache, then check
sleep 61
curl -s -H "Authorization: Bearer dev-token-at-least-16-chars" http://localhost:5000/api/system/health
kill %1
```

Expected: JSON containing `"type":"error"`, a message naming `/mnt/does-not-exist`, and a `wikiUrl` ending in `#library-paths--path-mappings`.

If `library_paths` is stored under a different settings key, read `Database.GetLibraryPaths()` (around `src/Themearr.API/Data/Database.cs:252`) and use the key it actually reads.

- [ ] **Step 2: Click through the UI**

Open `http://localhost:5000/system` after signing in with the dev token and confirm:
- the Health tab lists the missing-path error with a working *How to fix this* link
- the Tasks tab shows **Sync Library** with a *Run now* button
- clicking *Run now* twice quickly does not queue two syncs
- the sidebar **System** entry shows a red `1` badge

- [ ] **Step 3: Update the README**

In `README.md`, add to the feature list under **What it does** (after the "Downloaded status tracked per movie" bullet):

```markdown
- System page with health checks and scheduled tasks, arr-style
```

Then add this section immediately before `## Updating`:

```markdown
## Health checks

**System → Health** flags the things that silently break downloads: a library path
that is missing or read-only, a Plex server that is unreachable or has rejected its
token, a missing RapidAPI key, an exhausted quota, and a stalled auto-download
worker. Only problems are listed, so an empty page means everything is fine.

**System → Tasks** shows when the library last synced and lets you trigger a sync
immediately with *Run now*.

For external monitoring, Themearr exposes an unauthenticated `/health` endpoint
that returns `{"status":"Healthy"}` and nothing else — enough for Uptime Kuma or
Gatus, without leaking any configuration.
```

- [ ] **Step 4: Run everything one last time**

```bash
dotnet test
cd src/Themearr.Web && npx tsc --noEmit && npm run lint && npm run build
```
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs(readme): document the System health and tasks page"
```

---

## Self-review notes

**Spec coverage.** Every spec section maps to a task: architecture and `TaskRegistry` → Tasks 1–2; unresolved-path recording → Task 3; the DTO and severity mapping → Task 4; the four checks → Tasks 5–8; the 60-second cache, API contract and `/health` → Task 9; the UI, tabs and badge → Tasks 10–12; failure handling is folded into each check (3-second timeout in Task 8, no raw exception text in Tasks 5–8); testing is folded into each task's TDD cycle.

**Two spec corrections made while writing this plan.** `ApiAuthMiddleware` needs no change, because it guards only `/api/*` and `/health` is outside that prefix. And unresolved movies never enter the `movies` table, so Task 3 was added to record the count during sync — without it, `LibraryPathsCheck` would have had nothing to count. Both are now reflected in the spec.

**One deliberate test gap.** The "library path is not writable" case is not unit-tested. Making a directory genuinely unwritable is unreliable across platforms and no-ops when tests run as root, which is the normal case in CI containers. `ThemeFiles.IsDirectoryWritable` is already covered by `ThemeFilesWritableTests`; the check merely calls it. This is called out inline in Task 5 so nobody "fixes" it with a `chmod` test that passes locally and silently does nothing in CI.

**Two places where the plan tells the implementer to verify rather than assume.** The `IThemeAudioProvider` fake in Task 6 and the `Button` variant in Task 11 are both written from a partial reading of the real declarations, and each carries an inline note to check the source first.
