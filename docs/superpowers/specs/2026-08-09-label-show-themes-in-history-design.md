# Show Themes in Recent Downloads and History — Design

**Date:** 2026-08-09
**Branch:** `feat/label-show-themes-in-history`

## Goal

Include show themes in the dashboard's Recent downloads, and make a show entry
distinguishable from a movie entry on both that panel and the History page.

## Context

`theme_history` has carried a `media_type` column since 1b, and both media types write to
it. Neither surface uses it, in opposite directions:

- **Dashboard "Recent downloads"** queries `WHERE media_type = 'movie'`, so show themes are
  excluded. That scoping was deliberate in 1b — it stopped show downloads inflating a
  movies-only dashboard — but the dashboard now has a Shows section, so the exclusion has
  outlived its reason.
- **The History page** calls `GetThemeHistory()`, which is **not** filtered, so show themes
  are already listed there — rendered through `entry.movieTitle` with nothing indicating
  they are shows. On the reporting install that is ~100 rows currently mislabelled as
  movies. This surface is not incomplete; it is wrong.

`GetThemeHistory` already returns `mediaType` (added in 1b), so the History page needs no
server change at all.

## Backend: one query

In `GetStats()`, the `recentActivity` query drops `WHERE media_type = 'movie'`, adds
`media_type` to its SELECT, and carries `["mediaType"]` in each row.

**`addedThisWeek` stays movie-scoped.** It renders in the movie tile row beneath the "Movie
coverage" heading, so its current scope is correct; widening it would contradict the
labelling shipped in v1.48.0.

That is the entire server change.

## Frontend: a badge, in two places

`HistoryEntry` gains `mediaType: string`, and a small muted **Show** pill renders beside the
title on show rows — on the dashboard's Recent downloads and on the History page. Movie rows
are untouched.

One type addition covers both surfaces: `DashboardStats.recentActivity` is already typed as
`HistoryEntry[]`.

Two details that remove ambiguity:

- **Typed as `string`, not a `'movie' | 'show'` union.** The value crosses the wire, so a
  union would be a compile-time claim the runtime cannot honour. The render condition is
  `entry.mediaType === 'show'`; anything else renders as it does today.
- **No null handling is needed.** The column is `TEXT NOT NULL DEFAULT 'movie'`
  (`Database.cs:109`), and the 1b migration backfilled existing rows, so every row has a
  value.

The pill reuses the muted treatment already used by the `PLEX` badge on show cards —
`bg-[#344054]` with `text-[#D0D5DD]` — so it reads as metadata rather than as a status.

Badging only the minority is deliberate: at roughly 1437 movies to 100 shows, labelling
every row would add a pill to ~94% of a long list that never needed one, and those rows
already carry a title, year, theme name and timestamp.

The `movieId` / `movieTitle` / `movieYear` field names stay. They are historical and
inaccurate for shows, but renaming them is a wire-format change across two pages for no
user-visible gain.

## A consequence worth naming

Recent downloads lists the five most recent entries by time, regardless of type. Immediately
after a bulk show sync — such as the ~100 themes just downloaded on the reporting install —
that panel will be **entirely shows**. That is chronologically correct rather than a defect,
but it is a visible change from today's behaviour and should not come as a surprise.

## Testing

**xUnit:**
- `recentActivity` returns both media types, ordered by recency, each row carrying its
  `mediaType`.
- `addedThisWeek` still counts movies only — a show download must not increment it.

**Vitest:**
- The Show pill renders on a show row and not on a movie row, on the dashboard.
- The same on the History page.
- Existing dashboard and history tests pass **unmodified**.

`npm run lint` and `npx tsc --noEmit` clean.

## Out of scope

- **A Movies/Shows filter on History.** A badge makes shows *recognisable* but not
  *findable* — with ~1437 movie rows you would still be scrolling. That is a real gap, but
  the page's existing chips are a time dimension, so adding a media dimension is a layout
  decision rather than a line of code. A clean follow-up.
- **Renaming the `movie*` history fields**, per above.
- **A "This week" tile for shows** — it would need `GetShowStats` to read history, and the
  movie tile row is correctly movie-scoped.
