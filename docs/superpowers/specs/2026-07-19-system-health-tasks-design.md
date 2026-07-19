# System page: Health & Tasks

**Date:** 2026-07-19
**Status:** Approved, ready for implementation planning

## Goal

Give Themearr the **System** page every arr application has, covering Health and
Tasks. Health answers *"why isn't it working?"*; Tasks answers *"when did it last
run, and can I make it run now?"*

## Why this first

The user asked how to make Themearr more aligned with the arr suite. Three
directions emerged, and they decompose as follows:

| Sub-project | Depends on | Size |
|---|---|---|
| **A. System → Health / Tasks** | nothing — purely additive | small |
| B. Radarr integration | movie-identity refactor | medium |
| C. Native Jellyfin/Emby | same refactor + a second library client | large |

A was chosen first because it is independent, needs no schema migration, and
directly addresses the operational pain of the preceding audit: every bug chased
in that session — a `library_paths` mismatch, an unresolved Windows path, a
RapidAPI quota cooldown, a wedged download, a missing .NET runtime — would have
appeared as a red health check instead of requiring live debugging.

B and C each get their own spec later. Note that B partly obsoletes C: because
`theme.mp3` is read by Plex, Jellyfin, Emby and Kodi alike, sourcing a library
from Radarr reaches non-Plex users without writing a second media-server client.

Logs were considered and deliberately deferred — logging is console-only today
(`journalctl -u themearr` / `docker logs`), so a Logs page needs a new capture
layer, with its own decisions about retention and token redaction. That is a
separable spec.

## Scope

**In scope:** four health checks, two scheduled tasks, a `/system` page with
Health and Tasks tabs, a sidebar warning badge, and an unauthenticated `/health`
endpoint for external monitoring.

**Out of scope:** log capture and a Logs page; health-warning dismissals; an
event bus for future notifications. Dismissals only earn their keep once checks
are noisy, and there are none yet; the event bus guesses at a notifications spec
that does not exist.

## Constraints

- **Never spend RapidAPI quota to measure RapidAPI health.** youtube-mp36 has no
  free quota endpoint, so probing it costs a real request off the free tier —
  quota taken directly from downloads. The RapidAPI check is therefore passive:
  it reads state Themearr already holds.
- Checks may touch local disk and may ping the user's own Plex server. Nothing
  else is probed.
- Additive only. No existing service is restructured; the two background workers
  each gain roughly five lines.

## Architecture

`AutoSyncService` is registered *only* as a hosted service, with no singleton
handle:

```csharp
builder.Services.AddHostedService<AutoSyncService>();                                   // no handle
builder.Services.AddSingleton<AutoDownloadService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoDownloadService>());  // handle
```

A controller therefore cannot reach `AutoSyncService` to trigger a run. Rather
than change that registration, both workers take a dependency on a shared
`TaskRegistry` singleton, which becomes the seam. The controller never holds a
reference to a `BackgroundService`.

```
LibraryPathsCheck ──┐                       ┌─ GET  /api/system/health
PlexReachableCheck ─┤                       │
RapidApiCheck       ├─ HealthReport ────────┤
DownloadWorkerCheck ┘  (framework)          └─ GET  /health   (bare, unauthenticated)

                    ──reports──▶ ┌──────────────┐ ──▶ GET  /api/system/tasks
AutoSyncService                  │ TaskRegistry │
                    ◀──signals── └──────────────┘ ◀── POST /api/system/tasks/{id}/run
```

Workers push state in and pull triggers out; the controller does the mirror
image. Neither side references the other, so "Run now wakes the task" is testable
against the registry alone — no host, no timers, no waiting out an interval.

### Units

| Unit | Responsibility | Depends on |
|---|---|---|
| Four `IHealthCheck` classes | each answers one question about one subsystem | `Database`, `ThemeFiles`, `DownloadService` |
| `TaskRegistry` | holds last-run state; hands out trigger signals | nothing |
| `SystemController` | serialises both into arr-shaped JSON | the two above |

Each check is a standard ASP.NET Core `IHealthCheck`, so the framework provides
parallel execution, per-check exception isolation, per-check timeouts, and a free
`/health` endpoint. A thin mapper reshapes `HealthReport` into Radarr's health
DTO for the UI, serving both audiences from one engine.

### Files

**New**

- `src/Themearr.API/Services/Health/LibraryPathsCheck.cs`
- `src/Themearr.API/Services/Health/PlexReachableCheck.cs`
- `src/Themearr.API/Services/Health/RapidApiCheck.cs`
- `src/Themearr.API/Services/Health/DownloadWorkerCheck.cs`
- `src/Themearr.API/Services/Health/HealthDto.cs` — `HealthReport` → arr shape
- `src/Themearr.API/Services/TaskRegistry.cs`
- `src/Themearr.API/Controllers/SystemController.cs`
- `src/Themearr.Web/src/app/system/page.tsx`

**Modified**

- `src/Themearr.API/Program.cs` — `AddHealthChecks`, `MapHealthChecks("/health")`, `AddSingleton<TaskRegistry>`
- `src/Themearr.API/Services/AutoSyncService.cs` — report runs; race the delay against a trigger
- `src/Themearr.API/Services/AutoDownloadService.cs` — expose last-tick time for `DownloadWorkerCheck`
- `src/Themearr.API/Services/PlexService.cs` — record the unresolved-path count during sync

`ApiAuthMiddleware` needs **no** change. It guards only `/api/*`
(`Path.StartsWithSegments("/api")`), and `/health` sits outside that prefix, so
the monitoring endpoint is already unauthenticated without an allowlist entry.

### Recording unresolved paths

`PlexService.FetchMoviesAsync` currently *skips* a movie whose path cannot be
resolved (`continue`), so unresolved movies never reach the `movies` table and
cannot be counted from the database afterwards. Sync must therefore record the
outcome as it happens, writing two settings at the end of each run:

- `last_sync_unresolved_count` — how many movies were skipped as unresolved
- `last_sync_unresolved_sample` — the first such Plex-reported path, so the
  health message can show a concrete example

Both are overwritten on every sync, so a fixed mapping clears the warning on the
next run.
- `src/Themearr.Web/src/main.tsx` — `/system` route
- `src/Themearr.Web/src/components/layout/Sidebar.tsx` — nav entry + warning badge

## Health checks

| Check | Detects | Severity |
|---|---|---|
| `LibraryPathsCheck` | no library paths configured at all | error |
| | configured path does not exist | error |
| | configured path is not writable | error |
| | movies skipped as unresolved by the last sync | warning |
| `PlexReachableCheck` | server unreachable | error |
| | token rejected (HTTP 401) | error |
| `RapidApiCheck` | key/username not configured | error |
| | quota cooling down (from `IsQuotaCoolingDown`) | warning |
| `DownloadWorkerCheck` | no tick in over 5 minutes, *while auto-download is enabled* | error |

### Preconditions — avoiding false alarms

Two cases would otherwise report failures that are not failures:

- **Before setup completes.** On a fresh install there is no Plex server, no
  token and no RapidAPI key, so `PlexReachableCheck`, `RapidApiCheck` and
  `LibraryPathsCheck` would all go red on first launch and make a working install
  look broken. When the `setup_complete` setting is false, these three report
  `Healthy` and contribute nothing to the badge. The setup wizard, not the health
  page, is what guides a new user.
- **When auto-download is off.** A disabled worker does not tick, which is not a
  fault. `DownloadWorkerCheck` reports `Healthy` when the `auto_download` setting
  is off, and only applies the 5-minute staleness rule when it is on.

`LibraryPathsCheck` raises its unresolved-movies warning whenever the count is
one or more; there is no tolerance threshold, because a single unresolved movie
means a mapping is wrong for some subtree.

`LibraryPathsCheck` reuses the existing `ThemeFiles.IsDirectoryWritable()` and
`ThemeFiles.IsWithinRoots()` helpers. Its unresolved-movies case emits
*"N movies could not be resolved to a local path — check Path Mappings"* with
`wikiUrl` pointing at the README's `Library paths & path mappings` section. That
message plus that link is the support answer previously written by hand for a
user reporting `Skipping <title> — unresolved path`.

`RapidApiCheck` and `DownloadWorkerCheck` are entirely passive, reading state
that already exists in `DownloadService` and `AutoDownloadService`.

Severity maps directly: `HealthStatus.Healthy` → `ok`, `Degraded` → `warning`,
`Unhealthy` → `error`. Overall status is the worst child status.

## Tasks

Auto-download is deliberately **not** listed as a task. It ticks every 30
seconds, so *Run now* would be meaningless and the row would refresh faster than
it can be read. Its liveness belongs in Health via `DownloadWorkerCheck`.

| Task id | Name | Interval | *Run now* | Existing state |
|---|---|---|---|---|
| `syncLibrary` | Sync Library | 24h | signals `AutoSyncService` to wake | `last_auto_sync_at` setting |

"Check for Updates" was considered as a second task and rejected. `UpdateService`
has no scheduled loop — its 1-hour value is a *cache TTL*, refreshed lazily when
something asks. Listing it with a "next run" would claim a schedule that does not
exist, and manufacturing one purely to fill the table is not worth a background
loop. It stays where it already works, in Settings → Updates.

v1 therefore ships a one-row Tasks tab. That is the honest state of the app: one
thing is genuinely scheduled. The registry is the reusable part, and the Radarr
sub-project will add rows to it.

### `TaskRegistry`

```csharp
public sealed class TaskRegistry
{
    void Register(string id, string name, TimeSpan interval);
    void RecordRun(string id, DateTime startedUtc, TimeSpan duration, string result);
    Task WaitForTriggerAsync(string id, CancellationToken ct);   // workers await this
    bool Trigger(string id);                                     // controller calls this
    IReadOnlyList<TaskState> Snapshot();
}
```

`TaskState` carries `id`, `name`, `interval`, `lastRunUtc`, `lastDurationMs`,
`lastResult`, `nextRunUtc` and `isRunning`.

Each trigger is a `Channel<byte>` with capacity 1 and
`BoundedChannelFullMode.DropWrite`, so five impatient clicks on *Run now*
coalesce into one run rather than queueing five library syncs. The backpressure
lives in the channel's shape; no debounce logic is needed elsewhere.

`nextRunUtc` is derived as `lastRunUtc + interval` rather than stored, so it
cannot drift out of sync with reality.

Workers wake on either the timer or a trigger:

```csharp
await Task.WhenAny(Task.Delay(CheckInterval, ct), registry.WaitForTriggerAsync("syncLibrary", ct));
```

## API

```jsonc
// GET /api/system/health          (authenticated — full detail)
{
  "status": "warning",
  "checks": [
    { "source": "libraryPaths", "type": "error",
      "message": "Library path /mnt/pve2/Movies does not exist",
      "wikiUrl": "https://github.com/Themearr/themearr#library-paths--path-mappings" },
    { "source": "rapidapi", "type": "warning",
      "message": "RapidAPI quota exhausted — downloads paused until 14:32 UTC" }
  ]
}

// GET /health                     (unauthenticated — deliberately detail-free)
{ "status": "Healthy" }

// GET /api/system/tasks
[{ "id": "syncLibrary", "name": "Sync Library", "interval": "24:00:00",
   "lastRunUtc": "2026-07-19T02:14:11Z", "lastDurationMs": 4210,
   "lastResult": "1451 movies", "nextRunUtc": "2026-07-20T02:14:11Z",
   "isRunning": false }]

// POST /api/system/tasks/{id}/run
//   202 Accepted · 404 unknown id · 409 already running
```

`/health` is the only place this design touches authentication. It returns a
single status word: no check names, no messages, no version. An unauthenticated
caller learns nothing beyond what an open port already reveals, while Uptime Kuma
and Gatus get a standard endpoint. The compose file's loopback binding and the
existing reverse-proxy guidance continue to govern exposure.

## Data flow and caching

```
Sidebar (every page)  ─┐
                       ├─▶ GET /api/system/health ─▶ [60s cache] ─▶ 4 checks in parallel
/system page          ─┘
```

The health report is cached **server-side** for 60 seconds. Without this,
`PlexReachableCheck` would ping the user's Plex server once per open browser tab
per poll; three tabs left open overnight would generate thousands of probes.
Caching on the server collapses N tabs into one probe. This mirrors the existing
`UpdateService` cache (1 hour on success, 5 minutes on error), so it follows a
convention the codebase already has.

## UI

- New `/system` route with **Health** and **Tasks** tabs, matching arr's layout.
- Health tab: severity-coloured rows showing source and message, with a
  *Read more* link where `wikiUrl` is set.
- Tasks tab: a table with a *Run now* button per row, disabled while `isRunning`.
- `Sidebar.tsx` gains a **System** entry carrying a badge with the combined
  error/warning count. This badge is what surfaces a read-only mount without the
  user going looking for it.

## Failure handling

A failing check must never break the page. The framework already catches
exceptions per-check and reports `Unhealthy`, so one broken check degrades to one
red row. Two additions:

- `PlexReachableCheck` is registered with a **3-second timeout** via
  `HealthCheckRegistration`. An unreachable server is the expected case, and
  without a timeout the whole page waits on a TCP hang.
- **Exception text is never surfaced raw.** Plex requests carry `X-Plex-Token`,
  and an `HttpRequestException` can echo the request URL. Every check emits a
  hand-written message, and anything logged goes through the existing
  `LogSanitizer`, consistent with the earlier CWE-117 work.

## Testing

Extending the existing 94-test xUnit suite. Every check takes its dependencies
through the constructor, so these are plain unit tests with no host, no HTTP and
no timers. `PlexReachableCheck` is the only one needing an injected
`HttpMessageHandler`, to fake reachable, 401 and timeout responses.

| Unit | Tests |
|---|---|
| `TaskRegistry` | trigger wakes a waiter; five rapid triggers coalesce to one; unknown id returns false; `nextRunUtc` derives from `lastRunUtc + interval` |
| `LibraryPathsCheck` | missing dir → error; read-only dir → error; unresolved movies → warning; all good → healthy |
| `PlexReachableCheck` | reachable → healthy; 401 → error; timeout → error; no token in any message |
| `RapidApiCheck` | no key → error; cooling down → warning carrying the correct timestamp |
| `DownloadWorkerCheck` | tick within 5 min → healthy; stale tick → error; **auto-download off → healthy despite stale tick** |
| Preconditions | with `setup_complete` false, the three setup-dependent checks report healthy |
| `HealthDto` mapper | each status maps to the right arr type; overall status is the worst child |

## Success criteria

1. `/system` lists Health and Tasks, and a misconfigured library path appears as
   a red row with a working link to the README section that fixes it.
2. *Run now* on Sync Library starts a sync within a second, and repeated clicks
   produce exactly one run.
3. `/health` returns `{"status":"Healthy"}` without a token and leaks nothing
   else.
4. No health check ever consumes RapidAPI quota.
5. The sidebar badge reflects current error/warning count on every page.
6. `dotnet test` passes with the new tests added.
