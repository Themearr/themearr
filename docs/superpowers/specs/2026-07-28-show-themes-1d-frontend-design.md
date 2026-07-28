# Show Themes — Phase 1d (Frontend) Design

**Date:** 2026-07-28
**Branch:** `feat/show-themes-phase-1`
**Depends on:** 1a (discovery), 1b (download engine), 1c (shows API)
**Completes:** Phase 1 — Plex-sourced show themes

## Goal

Give shows a face. 1a–1c built discovery, downloading and an HTTP API, but nothing in the
app can reach any of it. 1d adds the Shows page, queue integration, the Settings
show-library selector and the nav entry — the point at which show themes become a feature
an operator can actually use.

## Approach

Three decisions, taken during brainstorming:

1. **A separate `/shows` page, plus a `Movies | Shows` toggle on the Queue.** Consistent
   with the parallel-stack decision that governed 1a–1c, keeps the movie pages' own routes
   and copy intact, and makes shows discoverable in the sidebar. Triage stays a single
   habit rather than two separate flows.
2. **Generalize the two heavy components rather than duplicate them.** What duplication
   would copy is precisely the windowing, in-flight guards and refresh-staleness logic that
   needed its own fix-spec (`2026-07-21-frontend-failure-handling-design.md`). Two copies
   of that will drift, and the existing vitest suite pins the behaviour closely enough that
   generalizing is verifiable in a way the backend controllers were not.
3. **The show-library selector lives in Settings only.** Shows are opt-in —
   `ShowSyncService` already no-ops when nothing is selected — so first-run setup stays
   movies-only and unchanged. Movie-only operators never click past a step they don't need.

## Backend addition

1d is not purely frontend. `Database.GetSelectedShowLibraries` / `SetSelectedShowLibraries`
have existed since 1a, but no controller exposes them.

Extend the existing settings endpoint rather than adding a new one: it is the same "which
libraries do I care about" concern, on the same page, saved by the same action.

- `SettingsController.Get()` gains `selectedShowLibraries = db.GetSelectedShowLibraries()`.
- `SettingsPayload` gains `SelectedShowLibraries` as a **nullable**
  `Dictionary<string, List<string>>?`.
- `Save()` writes it **only when non-null**.

**The nullability is load-bearing, not stylistic.** `Save()` currently writes
`SetSelectedLibraries(req.SelectedLibraries)` unconditionally, so any payload omitting the
new field would silently wipe the operator's show-library selection with a default `[]` —
an older cached frontend bundle after an upgrade is enough to trigger it. Skipping the
write when the field is absent is the same defensive shape as the existing *"Merge so a
save that omits the redacted token keeps the stored one"* immediately above it.

`SetupController.Save` must also be confirmed not to clobber show libraries: setup is
re-runnable by an already-configured operator, and that path already takes care not to
reset the existing Plex server/library selection.

**No new sync endpoint.** "Sync shows" reuses `POST /api/system/tasks/syncShows/run`, which
`ShowAutoSyncService` registered in 1b and which was verified working against a live server.
Sync progress is read from the existing `GET /api/system/tasks` snapshot's `syncShows` row
rather than inventing a shows-specific status endpoint: after triggering, the page polls
that snapshot until the row's `isRunning` goes false, then reloads the list once. The poll
is silent on failure — it does not drive the page's primary content, so a dropped request
must not disturb what is already shown (the rule from the frontend-failure-handling spec).

`PlexLibrary` already carries `type`, so the Settings selector filters
`type === 'show'` client-side from the existing `/api/setup/plex/libraries` response. No
change needed there.

## The media adapter

```ts
// lib/media/adapter.ts
export interface MediaAdapter {
  // endpoints
  list(): Promise<MediaItem[]>
  search(id: string, q?: string): Promise<{ results: YoutubeResult[] }>
  download(id: string, videoId: string): Promise<void>
  downloadUrl(id: string, url: string): Promise<void>
  downloadStatus(id: string): Promise<DownloadStatus>
  ignore(id: string): Promise<void>
  unignore(id: string): Promise<void>
  deleteTheme(id: string): Promise<{ deleted: boolean }>
  themeAudioObjectUrl(id: string): Promise<string>

  // presentation
  statuses: MediaStatus[]                       // which filter chips to render
  labels: { singular: string; plural: string; emptyHint: string }
}
```

Two instances: `moviesAdapter` and `showsAdapter`.

- `components/movies/MovieGrid.tsx` → `components/media/MediaGrid.tsx`
- `components/movies/SearchModal.tsx` → `components/media/SearchModal.tsx`

Both take `adapter` instead of importing `moviesApi`. The movies page passes
`moviesAdapter` and must behave identically; the shows page passes `showsAdapter` and the
`plexTheme` status. Pages stay separate and thin — they own their own data loading, sync
button and copy.

**The existing vitest suite is the regression guard for this refactor.**
`movies-refresh-race`, `polls-stay-silent`, `inflight-guards` and `actions-failure` all
exercise the exact logic being generalized and must pass **unmodified**. A change to any of
them is a signal the generalization altered movie behaviour.

## `plexTheme` in the UI

The fourth status from 1c gets first-class treatment, because it is the answer to the
original objection on issue #24 — that Themearr would "just fill everything".

- A fourth filter chip alongside pending / downloaded / ignored.
- A card badge visually distinct from the green *downloaded* badge: it means **Plex has
  this covered**, not *we fetched this*.
- The card's primary action becomes secondary and is worded **"Download anyway"**. 1c's API
  deliberately accepts a download for a `plexTheme` show, so this is an available override,
  not a blocked state.

The effect is that an operator can see exactly why a show is being skipped, and overrule it
per-show, rather than either being blanket-filled or given no explanation.

## Queue

A `Movies | Shows` segmented control at the top of `/queue`. Everything below it — advance,
ignore, search, download, progress polling — routes through the adapter.

The toggle is **component state, defaulting to Movies on every visit** — not persisted and
not in the URL. Persisting it would mean an operator who once looked at shows later opens
the Queue, sees an empty triage list, and concludes the queue is broken. Movies are the
default library and the safe default view.

The show queue is `status === 'pending'`, so `plexTheme` shows never enter triage. This
matches `GetPendingShows`, which filters on `plex_has_theme = 0`: the manual queue and the
auto-download worker agree on what counts as outstanding work.

## Nav and the Shows page

A **Shows** entry between Movies and History.

**No sync badge initially.** The Movies entry's badge costs a `syncApi.status` poll; shows
would need a second poll against a different task. Not worth it until asked for.

`/shows` mirrors `/movies`: list, a **Sync shows** button, and an empty state. When no show
libraries are selected the empty state says so and deep-links to Settings, so the opt-in is
discoverable at the moment it matters rather than only in a settings page nobody visited.

## Testing

- **Vitest:** shows page empty state (no libraries selected); the `plexTheme` chip, badge
  and "Download anyway" affordance; the queue's `Movies | Shows` toggle switching adapter
  and listing pending shows; the Settings show-library selector saving.
- **xUnit:** settings round-trip for `selectedShowLibraries`, **including an explicit test
  that a save omitting the field leaves the stored selection intact** — the data-loss
  hazard described above.
- **Movie tests pass unmodified**, frontend and backend alike.
- `npm run lint` and `npx tsc --noEmit` clean, per the repo's PR checklist.

## Out of scope

- **Webhook show-sync trigger** — `WebhookController` triggers only
  `AutoSyncService.SyncTaskId`, so a newly-added show waits for the 24h timer. Deferred
  since 1b.
- **Show auto-download debug endpoint** — not built here. `ShowAutoDownloadService.GetDiagnostics()`
  stays written and unreferenced through the end of Phase 1. Whether to expose it or delete
  it is a separate decision once Phase 1 lands; it is not a 1d task either way.
- **Sonarr** as a non-Plex show source, and the per-media-type source split — Phase 2.
- **ThemerrDB** as a curated source versus YouTube search — undecided, Phase 2.
