# Dashboard Show Stats — Design

**Date:** 2026-08-09
**Branch:** `feat/dashboard-show-stats`

## Goal

Show TV show coverage on the dashboard, so an operator who has enabled shows can see how
they are doing without visiting the Shows page.

## Context

The dashboard is movie-only: a coverage hero, four stat tiles, and two "recent" panels, all
fed by `statsApi.get()`.

Shows have had their own stats endpoint since 1c — `GET /api/stats/shows`, returning
`{ total, downloaded, plexTheme, pending, ignored, coverage }` — and `showsApi.stats()` is
already wired in the frontend. **Nothing has ever called it.** This closes that loop.

Scope is deliberately frontend-only. In 1b the dashboard's history-derived numbers
(`addedThisWeek`, `recentActivity`) were scoped to `media_type = 'movie'`, so anything
history-flavoured for shows needs new server code. That is out of scope here.

## The section

A **Shows** block below the existing movies content: a coverage bar plus four tiles —
Pending / Downloaded / Plex theme / Ignored.

Coverage arrives from the endpoint as `(downloaded + plexTheme) / total`, so the caption
reads **"162 of 253 shows covered"**, not "downloaded". The wording is load-bearing: a
Plex-Pass-themed show *is* covered, just not by Themearr, and the **Plex theme** tile breaks
that out so the headline number stays explainable rather than surprising.

### Placement

The Shows block goes **last — below the two "recent" panels**, not between the movie tiles
and those panels.

Both panels are movie-only and will stay that way (their history data is scoped to
`media_type = 'movie'`). Slotting the Shows block above them would leave two unlabelled
movie panels sitting underneath a Shows heading, reading as though they belonged to it.
Putting Shows last means nothing above it moves or changes meaning.

### One copy change to the movies section

The movies hero currently reads **"Library coverage"**. Every number on it is movie-scoped —
`total`, `downloaded`, `pending`, `ignored` and `addedThisWeek` all are — so that label is
already slightly wrong, and adding a second coverage figure below makes it actively
misleading. It becomes **"Movie coverage"**.

That is the only change to the movies section; its tiles, panels and values are untouched.

## When it appears

Gated on `total > 0` — shows actually present in the database.

This needs no extra request and puts no empty block on a movie-only dashboard, which is
the common case. The trade-off, stated plainly: an operator who has selected show libraries
but not yet synced sees nothing here. That is correct — there is nothing to report — and
the Shows page already handles prompting them to sync.

## Tile links

All four tiles link to `/shows`, **including Pending**.

Pending would naturally link to `/queue`, but the queue's `Movies | Shows` toggle is
deliberately component state — not persisted, not in the URL — so such a link would land the
operator on the *movies* queue. That is a real cost of that earlier decision. Sending them
somewhere wrong is worse than sending them somewhere useful, so all four go to `/shows`.

If deep-linking into the show queue proves worth having, the fix is a `?media=shows` query
parameter read on mount. That is its own change, not this one.

## Failure handling

Show stats are supplementary and must never take down the dashboard. They must equally
never render as a reassuring `0%` — the failure mode this project has repeatedly shipped.

The resolution uses what is already known:

- **Movie stats failed too** → the dashboard's existing error screen already covers it;
  nothing extra is shown.
- **Movie stats succeeded, show stats failed** → genuinely anomalous, so a single-line
  notice appears where the section would be: *"Couldn't load show stats: {message}"*, in the
  same muted-red treatment the other pages use for a non-blocking failure. Rare by
  construction, and informative when it happens.
- **Succeeded with `total = 0`** → no section, no notice.

A failed show-stats fetch therefore never invents numbers and never silently disappears in
the one case where its absence is meaningful.

## Testing

Vitest, in a new `dashboard-show-stats.test.tsx`:

- No Shows section when `total = 0`; movie content unaffected.
- Section renders with the right coverage caption and tile values when `total > 0`.
- The **Plex theme** tile exists only in the shows section — movies never have that status.
- The notice appears when movie stats resolve and show stats reject, and **not** when both
  reject.
- The movies hero reads "Movie coverage", and the movie tile values are unchanged.

Existing dashboard tests must pass **unmodified**. `npm run lint` and `npx tsc --noEmit`
clean.

## Out of scope

- **A "This week" tile for shows** — needs `GetShowStats` to count show history rows, i.e.
  backend work.
- **Show entries in History.** Worth knowing: `GetThemeHistory()` is *not* filtered by media
  type, so a downloaded show theme already appears on the History page today, unlabelled and
  looking like a movie. The data to fix it (`theme_history.media_type`) already exists. A
  separate change on a separate page.
- **Deep-linking into the show queue** (`?media=shows`), per above.
