# Plex Item Refresh After Theme Inject — Design

**Date:** 2026-08-10
**Branch:** `feat/45-plex-refresh-after-inject`
**Issue:** #45

## Goal

After Themearr writes a theme file, ask the Plex server to Refresh Metadata for *that
specific item*, so the theme starts playing without the user manually refreshing it.

The issue's own evidence sets the shape: movies already appear to work on the reporter's
install because Plex's "run a partial scan when changes are detected" library setting
notices the folder change; shows do not, so every show theme needs a manual refresh. Any
fix that fires on the movie path but not the show path rebuilds the reported bug.

## Where a theme actually lands

Every theme write in the codebase funnels through `DownloadService.RunAsync` — the
provider path hands the output path to `IThemeAudioProvider.DownloadAsync`
(`DownloadService.cs:182`, writing atomically inside `RapidApiThemeAudioProvider.cs:131`)
and the direct-URL path writes via `ThemeFiles.WriteAtomicAsync`
(`DownloadService.cs:200`). All five download entry points reach it through
`DownloadService.Start`:

- Movie manual: `MoviesController.cs:161`, `MoviesController.cs:180`, `MoviesController.cs:209`
- Show manual: `ShowsController.cs:84`, `ShowsController.cs:111`
- Movie auto loop: `AutoDownloadService.cs:230`
- Show auto loop: `ShowAutoDownloadService.cs:211`

`RunAsync` already branches movie/show only to pick the DB row
(`DownloadService.cs:140`), and its success tail sets status and history for both media
types (`DownloadService.cs:215`–`217`). **One hook there covers both halves of the
movie/show parallel structure, manual and auto alike** — this feature does not need the
usual paired change.

## Who has a Plex ratingKey

The refresh endpoint needs a server + ratingKey. What each source actually stores in
`source_ref`:

| Source | `source_ref` | Evidence |
|---|---|---|
| Plex movie | `{serverId}:{ratingKey}` | built at `PlexService.cs:270`, stored at `PlexService.cs:293` |
| Plex show | `{serverId}:{ratingKey}` | built at `PlexService.cs:391`, stored at `PlexService.cs:421` |
| Radarr movie | Radarr's own numeric id | read at `RadarrLibrarySource.cs:122`, stored at `RadarrLibrarySource.cs:139` |

Both `GetMovie` and `GetShow` rows expose these as `["source"]` / `["sourceRef"]`
(`Database.cs:917`–`918` movies, `Database.cs:881`–`882` shows), so `RunAsync` already
holds everything the refresh needs.

**Radarr movies are a documented skip, not a path-scoped section refresh.** A section
refresh would need a Plex section id and its path, neither of which Themearr stores — and
a Radarr-sourced install may have no Plex configured at all. When Plex *is* present
alongside Radarr, those users' movies keep exactly today's behaviour (partial scan, which
the issue reports as working for movies). Inventing a Plex identity for a Radarr row
would be exactly the "do not invent fields" failure.

## The call

`PUT {serverUrl}/library/metadata/{ratingKey}/refresh` — the same request Plex's own
"Refresh Metadata" item action issues (and what python-plexapi's `refresh()` sends). It
works identically for movies and shows, which is why the shared hook suffices.

New method on `PlexService`, because "all of the Plex API work stays in `PlexService`"
(`PlexLibrarySource.cs:7`–`8`):

```
Task<bool> RefreshItemMetadataAsync(string? source, string? sourceRef, Action<string>? logFn = null)
```

- Returns `false` without any HTTP when `source != "plex"` (the Radarr skip), when
  `sourceRef` does not split into two non-empty parts on `:` (the same parse the poster
  path uses, `PlexLibrarySource.cs:26`–`27`), or when the server id is no longer in
  `GetPlexServersDict()` (`Database.cs:430`) — a server removed since the sync.
- Resolves the server URL and token exactly as posters do (`PlexLibrarySource.cs:28`),
  sends the PUT with the standard `ClientHeaders` (`PlexService.cs:25`), token in the
  header only — matching the probe's "header, never the URI" rule
  (`PlexLibrarySource.cs:92`–`93`).
- Bounded by its own 10-second `CancellationTokenSource`. This runs on the tail of a
  download job *before* the job is marked finished, and `IsAnyInProgress`
  (`DownloadService.cs:89`–`92`) gates the auto-download loop — a wedged Plex server must
  not hold that gate for the typed client's default 100 s.
- Catches `HttpRequestException` / `OperationCanceledException` into a `logFn` line and
  `false`; anything unexpected bubbles to the caller's guard below.

**Not HostGuard's territory.** The URL is the user's own configured Plex server, fetched
with the same injected client every other `PlexService` call uses (e.g.
`PlexService.cs:336`); private IPs are the normal case there. HostGuard protects fetches
of *untrusted, user-supplied* URLs and is untouched.

## The hook

`DownloadService` gains an optional constructor dependency `PlexService? plex = null`.
Optional for two load-bearing reasons:

- Ten-plus existing tests construct `DownloadService` with the current five arguments
  (`DownloadServiceTests.cs:82` is the shared builder) and must keep compiling unmodified
  — "never edit an existing test to make a change pass".
- DI resolves it automatically: `PlexService` is registered (`Program.cs:49`), and a
  singleton capturing it has precedent — `PlexLibrarySource` is a singleton
  (`Program.cs:37`) that takes `PlexService` directly (`PlexLibrarySource.cs:10`).
  `Program.cs` therefore needs no change.

In `RunAsync`'s success tail, after history is recorded (`DownloadService.cs:217`) and
before the job flips to finished (`DownloadService.cs:225`), a private
`TryPlexRefreshAsync` runs. It never throws: a refresh failure after a successful
download is logged (`ILogger` warning with `LogSanitizer.Clean`, matching
`DownloadService.cs:245`, plus a job-log line the UI shows) and nothing else — the theme
is already on disk, so failing or rolling back the job would turn a cosmetic miss into a
false failure. Running *inside* the job, before `finished`, keeps the outcome visible in
that job's own log and makes test ordering deterministic.

Existing tests stay inert naturally: the shared builder seeds movies with source
`"srv1"` (`DownloadServiceTests.cs:72`), which fails the `source == "plex"` gate before
any HTTP could happen.

## Toggle: none — unconditionally on

Defended, not defaulted:

- It automates precisely the manual step the issue describes, and only fires after
  Themearr itself changed that item's folder. A user who wants themes downloaded but not
  noticed by Plex is not a real persona.
- Blast radius is one PUT per successful download, to the user's own server, scoped to
  one item. Plex's partial-scan setting already causes the equivalent for movies
  implicitly — this is not a new class of behaviour.
- Failure is best-effort and logged; a refresh changes nothing Plex wouldn't change on
  its own next scheduled refresh.
- A toggle costs a settings field, frontend surface, and capability propagation across
  dashboard/system for an operation with no plausible harm. If a real need surfaces, an
  env-var escape hatch can be added later without a wire change.

## Testing

**xUnit (new file, `PlexRefreshAfterDownloadTests.cs`):**

- Movie download with source `plex` / `srv1:45` and a registered server → the Plex
  handler sees `PUT /library/metadata/45/refresh` with the server token in
  `X-Plex-Token`; job finishes clean, status `downloaded`.
- Show download (mediaType `show`) → same refresh fires. **This is the test that pins the
  issue's actual complaint.**
- Radarr movie (source `radarr`, numeric ref) → zero Plex requests; job still finishes
  clean.
- Plex answers 500, and separately the handler throws → job error stays `null`, status
  stays `downloaded` (refresh failure never fails the download).
- `RefreshItemMetadataAsync` directly: unknown server id and malformed refs → `false`
  with no HTTP.

Existing `DownloadServiceTests` / `DownloadServiceShowTests` pass **unmodified**.

**Frontend:** no change (`npm test`, `npx tsc --noEmit`, `npm run lint` stay green at
0 errors / 3 warnings).

## Untouched surfaces, considered

- **History** — no. History records themes acquired (`DownloadService.cs:217`); the
  refresh is the delivery tail of that same event, and its outcome already lands in the
  per-job download log. A second row per download would be noise.
- **Dashboard / stats** — no. Nothing countable changed; themes and coverage are the
  facts, refresh is plumbing.
- **Queue** — no. The refresh lives inside the download job the queue already tracks;
  its outcome appears in that job's log lines.
- **Settings** — no, deliberately (see Toggle).
- **System / health tab** — no. A persistently failing refresh means the Plex server is
  unreachable or the token is bad, which the existing Plex reachability check already
  reports (`PlexLibrarySource.cs:70`–`77`); a dedicated "last refresh failed" indicator
  would duplicate it.
- **Movies half / shows half** — both covered by the single `RunAsync` hook, and the
  show path carries its own test precisely because "movies work, shows don't" is the bug
  being fixed.

## Out of scope

- **Refresh on theme delete** (`MoviesController.cs:62`, `ShowsController.cs:134`). The
  same staleness argument applies in reverse, but the issue is about inject; delete can
  reuse `RefreshItemMetadataAsync` as a follow-up.
- **The Plex webhook not triggering show sync** — separate, deliberately unwired path;
  not touched.
- **Path-scoped section refresh for Radarr movies** — needs data Themearr doesn't store;
  see "Who has a Plex ratingKey".
- **Retrying failed refreshes** — best-effort by design; the user-visible fallback is
  the manual refresh they do today.
