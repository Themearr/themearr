# Addendum: Plex item refresh after theme delete

Sequel to `2026-08-10-plex-refresh-after-inject-design.md` (#45). Deleting a theme left
Plex playing its cached copy until a manual "Refresh Metadata" — the same staleness #45
fixed for the inject direction. This note is deliberately an addendum, not a full
spec/plan pair: the mechanism (`RefreshItemMetadataAsync`, `src/Themearr.API/Services/PlexService.cs:512`)
already exists; the only design work was where the delete-side call lives.

## Where the call landed, and why

Downloads had a shared funnel (`DownloadService.RunAsync` calls `TryPlexRefreshAsync`,
`src/Themearr.API/Services/DownloadService.cs:223` and `:253`). Deletes have none: each
controller deletes inline via `ThemeFiles.DeleteThemes`
(`src/Themearr.API/Controllers/MoviesController.cs:80`,
`src/Themearr.API/Controllers/ShowsController.cs:151`). So the shared piece is a helper,
not a funnel:

- **`PlexService.TryRefreshItemMetadataAsync`**
  (`src/Themearr.API/Services/PlexService.cs:560`) — never-throws wrapper over
  `RefreshItemMetadataAsync`, one copy of the soft-fail/log logic. Failure logs a warning
  with the item id through `LogSanitizer.Clean` (route parameter → user-influenced).
- Both delete endpoints call it after a successful delete, fire-and-forget:
  `src/Themearr.API/Controllers/MoviesController.cs:96` and
  `src/Themearr.API/Controllers/ShowsController.cs:164`, gated on `deleted`
  (`MoviesController.cs:87`, `ShowsController.cs:155`) — no disk change, nothing stale in
  Plex, no traffic.

## Fire-and-forget, not awaited (the one divergence from #45)

The delete actions are synchronous, and their signatures are pinned by existing tests
that call them without await (`tests/Themearr.API.Tests/DeleteThemeTests.cs:66`,
`tests/Themearr.API.Tests/ShowsThemeEndpointTests.cs:36`) — making them async would mean
editing existing tests. Awaiting inline would also hold a DELETE response up to
`RefreshTimeout` (10s, `src/Themearr.API/Services/PlexService.cs:498`) on a wedged Plex
server. The discarded task is safe because the wrapper catches everything — an unobserved
faulted task is precisely what it exists to prevent.

Consequences accepted:

- The refresh outcome is invisible to the API caller; the response stays `{ deleted }`.
  It was already best-effort in #45 — a failure there only reached the job log.
- Radarr rows skip honestly via the `source != "plex"` gate
  (`src/Themearr.API/Services/PlexService.cs:514`); the gate runs before any HTTP is
  composed, so a Radarr delete produces no Plex traffic at all.
- No settings toggle, matching #45's defended decision: a refresh that is skipped for a
  non-Plex source and soft-fails otherwise has no failure mode a toggle would mitigate.

## Tests

`tests/Themearr.API.Tests/PlexRefreshAfterDeleteTests.cs` mirrors
`PlexRefreshAfterDownloadTests.cs`: movie delete refreshes, show delete refreshes (the
parity case — a movie-only hook rebuilds the classic miss), Radarr delete skips, refresh
failure (Plex 500 / unreachable) never fails the delete, and a no-op delete (no theme on
disk) sends nothing. Because the tests bind to new constructor parameters they fail to
compile without the change; the call-site coverage was additionally teeth-checked by
commenting out only the two controller calls, which turned exactly the two
refresh-arrival tests red.
