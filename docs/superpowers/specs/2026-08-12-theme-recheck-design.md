# Theme Re-Check — Design

**Date:** 2026-08-12
**Branch:** `feat/theme-recheck`

## Goal

Let a user re-check a library they have already populated, so themes chosen under older
matching rules can be replaced when the rules improve.

The matching rules do keep changing — `ThemeMatch.IsConfident` was added in #41 to stop
trailers landing in `theme.mp3`, and the confidence floor was tightened again in #42 and
#47. Every one of those improvements applied only to *future* downloads. A library synced
before #41 still has the trailers #41 was written to prevent, and nothing in the product
will ever revisit them.

## The shape of the problem

Two facts about the codebase decide the design.

**Judging a stored pick is free; finding a replacement is not.** `ThemeMatch.IsConfident`
is a pure function of `(score, videoTitle)` (`ThemeMatch.cs:101`), deliberately kept apart
from the live YouTube call because that call is not reproducible — the same query returned
different top results 63 minutes apart (`ThemeMatch.cs:8-10`). So "is the theme I already
have still acceptable?" can be answered with no API request at all. Only "what should
replace it?" costs quota.

**The evidence already exists.** Nothing on the `movies` or `shows` row records which
video produced the theme — the row carries only `status` (`Database.cs:29`). But
`theme_history` does: `theme_title` is the YouTube video's own title and `source_url` its
watch URL, written on every successful download (`DownloadService.cs:213`, from the
provider's response at `RapidApiThemeAudioProvider.cs:109`). Existing installs therefore
need no backfill.

Those two together give the feature its shape: **a free judging pass over the whole
library, and a paid search only for the items that fail it.** A healthy library re-checks
at zero API cost.

## Decisions

### Replacement rule: only replace what is now wrong

A re-check re-judges each stored pick against today's rules. If it still passes, the item
is left completely alone — no search, no download, no churn. Only a pick that now *fails*
is eligible for replacement.

The rejected alternative was comparing scores and taking any higher-scoring result. That
would swap themes that were already correct, because YouTube's ranking moves on its own
(`ThemeMatch.cs:8-10`) — ranking noise, not quality, would be driving the swap, at one
API call per item per run.

### A theme you chose by hand is never replaced automatically

An auto-picked theme that fails today's rules is replaced without asking. A hand-picked
one is flagged for review instead. This matters because manual picks fail the rules more
often than they should: a user who deliberately chooses "Blade Runner 2049 - Mesa (Hans
Zimmer)" has picked a real track with no music-evidence word in its title, so
`HasMusicEvidence` (`ThemeMatch.cs:65`) rejects it. Replacing that silently would be the
product overruling an explicit human decision.

### Provenance for pre-upgrade themes is inferred, not guessed

Every download path funnels through the same `DownloadService.Start` →
`AddThemeHistory` call (`DownloadService.cs:213`), so nothing recorded *how* a theme was
chosen. Going forward a new `picked_by` column records it. For rows written before this
ships, provenance resolves at read time from the one signal the data actually contains:

```
picked_by ?? (DownloadService.IsProviderUrl(source_url) ? "auto" : "manual")
```

A non-YouTube `source_url` can only have come from `POST /api/download-url`
(`MoviesController.cs:184`) — a human typed it. `IsProviderUrl` is already static and
already the single source of truth for that question (`DownloadService.cs:107`).

The residual gap is stated rather than papered over: **a legacy theme hand-picked from the
search list is indistinguishable from an auto-picked one, and may be replaced without
being asked about.** The alternative — treating every legacy row as manual — would make
the first re-check, the one that clears the backlog the feature exists for, entirely
manual.

### No theme is deleted up front

A flagged item keeps its existing theme until a replacement has actually landed. This
needs no new machinery: both download paths write through `ThemeFiles.WriteAtomicAsync`
(`ThemeFiles.cs:93`) — the provider at `RapidApiThemeAudioProvider.cs:130-132` and direct
URLs at `DownloadService.cs:196-197` — so a failed or empty download cannot clobber a
good file. A user never goes from wrong audio to silence.

### The flag cannot reuse `status`

`status` and "is the theme any good" are orthogonal facts, and packing them into one
column produces a specific bug: the auto-download worker reconciles any `pending` item
that has a theme file on disk straight back to `downloaded` (`AutoDownloadService.cs:176-180`),
so a flagged item would leave the queue on the next tick. The flag gets its own column.

## Data model

All three changes are additive, following the existing idempotent
`PRAGMA table_info` + `ALTER TABLE ... ADD COLUMN` migration pattern
(`Database.cs:83-121`).

| Table | Column | Values |
|---|---|---|
| `theme_history` | `picked_by TEXT` | `'auto'` / `'manual'` / NULL (pre-upgrade) |
| `theme_history` | `match_score INTEGER` | the score behind the pick; NULL (pre-upgrade) |
| `movies`, `shows` | `recheck_state TEXT` | NULL / `'queued'` / `'review'` / `'kept'` |
| `movies`, `shows` | `recheck_reason TEXT` | why it was flagged; shown in the queue |

`match_score` exists because the score cannot be recomputed faithfully — see below. Both
auto callers already have it: `RankAndMark` writes the score back into every search result
(`YoutubeService.cs:68`), which is the same dictionary they read `videoId` and `bestMatch`
from. It is null for a manual pick — `DownloadRequest` carries only the movie and video id
(`MoviesController.cs:226`) — and for a direct-URL download, neither of which was scored.

`picked_by` is plumbed through `DownloadService.Start(id, url, mediaType, pickedBy)`.
Four callers per media type set it:

| Caller | Value | Why |
|---|---|---|
| `POST /api/download` | `manual` | the human chose this video |
| `POST /api/download-url` | `manual` | the human typed this URL |
| `POST /api/auto-download/{id}` | `auto` | the human clicked, the matcher picked |
| `AutoDownloadService` | `auto` | nobody was watching |

## Judging a stored pick

A new pure method beside the existing ones:

```csharp
// null = the stored pick still passes today's rules; otherwise the reason it does not.
public static string? RejectionReason(
    string videoTitle, string? mediaTitle, int? year, int? storedScore)
```

It applies the same two vetoes `IsConfident` applies — the `PromoMarkers` disqualifier and
`HasMusicEvidence` (`ThemeMatch.cs:101-107`) — and returns a human-readable reason, which
is stored in `recheck_reason` and rendered in the queue. Computing the reason once at flag
time is what lets the queue explain itself without a second lookup.

### The score cannot be recomputed, so it is stored

`IsConfident`'s third condition is `score > 0`, and `Score` takes channel and duration
(`ThemeMatch.cs:120`) — neither of which was ever persisted. Recomputing with
`channel: ""` and `duration: null` produces a **different, lower** score than the one the
original decision used, and that difference flags themes that were fine:

Six of the nine `MusicPhrases` and both `MusicWords` (`ThemeMatch.cs:20-33`) contribute
nothing to `Score` — only `theme` (+15), `soundtrack` (+12) and `score` (+8) appear in the
keyword block (`ThemeMatch.cs:143-149`). `ost` is worth +12 only via the literal `" ost"`
(`ThemeMatch.cs:147`), while `HasMusicEvidence` matches it through `ContainsWord`, so
`"Dune [OST]"` has evidence and scores zero. A theme titled `"Opening Credits [OST]"`
scored 15 at download time on its duration bonus alone and recomputes to 0 — and `0 > 0`
is false, so it would be flagged and needlessly re-downloaded.

So `theme_history` gains **`match_score INTEGER`** alongside `picked_by`, written at
download time. When it is present, the review reproduces the original judgement exactly,
and there is no divergence to reason about.

**Pre-upgrade rows have no stored score, and take a deliberately weaker test.** For those,
the score is recomputed and rejects only when it is **negative**, not merely zero. The
distinction is the point: a zero recomputed score means the evidence word simply carries
no weight in `Score`, which says nothing about quality; a negative one means the title
carries active negative markers — `compilation`, `reaction`, `ranked`, `every`
(`ThemeMatch.cs:168-172`) — which is a genuine reason to flag. Legacy rows therefore
under-flag rather than false-flag, which is the right direction of error for a feature
whose promise is that it never churns a good theme.

**Items with no evidence are skipped, not flagged.** A direct-URL download leaves
`theme_title` null — it is only assigned in the `videoId` branch (`DownloadService.cs:147`)
— so there is no title to judge. Flagging those would be noise about something that
cannot be assessed.

## State machine

```
        NULL ──scan: auto pick fails──────────> 'queued'
        NULL ──scan: manual pick fails────────> 'review'
    'queued' ──replacement lands─────────────> NULL
    'queued' ──no confident match────────────> 'review'
    'review' ──user: Keep this───────────────> 'kept'
    'review' ──user: Find replacement────────> 'queued'
       any    ──theme deleted in-app─────────> NULL
```

Each transition has exactly one owner:

| Transition | Owner | Note |
|---|---|---|
| `NULL → queued/review` | `ThemeRecheckService` | the scan |
| `queued → review` | `AutoDownloadService`, `ShowAutoDownloadService` | no confident match |
| `queued → NULL` | `DownloadService` | in the success block that already sets status and writes history (`DownloadService.cs:211-213`) |
| `review → kept/queued` | `MoviesController`, `ShowsController` | the two user actions |
| `any → NULL` | `DeleteTheme` | alongside the status reset it already does (`MoviesController.cs:84`) |

Putting clear-on-success in `DownloadService` rather than in the worker means a manual
replacement from the movies page clears the flag too, for free — the flag cannot outlive
the problem.

`'kept'` is permanent: a later rules bump does not re-open it. Deleting the theme is the
escape hatch.

## Services

### `ThemeRecheckService` (singleton) — the scan

Mirrors `SyncService`'s shape exactly: `SemaphoreSlim(1, 1)`, `volatile _inProgress`, and
a fire-and-forget `Task.Run` whose continuation releases the lock (`SyncService.cs:26-39`).
Same requirement — one at a time, started from a controller, polled for status — so the
same shape, including `Current` so a caller can await the outcome rather than only learning
one was launched (`SyncService.cs:16-19`).

Walks non-ignored movies **and** shows that have a theme on disk; for each, reads the
latest `theme_history` row, resolves provenance, judges, writes flags. No API calls.

### `AutoRecheckService` (hosted) — the automatic trigger

Roughly thirty lines: warm-up delay, compare `ThemeMatch.RulesVersion` against the
`match_rules_version` setting, run one scan if the code is newer, store the new value.
`RulesVersion` ships at 1 and a missing setting reads as 0, so the first upgrade after
this lands runs exactly one scan over the existing backlog.

Keeping *when* apart from *what* mirrors `AutoSyncService`/`SyncService` and is what makes
the scan testable without a clock.

**Not registered in `TaskRegistry`.** `Snapshot` derives `NextRunUtc` as
`LastRunUtc + Interval` (`TaskRegistry.cs:157`), and a task with no schedule has no honest
value for that — the Tasks tab would show a next-run time that never arrives. The manual
trigger is a button on the queue page instead.

`ThemeRecheckService` registers as a singleton and `AutoRecheckService` as a hosted
service, alongside the existing workers (`Program.cs:62-70`).

### `AutoDownloadService` / `ShowAutoDownloadService` — a second candidate source

Pending items are still served first; flagged items are picked up only when the pending
queue is dry. A movie with no theme is a worse gap than one with a wrong theme, and this
ordering also leaves existing behaviour unchanged whenever nothing is flagged. Everything
already in the loop applies unchanged: one download at a time, quota cooldown, error
cooldown.

Two hazards, both in this repo's known bug classes:

**The outcome check inverts for recheck items.** The loop decides whether the last
download worked by testing `status != "downloaded"` (`AutoDownloadService.cs:151-155`). A
recheck item is *already* `downloaded`, so a failed replacement reads as success, no
cooldown is applied, and the item is retried every 30 seconds forever. Recheck items must
be judged by whether `recheck_state` is still `'queued'`.

**One search, then review.** A `'queued'` item that yields no confident match goes
straight to `'review'` rather than backing off for the 6h `NoMatchCooldown`
(`AutoDownloadService.cs:19`). Unlike a missing theme there is no gap to fill, and results
for the same query barely move, so retrying would burn quota indefinitely for an answer
that will not change.

## API

New `RecheckController` at `api/themes/recheck`, fronting `ThemeRecheckService` the way
`SyncController` fronts `SyncService`:

| Route | Returns |
|---|---|
| `POST /api/themes/recheck` | `{ started, detail }` |
| `GET /api/themes/recheck/status` | `{ inProgress, finished, scanned, flagged, error }` |
| `GET /api/themes/recheck/flagged` | id, `mediaType`, title, year, current theme title, reason, state |

`flagged` returns movies and shows in one list — the queue renders them together — so
`mediaType` is what routes each item's action back to the right controller pair.

Per-item actions go on the existing controllers, keeping the movie/show pairs parallel:
`POST /api/movies/{id}/recheck/keep`, `POST /api/movies/{id}/recheck/replace`, and the two
`shows` mirrors.

**`ApiAuthMiddleware.RequiresAuth` is not touched.** Every new route sits under `/api` and
outside `/api/auth` and `/api/poster`, so the existing rule guards it
(`ApiAuthMiddleware.cs:31-34`). No prefix is widened.

Media titles reaching a log go through `LogSanitizer.Clean`, as everywhere else.

## Frontend

**Queue page** gains a "Needs attention" section: current theme title, the reason, and
`[Find replacement]` / `[Keep this]`. The "Re-check themes" button lives here, with
progress while a scan runs.

**Dashboard** gains a needs-attention count, which means `StatsResult` and
`ShowStatsResult` (`Database.cs:960-963`) each gain a field and `StatsController` surfaces
it — shows as well as movies.

The recheck-status poll must stop when the scan finishes, must not fire while a request is
in flight, and must not let a stale response overwrite a newer one.
`inflight-guards.test.tsx`, `polls-stay-silent.test.tsx` and `movies-refresh-race.test.tsx`
exist because each of those was a real regression; they are the templates.

Movies and shows pages, and history, are deliberately unchanged. A replacement still
writes a history row (`DownloadService.cs:213`), so swaps stay auditable — they are just
not labelled as replacements.

## Keeping `RulesVersion` honest

`RulesVersion` is worthless if someone changes `ThemeMatch` and forgets to bump it: the
release ships, no library is re-checked, and nothing fails.

So the rules get a **golden-corpus fingerprint test** — a fixed list of roughly thirty
representative video titles run through `RejectionReason`, the verdict vector hashed, and
the hash pinned next to the expected `RulesVersion`. Change any vocabulary list, any score
weight, or any branch of the judging logic and the hash moves and the test goes red; the
only way back to green is updating the hash and the version together.

Hashing the verdicts rather than the rule arrays is what makes this cover the score
weights and the logic, not just the vocabulary.

## Testing

Both suites gate the release, and the tests that carry weight here are the failure paths —
the happy path is trivially correct.

- **Failed replacement.** A download fails on a `'queued'` item → the old theme is still
  on disk, the flag is still `'queued'`, and a cooldown was applied. This is the inverted
  outcome check above; it must be written first and watched go red, because the bug is
  silent.
- **Stored score reproduces the original judgement.** A pick that passed at download time,
  replayed through `RejectionReason` with its stored `match_score`, returns `null`. This
  is the invariant that makes the whole feature safe: with a stored score, review and
  download agree by construction.
- **A zero recomputed score is not a flag.** `"Opening Credits [OST]"` with no stored
  score returns `null` — it scores 0 without a duration bonus, and only a negative score
  rejects. Written first and watched go red against a `<= 0` test, because that is the
  false-flag this design was corrected to avoid.
- **A negative recomputed score is a flag.** `"Every Star Wars Theme Ranked"` with no
  stored score is rejected — active negative markers, not a missing duration bonus.
- **`'kept'` survives.** A kept item is untouched by a scan, including after a version bump.
- **Provenance table.** All four cases: explicit `'manual'`, NULL + YouTube URL, NULL +
  pasted URL, NULL + null `source_url`.
- **Scan selection.** Ignored items skipped; items with no theme file skipped; items with
  null `theme_title` skipped; auto failure → `'queued'`; manual failure → `'review'`;
  passing pick untouched.
- **One search, then review.** A `'queued'` item with no confident match becomes
  `'review'` and is not searched again.
- **Migration.** An upgraded database with legacy rows gets NULLs and does not throw.
- **Auth.** The new routes require a bearer token.
- **Frontend.** Queue section renders reason and actions; keep/replace hit the right
  endpoints; the button is disabled while a scan runs; the poll stops when it finishes and
  survives a stale response.

Every one of the above exists for shows as well as movies.

## Scope

Movies and shows ship together. The extension seam does not span them —
`ILibrarySource.FetchAsync` returns `IReadOnlyList<MovieRecord>` and is movie-only
(`ILibrarySource.cs:21`) — so each piece above is built twice by design, and shipping the
movie half alone is the exact miss this repo keeps making.

Reviewers to run before the PR: **`concurrency-reviewer`** (two background services
changed, one added, a new React poll) and **`security-invariant-reviewer`** (new `/api`
routes, media titles reaching logs).

Merging to `main` publishes a release; `feat:` makes it a minor bump.

## Explicitly out of scope

- **Per-item re-check from the movies or shows page.** The feature is library-wide.
- **A recurring scheduled re-check.** The answer only changes when the rules change, so
  nearly every run would be wasted work.
- **Re-opening `'kept'` items after a rules bump.** Kept means kept.
- **Labelling replacements distinctly in history.** Swaps are recorded, just not
  distinguished from first-time downloads.
