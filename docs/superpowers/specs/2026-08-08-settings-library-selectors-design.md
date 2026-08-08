# Settings Library Selectors — Design

**Date:** 2026-08-08
**Branch:** `feat/settings-library-selectors`
**Closes:** #32

## Goal

Let an operator change which Plex **movie** libraries Themearr watches, from Settings,
without re-running setup or factory-resetting.

## Context

Issue #32 (jazzstar26, an unprompted third-party install) reports having to **factory reset
Themearr to add a second movie library**. That is accurate, and the workarounds are worse
than they look:

- Settings has no movie-library selector. `selectedLibraries` is written only by the setup
  wizard (`SetupWizard.tsx`).
- `/setup` *is* reachable by an already-configured operator, and its Plex branch does write
  the selection — but nothing in the nav links to it, and that branch posts
  `pathMappings: []`. Anyone who found it would silently lose their path mappings. The
  factory reset was arguably the safer instinct.

v1.46.0 made this worse in one respect: it added a **Show Libraries** section to Settings,
so shows can now be re-selected post-setup while movies — the primary feature — cannot.

## Approach

A **Movie Libraries** section in Settings, mirroring the Show Libraries section shipped in
v1.46.0: a checkbox list filtered to `type === 'movie'`, saved through the page's existing
whole-object `settingsApi.save`.

**No backend change is required.** `SettingsPayload.SelectedLibraries` is already
non-nullable and already written unconditionally by `Save()`, so the endpoint accepts this
today. (This is the opposite of `SelectedShowLibraries`, which had to be nullable precisely
because it was new — an older frontend omitting it must not wipe the stored value.)

The `plexLibraries` fetch added for Show Libraries is reused as-is, so there is no extra
API call and no new endpoint.

The section always renders, and shows `No movie libraries found on your Plex server.` when
the fetch returns none — matching how Show Libraries behaves rather than conditionally
hiding itself. A Radarr install, or one whose Plex server is unreachable, therefore sees an
explanation rather than a section that silently vanishes.

Note the settings page **already round-trips `selectedLibraries` on every save** — it holds
the whole `Settings` object in state and posts it back — so this adds a control over a
value that is already being written, rather than introducing a new write path.

One behaviour to be aware of but not change: `Save()` calls `MarkSetupComplete()` when the
payload has at least one server and at least one movie library
(`SettingsController.cs:55`). It only ever *sets* the flag — nothing clears it — so
unticking every movie library cannot knock a configured install back into the setup wizard.
Re-marking it on each save is idempotent and harmless.

## After save: the part that matters

Saving alone is not enough. `selectedLibraries` only affects what the next `FetchAsync`
pulls, so ticking a box changes nothing observable until a sync runs. Shipping just a save
button would recreate the exact dead end that produced #32 — the operator sees no effect
and concludes the setting does nothing.

So a successful save renders an inline confirmation with a **Sync now** button wired to the
ordinary `syncApi.start()`. The sync is offered rather than triggered automatically: a full
Plex scan on a large library is not free, and `SyncService` already no-ops when one is in
progress, which would make an automatic trigger silently do nothing.

The prompt stays visible until the operator either triggers the sync or edits the selection
again — it is a "you still need to do this" reminder, so a timed auto-dismiss would defeat
its purpose. Triggering the sync replaces it with a plain confirmation.

The **Show Libraries** section gets the same prompt (triggering
`systemApi.runTask('syncShows')`), because it has the identical dead end today. Two
adjacent sections behaving differently for no reason is its own defect.

## Telling the truth about removal

A static hint under the movie list states what unticking does: those movies are removed
from Themearr on the next sync, **`theme.mp3` files on disk are never touched**, and
re-ticking the library restores them.

That wording is accurate rather than merely reassuring:

- `PruneMoviesExcept` (`Database.cs:497`) deletes **rows only**; it never touches the
  filesystem.
- Identity is folder-derived (`MediaFolderId.For(folder)`), so a restored row gets the same
  id back and re-derives its status from disk — a movie with a `theme.mp3` returns as
  `downloaded`.
- Ignored movies are exempt from pruning.
- `SyncService` only prunes when `movies.Count > 0 && unresolved == 0`, so unticking
  *everything* deletes nothing at all.

No confirmation dialog. The action is recoverable and the hint is adjacent to the control;
a modal would be friction without a matching risk.

## Testing

Vitest, in a new `settings-movie-libraries.test.tsx`:

- The list shows only movie-type libraries — the show library must not appear in it.
- Saving posts the expected `selectedLibraries`.
- A successful save reveals the **Sync now** prompt, and clicking it calls `syncApi.start`.
- The Show Libraries prompt calls `systemApi.runTask('syncShows')`, not `syncApi.start`.

Existing settings tests (`settings-load.test.tsx`, `settings-plex-url.test.tsx`,
`settings-show-libraries.test.tsx`) must pass **unmodified**. `npm run lint` and
`npx tsc --noEmit` clean.

## Out of scope

- **`/setup` wipes `pathMappings` for a configured operator.** A separate latent bug,
  found while investigating this one. Worth its own issue; not fixed here.
- **No nav link to `/setup`.** Unchanged — this spec removes the *reason* to go there
  rather than making an unsafe path easier to reach.
- **Radarr as the active source.** This section is Plex-specific, exactly like Show
  Libraries. Radarr installs select nothing here.
