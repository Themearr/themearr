# Auto-Download Confidence Floor — Design

**Date:** 2026-08-09
**Branch:** `fix/auto-download-confidence-floor`
**Issue:** [#39](https://github.com/Themearr/themearr/issues/39) (found while investigating #28)

## Goal

Stop auto-download writing trailers, clips and promos into `theme.mp3` for films and shows
that have no real music presence on YouTube.

## The bug

`YoutubeService.SearchAsync` marks the top-ranked result `bestMatch` when its score is
above zero. That score exists to *rank candidates against each other*; using it as a
confidence gate asks it a question it was never built to answer — "is the best of these
actually any good?"

An exact title match is **+30** and a 1–6 minute runtime is **+15**. That is **45 points
before anything has established the video is music at all**, and 45 clears a gate set at
`> 0`.

Two weightings make it worse:

- **`"official"` scores +10**, which actively rewards trailers — they are titled "Official
  Trailer".
- **Plain `"trailer"` carries no penalty.** Only `trailer music` / `trailer theme` are
  penalised, at −10.

Popular titles hide the flaw completely: their candidate pool always contains something
good, so the best-ranked result is also a good result. It only surfaces when the pool is
poor.

Measured over twelve deliberately low-profile films, six picked something clearly wrong:
*Applecart* → "Trailer #1", *Hell Baby* → "Blazed Cable Guy" (a clip), *Bad Milo* →
"Official Red Band Trailer", among others.

## The rule

`bestMatch` requires two conditions instead of one:

1. the top-ranked result still scores above zero, **and**
2. its title carries positive evidence that it is music.

Music evidence is any of: `theme`, `soundtrack`, `score`, `main title`, `suite`,
`overture`, `ost` (see below), and the show forms `title sequence`, `intro`,
`opening credits`, `end credits`. Matching is case-insensitive on the video title.

**`ost` must be matched on word boundaries, not as a bare substring.** It is a substring of
*ghost*, *lost*, *most* and *post*, so a naive `Contains("ost")` would let "Ghostbusters",
"Ghost Rider" and "Lost in Translation" certify themselves as music. The existing scorer
already dodges this with `Contains(" ost")` (leading space); the floor must be at least as
careful, and should match `ost` delimited by a non-letter on both sides so a trailing
"… OST" at the end of a title still counts. Every other keyword in the list is long enough
that a plain substring match is safe.

**`"official"` is deliberately excluded from that list.** It remains a general scoring
bonus, but it must not satisfy the floor by itself — that is precisely what let *Sun Choke*
and *Bad Milo* through, both of which are trailers.

Trailer penalties are added, since none exist today:

| Marker | Penalty |
|---|---|
| `trailer` | −25 |
| `featurette`, `behind the scenes`, `interview` | −25 |
| `clip`, ` scene` | −20 |

### Why show vocabulary is in the list

Shows share this code path via `ShowAutoDownloadService`, and a movie-only keyword list
breaks them. Measured directly: *Severance*'s correct result is "Official **Intro Title
Sequence**", which a movie-oriented list rejects. A series' theme is routinely labelled as
its titles or intro rather than as a soundtrack.

## Where it applies

In `YoutubeService`, where `bestMatch` is set — one threshold in one place.

`bestMatch` has two kinds of consumer, and both inherit the change:

**Decisions** — act with no human looking at the candidate:
`AutoDownloadService`, `ShowAutoDownloadService`, `MoviesController.AutoDownload`, and
`lib/media/adapter.ts` (which mirrors the controller's rule client-side).

**Hints** — highlight a result in a list the user is reading:
`app/queue/page.tsx` and `components/media/SearchModal.tsx`.

Tightening the hint is intended, not collateral. Painting a green "Best match" badge on
"Applecart Trailer #1" is the same false claim the auto-downloader makes, addressed to a
human instead of a worker. All eight results still render, stay selectable, and keep their
own Download button; what goes is both the badge and `queue/page.tsx`'s toolbar "Best
match" one-click action, which is gated on the same flag and disappears along with it on a
weak search.

The rejected alternatives, and why:

- **A separate `confident` flag** — leaves manual search untouched, but introduces two
  concepts, a wire-format addition and a frontend type change, to preserve a highlight that
  is wrong when it fires.
- **A floor inside each decision consumer** — no wire change, but duplicates the threshold
  across four sites. That duplication is what produced the "91 movies left" bug and the
  `/setup` path-mapping wipe.

## What happens on rejection

Nothing new is required — every path already exists:

- `AutoDownloadService` / `ShowAutoDownloadService` → log and back off for `NoMatchCooldown`
  (6h).
- `MoviesController.AutoDownload` → `422` with *"No suitable match found — please select
  manually."*
- Manual search → all eight results still listed, each keeping its own Download button;
  just no badge and no one-click Best-match button (see "Where it applies" above).

## Testing

**No test may touch the network.** YouTube's result set is not stable between runs —
observed directly: Barbie's top result was "Pink (Barbie Opening Theme)" at score 80 in one
probe and "I'm Just Ken" at 63 minutes later, same query. A live-network assertion would be
flaky by construction.

Scoring and the floor are therefore extracted as pure static functions over
`(videoTitle, channel, duration, title, year)` and tested against fixed fixtures. The real
measured cases become permanent regression tests:

| Fixture | Expected |
|---|---|
| `Applecart Trailer #1 (2017) - Movie Trailer` | rejected — no music evidence |
| `Hell Baby - Blazed Cable Guy` | rejected — no music evidence |
| `Bad Milo Official Red Band Trailer` | rejected — `official` alone is not evidence |
| `Severance - Official Intro Title Sequence` | **accepted** — show vocabulary |
| `The Nice Guys Theme \| The Nice Guys (Official Soundtrack)` | accepted |
| `Prisoners (2013) OST - Main Theme` | accepted |

Plus a test pinning that `official` alone does not satisfy the floor, one pinning that a
plain `trailer` is penalised, and one pinning the `ost` word-boundary rule — `Ghostbusters
(1984)` and `Lost in Translation` must **not** count as music evidence, while
`Prisoners (2013) OST` must.

## Accepted cost

*Whiplash* stops auto-downloading. Its best result is the bare title "Whiplash" with no
music evidence, and no rule can separate that from "Bad Milo" on the title alone.

This is the right trade. A false positive writes a trailer into `theme.mp3`, where it plays
in Plex and the user may never connect the bad audio to Themearr. A false negative leaves
the movie visibly in the pending queue with manual search one click away. The failure being
prevented is silent; the failure being introduced is visible.

## Out of scope

- **Wrong-work title matches.** *The Endless* → "Endless Space 2 Original Soundtrack" and
  *The Menu* → Stray's "Main Menu Theme" both survive this rule, because they are genuinely
  music — for the wrong work. That is weak title matching on short, common-word titles, a
  different bug needing its own issue.
- **Retuning the existing score weights** beyond adding the trailer penalties.
- **Re-examining themes already downloaded** under the old bar. Nothing rewrites existing
  files; the fix applies to future downloads only.
