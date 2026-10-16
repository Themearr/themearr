# Plex Item Refresh After Theme Inject — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After a theme download succeeds, PUT `/library/metadata/{ratingKey}/refresh` at
the item's own Plex server, for movies and shows alike, without ever failing the download.

**Architecture:** One new method on `PlexService` (the Plex API stays in `PlexService`,
per `PlexLibrarySource.cs:7`–`8`); one call from `DownloadService.RunAsync`'s success
tail, which every download path already funnels through. No frontend, no `Program.cs`
change, no settings.

**Tech Stack:** .NET 10 + xUnit only.

## Global Constraints

- **Both media types via the one hook.** `RunAsync` is shared by movies and shows
  (`DownloadService.cs:140`), so the hook lands once — but the show test is mandatory,
  because "movies work, shows don't" is the issue.
- **A refresh failure never fails the job.** The hook catches everything; the job's
  outer `catch (Exception)` (`DownloadService.cs:243`) must never see a refresh error,
  or a successfully landed theme would be reported as a failed download.
- **No existing test is edited.** `DownloadService` gains `PlexService? plex = null` as
  an *optional* parameter so the five-argument constructions (builder at
  `DownloadServiceTests.cs:82`) compile unchanged. Existing fixtures use source `"srv1"`
  (`DownloadServiceTests.cs:72`), which the `source == "plex"` gate leaves inert.
- **Refresh only with a real Plex identity.** `source == "plex"` and `sourceRef` splits
  `serverId:ratingKey` (parse mirrors `PlexLibrarySource.cs:26`–`27`); server resolved
  via `GetPlexServersDict()` (`Database.cs:430`). Radarr rows (numeric ref,
  `RadarrLibrarySource.cs:122`) are skipped silently.
- **Bounded at 10 s** by an internal CTS so a wedged Plex can't hold `IsAnyInProgress`
  (`DownloadService.cs:89`–`92`) for the client-default 100 s.
- Token in the `X-Plex-Token` header only, never the URI (`PlexLibrarySource.cs:92`–`93`).
- Any user-controlled value reaching `ILogger` goes through `LogSanitizer.Clean`
  (pattern at `DownloadService.cs:245`).
- Gate: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo` ends
  `Failed: 0, Skipped: 0` with 400+ tests; `npm test`, `npx tsc --noEmit`,
  `npm run lint` (0 errors / 3 warnings) from `src/Themearr.Web`.

---

### Task 1: Failing tests

**Files:**
- Create: `tests/Themearr.API.Tests/PlexRefreshAfterDownloadTests.cs`

- [ ] **Step 1: Write the tests**

Harness (own copies — test doubles are not shared across files in this suite): a
`RecordingHandler` that records `(method, path, X-Plex-Token)` per request and returns a
configurable status; a provider that writes valid theme bytes (mirrors
`DownloadServiceTests.cs:51`–`61`); a builder that seeds `SetPlexServers` with `srv1` →
the handler-backed URL, upserts the movie/show row, and constructs
`new DownloadService(provider, db, factory, config, NullLogger…, plexService)` where
`plexService = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db))`
(construction pattern from `PlexFetchShowsTests.cs:91`).

Tests:
1. `Movie_download_asks_plex_to_refresh_the_item` — source `plex`, ref `srv1:45`;
   after the job finishes: handler saw exactly one `PUT /library/metadata/45/refresh`
   carrying `X-Plex-Token: tok`; job error null; movie status `downloaded`.
2. `Show_download_asks_plex_to_refresh_the_item` — `UpsertShows`
   (`Database.cs:594`) with ref `srv1:78`, `Start(id, url, "show")`; handler saw
   `PUT /library/metadata/78/refresh`.
3. `Radarr_movie_skips_the_refresh` — source `radarr`, ref `7`; handler saw zero
   requests; job clean, status `downloaded`.
4. `Refresh_failure_never_fails_the_download` — Plex responds 500: job error null,
   status `downloaded`. Second case: handler throws `HttpRequestException` — same
   assertions.
5. `RefreshItemMetadataAsync_without_a_resolvable_identity_is_a_no_op` — direct calls:
   (`"radarr"`, `"7"`), (`"plex"`, `"no-colon"`), (`"plex"`, `"ghost:9"` with only
   `srv1` registered) all return `false` with zero HTTP requests.

- [ ] **Step 2: Watch them fail for the right reason**

`dotnet test … --filter "FullyQualifiedName~PlexRefreshAfterDownload"` — compile fails
(no `RefreshItemMetadataAsync`, no sixth constructor argument). That is the *new-class*
failure mode CLAUDE.md warns about, so the real teeth-check happens in Task 3.

---

### Task 2: Implement

**Files:**
- Modify: `src/Themearr.API/Services/PlexService.cs` (new `RefreshItemMetadataAsync`)
- Modify: `src/Themearr.API/Services/DownloadService.cs` (optional `PlexService? plex`,
  `TryPlexRefreshAsync`, one call in the success tail)

- [ ] **Step 1: `PlexService.RefreshItemMetadataAsync`**

Per the spec: source/ref/server gates returning `false`; PUT with `ClientHeaders`
(`PlexService.cs:25`); 10 s CTS; `HttpRequestException`/`OperationCanceledException`
→ `logFn` + `false`; non-2xx → `logFn` with the status code + `false`; 2xx → `true`.

- [ ] **Step 2: Hook in `DownloadService`**

Constructor gains trailing `PlexService? plex = null`. After `db.AddThemeHistory(…)`
(`DownloadService.cs:217`), before the finished `JobState`, call
`await TryPlexRefreshAsync(item, key);` — a private method that no-ops when `plex` is
null, passes `item["source"]` / `item["sourceRef"]`, adds a job-log line on success, and
catches *all* exceptions into a sanitized `ILogger` warning plus a job-log line.

- [ ] **Step 3: Full backend suite**

`dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo` →
`Failed: 0, Skipped: 0`, 400+ total.

---

### Task 3: Teeth-check

- [ ] Comment out only the `await TryPlexRefreshAsync(item, key);` line (leave the new
  classes compiled and wired), rerun the new tests: 1, 2 and the job-log assertions must
  go red because no refresh request is recorded — proving the tests bind to the *call
  site*, not merely to the new method's existence. Restore the line, rerun, green.

---

### Task 4: Verification and docs

- [ ] Full gate (backend + `npm test`, `npx tsc --noEmit`, `npm run lint` from
  `src/Themearr.Web`; frontend untouched, so no rebuild/E2E required — run them anyway
  if the Stop hook asks).
- [ ] `/check-citations` on this plan and the spec.
- [ ] Commit on `feat/45-plex-refresh-after-inject`.

## Self-review notes

- **Spec coverage:** shared-hook placement (Task 2 Step 2), ratingKey table (Task 1
  fixtures), Radarr skip (tests 3/5), never-fails guarantee (test 4), 10 s bound and
  header-only token (Task 2 Step 1), no toggle / no frontend / no `Program.cs` change.
- **Not in this plan:** refresh on theme delete, webhook show sync, Radarr section
  refresh, retries — all argued in the spec's Out of scope.
