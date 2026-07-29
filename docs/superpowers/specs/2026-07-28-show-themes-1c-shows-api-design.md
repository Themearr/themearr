# Show Themes — Phase 1c (Shows API) Design

**Date:** 2026-07-28
**Branch:** `feat/show-themes-phase-1`
**Depends on:** 1a (discovery engine), 1b (download engine)
**Followed by:** 1d (frontend — Shows page, queue, settings, nav)

## Goal

Expose the show themes engine built in 1a/1b over HTTP, so the frontend slice (1d) has
everything it needs: list shows with an honest status, search and download a theme,
ignore, delete, stream the theme for preview, fetch posters, and read aggregate counts.

## Context

1a discovers shows into the `shows` table (including `plex_has_theme`, parsed from Plex's
`theme` attribute). 1b routes downloads by media type, auto-fills themeless shows, and
keeps them synced. Nothing in the app can currently *reach* any of it — there is no shows
controller, so 1c is the missing API layer.

## Approach

**Parallel controller, shared helpers extracted.** The same principle that governed 1b:
duplicate the shape, share the parts where a bug bites.

Generalizing `MoviesController` by media type was considered and rejected — it is a
240-line controller whose every route would grow a branch, and the phase-wide constraint
"movie behavior must not change" is hard to hold once every movie request flows through
new conditional code.

But two pieces must not be copy-pasted, because a divergence between the copies is a real
defect:

- **Theme-file resolution + content-type mapping** — the `theme.*` lookup that skips
  `.part`/`.ytdl`, and the extension→MIME map. Currently inline in
  `MoviesController.GetThemeAudio`.
- **The "delete only within the configured library roots" guard.** Currently inline in
  `MoviesController.DeleteTheme`. This is a path-containment control; two copies is one
  copy too many.

Both move to `ThemeFiles`, which already owns `IsWithinRoots`, `HasUsableTheme` and
`WriteAtomicAsync`. Movies call the extracted helpers too, so there is one implementation.

## Endpoints

All namespaced under `/api/shows`. The movie routes' un-namespaced legacy shape
(`/api/search/{id}`, `/api/download`, `/api/download/status/{id}`) is **left untouched** —
changing it would break the existing frontend for no benefit to this slice.

| Method | Route | Notes |
|---|---|---|
| GET | `/api/shows` | list + `posterUrl` + `plexHasTheme` + 4-state `status` |
| GET | `/api/shows/{id}/search?q=` | default query = `ShowAutoDownloadService.BuildQuery(title)` (year-free) |
| POST | `/api/shows/{id}/download` | `{ videoId }` → `download.Start(id, url, "show")` |
| POST | `/api/shows/{id}/download-url` | `{ url }` — same scheme + private-address pre-checks as movies |
| GET | `/api/shows/{id}/download/status` | `download.GetStatus(id, "show")` |
| POST | `/api/shows/{id}/ignore` | |
| POST | `/api/shows/{id}/unignore` | |
| DELETE | `/api/shows/{id}/theme` | deletes `theme.*`, resets stored status to `pending` |
| GET | `/api/shows/{id}/theme/audio` | `PhysicalFile` + ETag + Last-Modified + range processing |
| GET | `/api/poster/show` | signed, expiring; Plex-only (see below) — **note the prefix** |
| GET | `/api/stats/shows` | total / pending / downloaded / plexTheme / ignored / coverage |

### Auth, and why the poster route is `/api/poster/show`

Auth is not per-controller. `Program.cs` wraps `ApiAuthMiddleware` in a `UseWhen` whose
predicate is a **path-prefix allowlist**: everything under `/api` is protected except
`/api/auth` and `/api/poster`.

A poster URL must be loadable by an `<img>` tag, which cannot send an `Authorization`
header — hence the exemption, with the signed expiring query string standing in for
credentials.

This makes the obvious route, `/api/shows/poster`, actively dangerous: it sits under
`/api/shows`, so it would 401, and fixing it means adding an exemption line adjacent to a
namespace that contains **every** shows endpoint. A single dropped path segment there
(`/api/shows` instead of `/api/shows/poster`) would silently unauthenticate the whole
shows API — a quiet, high-severity failure with no test that would obviously catch it.

Placing it at `/api/poster/show` requires **no middleware change at all**:
`StartsWithSegments("/api/poster")` already matches it. The publicly-reachable surface
stays exactly one prefix, and it cannot be widened by accident. Every other shows route
stays protected by default, which is the correct direction for a mistake to fail in.

## Status derivation

A show's reported `status` is derived in this order:

1. `ignored = 1` → **`ignored`**
2. a non-empty local `theme.*` exists in the show folder → **`downloaded`**
3. `plex_has_theme = 1` → **`plexTheme`**
4. otherwise → **`pending`**

**Rule 2 before rule 3 is the load-bearing part.** A local file is a fact on disk; Plex
having its own theme is metadata about a different artifact. Ordering it this way is what
makes "Download anyway" on a Plex-themed show coherent: the download writes a real
`theme.mp3`, and the row then reads `downloaded` rather than staying stuck at `plexTheme`.

`plexTheme` exists so the UI can explain *why* a show is being skipped instead of
presenting it as ordinary pending work — the "it'd just fill everything" concern from
issue #24. The auto-download worker already agrees with this: `GetPendingShows` filters on
`plex_has_theme = 0`.

A `plexTheme` show is **not** blocked from downloading. The status is informational; the
download endpoints accept it, and the UI is expected to require an explicit override.

## Posters

Show posters get their own route rather than extending `/api/poster`, for two independent
reasons:

- **ID collision.** `MediaFolderId.For(path)` is a pure function of the folder path, so a
  show and a movie on the same folder produce the *same* id. `/api/poster?id=X` genuinely
  cannot tell which table to read. Disambiguating by route leaves the movie path
  byte-for-byte unchanged; widening the signed payload would not. This is the same hazard
  the `{mediaType}:{id}` job key addressed in 1b.
- **Source.** `PosterController` resolves through `LibrarySourceResolver.Active`, which is
  *Radarr* for a Radarr user. Shows only ever come from Plex, so show posters must resolve
  through `PlexLibrarySource` directly — the same correction applied to the show sync
  interval in 1b.

`PosterUrlSigner` is reused unchanged (same HMAC, same 12-hour expiry). Only the route and
the lookup differ. Width clamping and the `StreamLimits.MaxPosterBytes` cap carry over.

A show only gets a signed `posterUrl` when it has a non-empty `source_ref`; otherwise the
field is `null`, mirroring `MoviesController.ListMovies`.

## Database

- **A show-specific row reader** surfacing `plexHasTheme` and the 4-state status. The
  shared `ReadMediaRow` is *not* modified — movies depend on its 3-state derivation, and
  the phase constraint is that movie behavior does not change.
- **`GetShowStats()`** backing `/api/stats/shows`: counts by derived status plus coverage.

  **Coverage is `(downloaded + plexTheme) / total`** — a show Plex already themes *is*
  covered from the user's point of view, and showing it as a gap would push them to
  download a theme they do not need. `downloaded` and `plexTheme` are also returned as
  separate counts, so the number is always explainable rather than a black box. `ignored`
  shows stay in `total`, matching how the movie stats treat them.

## Testing

- Controller tests following the existing `TestControllers.cs` pattern.
- DB-level tests for **every branch** of the status derivation, with a dedicated test that
  a local `theme.mp3` beats `plex_has_theme = 1` (rule 2 before rule 3).
- A poster test proving a movie id cannot fetch a show poster and vice versa — the
  collision case above, which is the reason the route is separate.
- A test that `/api/poster/show` rejects an unsigned or expired request.
- **A test that every `/api/shows/*` route still requires auth.** The poster exemption is a
  path prefix, so this is the regression guard against a future change widening it.
- Full suite green after each task; movie tests must not change.

## Out of scope

Deferred to 1d or later, decided explicitly:

- **Webhook show-sync trigger.** `WebhookController` triggers only
  `AutoSyncService.SyncTaskId`, so a newly-added show waits for the 24h timer. One line,
  but deferred.
- **Show auto-download debug endpoint.** `ShowAutoDownloadService.GetDiagnostics()` already
  exists and stays unreferenced until this ships. Harmless, already written.
- **All UI** — Shows page, queue integration, settings show-library selector, nav (1d).
- **Sonarr as a show source** and the per-media-type source split — Phase 2.
- **ThemerrDB** as a curated source versus YouTube search — undecided, Phase 2.
