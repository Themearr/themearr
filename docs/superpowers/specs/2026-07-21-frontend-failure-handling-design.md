# Frontend failure handling, and a test harness that can see it

**Date:** 2026-07-21
**Status:** Approved, ready for implementation planning

## Goal

Stop Themearr's web UI from reporting failures as success, and add the smallest
test setup that can actually catch that class of bug.

## Why

Every frontend bug found during the last two days of work was found by a person —
the maintainer using the app, or a browser session driven by hand. None was caught
by CI, because `src/Themearr.Web` has no test harness at all: no test script, no
runner, no testing library.

A review then found six failure-handling bugs. Reading every `catch` in the app
turned up **fourteen** sites that swallow a failure, of which the six were simply
the ones someone had looked at.

The most damaging are the three that render a *reassuring* empty state when a
request fails — "No movies yet", "No downloads yet", "All caught up! Every movie in
your library has a theme" — because a single failed request tells the operator their
library is fine. A fourth, on Settings, fails differently but no better: it strands
the page on a spinner forever, with no error and no retry.

## The rule

Not every swallowed failure is a bug. Background poll failures were deliberately
made silent earlier, so that one dropped request could not blank an
already-populated table. Removing every `.catch` would reintroduce that.

What matters is what the user is owed at that moment:

- **A user-initiated action** that fails must say so. They asked for something; they
  are entitled to know it did not happen.
- **A background poll** that fails should stay silent. They did not ask, and blanking
  the screen over one dropped request is worse than the stale value.
- **An initial load** that fails must show an error, never an empty state — because
  "nothing here" and "we never found out" are different facts, and rendering one as
  the other is a lie.

That rule sorts all fourteen sites without further judgement calls.

## Classification

**Initial load — must show an error with a retry (7 sites)**

`app/settings/page.tsx:55,56,57` · `app/dashboard/page.tsx:13` ·
`app/movies/page.tsx:16` · `app/history/page.tsx:23` · `app/queue/page.tsx:34`

`settings:55` is the worst of these: a failed `GET /api/settings` leaves the page on
a spinner with no error and no retry, and the Library Source, API Key and RapidAPI
sections all sit behind that gate — so one 500 makes every setting unreachable.

The other two Settings loads are **supplementary, and their failure must not block
the page**. `settings:56` fetches the version for a display line and `settings:57`
reports whether a RapidAPI key is stored; if either fails, its own small section
should say so while the rest of Settings stays usable. Only `settings:55` gates the
page, and only it should.

**Background poll — stays silent, already correct, not changed (5 sites)**

`settings:95` (update status, 1s) · `settings:294` (version re-check) ·
`movies:45` (sync status, 1.5s) · `system:87` (tasks, 10s) ·
`login:39` (Plex PIN polling)

**User action — must surface the failure (3 sites)**

- `settings:135` — **Check for updates**: the spinner stops and nothing is said.
- `settings:156` — **Remove RapidAPI key**: reports the key removed when the DELETE
  failed. The key is still stored and still spending quota.
- `queue:43` — **Auto toggle**: shows on when the save failed, so the background
  auto-download worker never starts.

**One that is not a swallow**

`queue:122` polls download status every second with an `await` inside and no
in-flight guard. If a response takes longer than the interval, two callbacks are in
flight, both observe `finished`, and both call `advanceQueue()` — the `clearInterval`
in the first is too late for the second. The index advances twice and **a movie is
silently skipped**, with no theme and no message. This needs an in-flight guard, not
error handling.

## Design

### `useResource`

The seven load sites become one hook:

```ts
const { data, error, retry } = useResource(() => moviesApi.list())
```

It exposes three states — **loading**, **failed** (with a retry), **loaded** — rather
than the current two. An empty array then means genuinely empty, because failure
takes a different branch.

Pages keep their existing empty-state copy. It simply stops being reachable by
failure.

**Why a shared hook rather than seven local fixes.** The bug is not that seven sites
forgot error handling. It is that `null` and `[]` were doing double duty as both
"nothing here" and "we never found out". Seven separate fixes each have to
re-derive that distinction, and the seven authors of the current code each
reasonably decided an empty list was fine. One type with three states makes the
mistake unrepresentable rather than merely fixed.

### The three action sites stay inline

A toggle, a destructive delete and a refresh are genuinely different shapes; a
shared abstraction over them would be forced. Each surfaces its failure using the
page's existing error convention, and — critically — none reports success it did not
achieve.

### `queue:122`

An in-flight guard so a slow response cannot let two callbacks advance the queue.

## Testing

**Vitest + jsdom + Testing Library**, with `@/lib/api` mocked. Vitest reuses the
existing Vite config and TypeScript setup, so it is the smallest addition that can
render a page.

| Unit | Tests |
|---|---|
| `useResource` | loading → loaded; loading → failed; retry clears the error and re-fetches |
| The four lying pages | with a failing API, the error appears **and the reassuring copy does not** |
| The three actions | a failing action surfaces the failure; `Remove` does **not** report the key gone |
| `queue:122` | two overlapping polls advance the queue once, not twice |

**The negative assertion is the point.** "An error message appears" is easy to
satisfy wrongly — a page could show an error banner and still render "All caught
up!" beneath it, which is today's bug wearing a hat. Each page test asserts the
*absence* of the reassuring copy. `expect(screen.queryByText(/All caught up/)).toBeNull()`
would have caught the real bug; asserting an error appeared would not.

### CI

The release workflow currently runs `npm ci && npm run build` for the frontend — no
lint, no tests. `npm test` is added to that job. A harness that never runs
unattended is only a thing you can run; wiring it into CI is what makes it a gate,
and without it the next frontend bug is still found by the maintainer.

## Out of scope

End-to-end browser tests, snapshot tests, coverage thresholds, and any change to the
five correct background-poll sites. The goal is catching one class of bug, not a
coverage number.

## Success criteria

1. With the API failing, none of the Movies, History or Queue pages renders its
   empty-state copy; each shows an error and a working retry.
2. A failed `GET /api/settings` no longer strands the Settings page — it shows an
   error and the other sections remain reachable.
3. Removing the RapidAPI key when the request fails does not report success, and the
   stored key is unchanged.
4. Toggling Auto when the save fails does not leave the toggle on.
5. A download-status response slower than the poll interval cannot skip a movie.
6. The five background-poll sites still fail silently — a dropped poll does not blank
   a populated view.
7. `npm test` passes locally and runs in CI on every release build.
