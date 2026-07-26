# Show themes — Phase 1 (Plex-sourced) — design

**Date:** 2026-07-26
**Status:** Approved (design), pending implementation plan
**Origin:** Issue [#24](https://github.com/Themearr/themearr/issues/24) — backfill missing TV **show** theme songs, especially for shows Plex has no theme for (non-Plex-Pass). Strategic context: show themes play on Plex's new-experience TV clients where movie themes don't, and on Jellyfin/Emby/Kodi via local `theme.mp3`.

## Goal

For users on Plex, add TV-show theme support **end-to-end**: read show libraries → detect shows missing a theme → download `theme.mp3` into the show root → manage them in a Shows section. The output (`theme.mp3` at the series root) plays on Plex (new experience *and* old), Jellyfin, Emby, and Kodi — so this is "not just Plex" on the consumption side from day one.

## Scope

**In:** Plex as the show source; a `shows` data model; detection of shows missing a theme; the download pipeline for shows; a Shows section in the UI; opt-in show-library selection.

**Out (Phase 2 — committed follow-on, not dropped):**
- **Sonarr** as the no-Plex show source, and **splitting the single `library_source`** into independent per-media-type sources (so movies-from-Radarr + shows-from-Sonarr, or Plex-movies + Sonarr-shows, become expressible). Phase 1 needs no source-split because shows ride the user's existing Plex connection.

**Also out:** any change to movie behavior beyond a mechanical shared-primitive rename (see Data model).

## Architecture — a parallel show stack

Per the codebase map, the genuinely generic primitives are already folder/string based and reused **unchanged**: `ThemeFiles`, `IThemeAudioProvider`/`RapidApiThemeAudioProvider`, `YoutubeService` scoring, `LocalFolderResolver`, `PosterUrlSigner`, `TaskRegistry`, and the whole health/system subsystem. Shows get their own table, source-fetch, sync, and controller **alongside** movies. We do **not** generalize `ILibrarySource`, the `Database` movie methods, or the sync/download services — those are duplicated per media type (cheaper and lower-risk than a generic abstraction for two types).

### Data model

- **New `shows` table**, columns mirroring `movies`: `id` (PK), `folderName` (show root, unique), `source` ('plex'), `source_ref` ('{serverId}:{ratingKey}'), `title`, `year`, `sourcePath`, `status`, `ignored`, `synced_at`. Created via an additive migration following the existing `Migrate*` pattern.
- **Identity:** rename `MovieFolderId` → **`MediaFolderId`** (mechanical, compiler-checked; logic already generic) and use it for both. `shows.id = MediaFolderId.For(showRootFolder)`. *(Flagged: this is the one refactor that touches movie code. If undesirable, shows can call `MovieFolderId` under its existing name — cosmetic only.)*
- **Status is disk-derived**, exactly like movies: `theme.mp3` (any `theme.*` non-`.part`) in the show root ⇒ `downloaded`, else `pending`, unless `ignored`.
- **`theme_history`:** add a `media_type` column (`'movie'` default for existing rows, `'show'` for show downloads) so the History page can carry both; existing `movie_*` column names are kept (they hold the show's id/title/year for show rows).

### Sourcing (Plex show libraries)

- Extend the Plex integration to list **show** libraries (`/library/sections`, `type=show`) — generalizing the existing `ListLibrariesAsync` `libraryType` param already added in the spike's branch.
- Fetch shows from a show section (`/library/sections/{key}/all?type=2`) as `<Directory type="show">` elements. For each, read: `title`, `year`, the **show root folder** from the `<Location path>` element (a show may have several; Phase 1 uses the first), and the **`theme`** attribute (has-theme). The spike's `PlexShowThemes.Parse` is the starting point, extended to also pull `<Location>`.
- Resolve the show root through the existing **path-mappings** (`LocalFolderResolver`) — but via a **show-root entry point** that maps the folder directly, not the movie flow that derives a parent from a file path. Unresolved paths are skipped with the same "add a Path Mapping" log + health warning as movies.
- New `ShowRecord(Folder, Source, SourceRef, Title, Year, SourcePath, HasPlexTheme)` returned by a Plex show-fetch method (parallel to `FetchMoviesAsync`).

### The "don't fill everything" rule

A show is a **download candidate only when** it has **no** `theme.mp3` on disk **and** Plex reports **no** theme (`theme` attribute absent). Shows Plex already themes (Plex-Pass, or a pre-existing local file) are left alone. This directly targets #24's *"missing show themes that do not have plexpass music"* and answers the maintainer's original "it'd just fill all of them" concern.

### Sync

- A **show sync** (parallel to `SyncService`) fetches Plex shows from the **selected** show libraries, upserts the `shows` table, and prunes removed shows — reusing the fetch → upsert → prune-except shape against show-specific `Database` methods (`UpsertShows`, `PruneShowsExcept`, …).
- Registered as its own scheduled task in `TaskRegistry` (e.g. `syncShows`), on the Plex `SyncInterval` (24h). Runs only when show libraries are selected (opt-in).

### Downloads

- Reuse the **entire** existing pipeline. `DownloadService` is already keyed by an opaque id and writes to `Path.Combine(folder, "theme.mp3")` — the only movie-typed calls (`db.GetMovie`, `db.SetMovieStatus`, `db.AddThemeHistory`) get show-aware routing (by media type / id lookup).
- **Show-tuned search query:** `"{title} theme song"` — **drop the year** (a show isn't per-year). Existing keyword scoring (`theme`, `main theme`, `ost`, penalties for `compilation`/`reaction`) applies as-is; `YoutubeService.SearchAsync`'s `movieTitle`/`movieYear` params are generalized to `title`/`year?`.
- **Auto-download** follows the existing global `auto_download` setting; candidates come from a `GetPendingShows()` pre-filter mirroring `GetPendingMovies()`.

### Frontend

- New **Shows** section mirroring Movies: a `Sidebar` nav entry and a **Shows** grid page with per-show search/download/ignore.
- **Reuse `MovieGrid`/`SearchModal`** by generalizing just those two shells to a shared media-item shape (they already hold zero movie-specific logic); everything else (page, `showsApi`, `Show` type) is a clean parallel to the movie code.
- **Queue:** the existing Queue page (whose search → best-match → download → poll → advance flow the map found to be media-type-agnostic) gains a **Movies/Shows type filter** and lists pending shows alongside pending movies — one batch-download workflow for both, rather than a duplicated show-queue page. *(Fallback if extending the shared Queue proves risky: a parallel show-queue page.)*
- **Opt-in:** a **Plex show-library selector** in Settings (next to the movie-library one). Shows stay off until libraries are selected; nothing is scanned or filled before that.

## Data flow

Select show libraries (Settings) → show sync fetches Plex shows (title/year/root-folder/has-theme) → upsert `shows`, status disk-derived → Shows grid + queue show pending (missing-theme) shows → per-show or auto-download: YouTube search (`"{title} theme song"`) → RapidAPI provider → `theme.mp3` written atomically to the show root → status flips to downloaded; history recorded (`media_type='show'`). Plex/Jellyfin/Emby/Kodi then play it.

## Error handling

- Unresolved show root path → skip that show, log the "add a Path Mapping" message, feed the existing library-path health check.
- Unreachable Plex / rejected token → existing `PlexLibrarySource` health messages (unchanged).
- Download failures (quota, CDN, private/age-gated video) → existing `DownloadService` behavior (quota cooldown, capped retries).
- A malformed show entry from Plex → skip just that show, like the Radarr per-entry guard.

## Testing

- **Parser (fixtures):** extend `PlexShowThemesTests` — `<Location>` extraction, multiple locations (use first), missing location, and the has-theme rule.
- **Show fetch (StubHandler):** sections-list (type=show) → section-all (type=2) → returns shows with root folder + has-theme; path-mapping resolution + unresolved-skip.
- **Data:** `shows` migration; `UpsertShows`/`PruneShowsExcept`/`GetPendingShows`/status-from-disk; `theme_history` `media_type`.
- **Candidate rule:** a show with a local `theme.mp3` OR a Plex `theme` attribute is not a candidate; one with neither is.
- **Download tuning:** show query drops the year; writes to the show root; history records `media_type='show'`.
- **Frontend (Vitest):** Shows grid renders; per-show download flow; show-library selector; failures surfaced (not swallowed).

## Recorded decisions (the four flagged calls, approved)

1. Separate **Shows** nav section (not a unified movies+shows library).
2. **Skip** shows Plex already themes even without a local file (per #24 intent). Narrow edge case: a Plex-Pass user's Jellyfin clients don't get a local file for those — accepted for Phase 1.
3. **Opt-in** via show-library selection in Settings.
4. **Generalize** `MovieGrid`/`SearchModal` (reuse) rather than duplicate; everything else parallel.

## Open items for the implementation plan

- **`MediaFolderId` rename** blast radius (compiler-checked; the one movie-code touch) — or keep `MovieFolderId` by name.
- Exact new **settings keys** for selected show libraries (mirror `plex_selected_libraries`), and whether show sync is a separate `TaskRegistry` task or folded into the Plex sync.
- The plan will **sequence backend-first** (model → sourcing → sync → downloads) then **frontend** (Shows page → settings selector → nav), as independently testable slices — it will be sizable.
