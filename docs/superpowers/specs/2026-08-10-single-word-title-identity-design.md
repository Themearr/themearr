# Single-Word Title Identity — Design

**Date:** 2026-08-10
**Branch:** `fix/42-single-word-title-match`
**Issue:** [#42](https://github.com/Themearr/themearr/issues/42) (filed as a follow-up while shipping #39)

## Goal

Stop auto-download accepting a real piece of music for the wrong work when the media
title is short and made of common words. Two measured cases, both left open by design in
#39 (`docs/superpowers/specs/2026-08-09-auto-download-confidence-floor-design.md:153`):
*The Endless (2017)* picks "Endless Space 2 Original Soundtrack" and *The Menu (2022)*
picks Stray's "Main Menu Theme" — video-game music both times, and genuinely music both
times, so the music-evidence floor is blind to the mistake by construction.

## The bug

`ThemeMatch.Score`'s title block has two branches. A video title containing the full
media title earns +30 (`src/Themearr.API/Services/ThemeMatch.cs:177`); otherwise the
partial branch pays +8 per "significant" title word — longer than three letters — found
in the video title (`src/Themearr.API/Services/ThemeMatch.cs:185`).

Both measured failures share one shape: **the media title yields exactly one significant
word, and only that word matches**. "The Endless" reduces to `endless`, "The Menu" to
`menu` ("the" is under the length bar). One generic word appears in thousands of
unrelated uploads, so the wrong work's music clears the partial branch, adds real music
keywords and a plausible runtime, and lands comfortably positive:

| Video title | Points |
|---|---|
| "Endless Space 2 Original Soundtrack" vs *The Endless* | 8 partial + 12 soundtrack + 5 original + 15 duration = **40** |
| "Stray - Main Menu Theme" vs *The Menu* | 8 partial + 15 theme + 15 duration = **38** |

The confidence floor (`src/Themearr.API/Services/ThemeMatch.cs:144`) then asks three
questions — positive score, no promo marker, music evidence — and all three answer yes.
Nothing anywhere asks whether the music is for *this* work: the floor never sees the
media title at all, and `score > 0` is cleared by music keywords and duration alone
(12 + 5 + 15 = 32 with **zero** title overlap).

## The rule

`IsConfident` gains a fourth condition: the video title must **establish the work's
identity**.

- A **full-title match** always establishes it — judged by the same plain `Contains`
  the +30 branch uses (`src/Themearr.API/Services/ThemeMatch.cs:177`), so the scorer and
  the floor can never disagree about what "full" means.
- Without one, identity rests on the title's significant words, and a title with **at
  most one** of them has nothing left to be identified by. Partial match of a
  single-significant-word title → not confident.
- Titles with **two or more** significant words are deliberately untouched — today's
  behavior, verbatim. Both measured failures share the one-word shape, and the #39
  accept baseline (mainstream 11/12, obscure 6/12 all-correct rejections, shows 9/12)
  was measured under the current rule and cannot be re-measured: YouTube's ranking is
  not reproducible between runs, so any broader rule is an unmeasurable risk to the
  accept side.

The word rule is the scorer's own — split on spaces, keep words longer than three
letters (`src/Themearr.API/Services/ThemeMatch.cs:185`) — extracted into one shared
helper so the partial branch and the identity check cannot drift apart, the same move
`PromoMarkers` made for the penalty block and the veto.

`Score` is untouched. Ranking order is identical before and after; every pinned score
fixture keeps its number. Like #39, the fix lives in the floor, not the ranking — the
score orders candidates against each other, and ordering is not the broken part.

### Rejected alternatives

- **Scale the partial bonus by fraction of title words matched** (issue option 1): a
  one-word title matching its one word is 1/1 — the full bonus — so neither measured
  failure is killed. The failure is not over-generous arithmetic, it is that one common
  word is not identity.
- **Penalise wrong-medium signals** (`video game`, `gameplay`; issue option 3): neither
  measured title contains either phrase — "Endless Space 2 Original Soundtrack" and
  "Stray - Main Menu Theme" both read as ordinary music uploads. A medium blocklist is
  whack-a-mole, and `game` sits inside *endgame* and *games*, importing the exact
  short-word boundary trap `ost`/`intro` already had to solve.
- **Zero the partial bonus for one-word titles in `Score`**: does not fix the bug —
  "Endless Space 2 Original Soundtrack" still scores 32 without any title contribution
  and `IsConfident`'s `score > 0` gate still passes. Scoring alone cannot express
  "decline"; that is what #39 built the floor for.
- **Require identity for multi-word titles too** (≥1 significant word matched): coherent,
  but changes behavior for cases outside the measured failures against a baseline that
  cannot be re-measured. Smallest rule wins.
- **Word-boundary matching for the full title**: stricter than the +30 branch's plain
  `Contains`, so the floor and the scorer would disagree about what a full match is, and
  two-letter titles (*Up*) already rely on the loose form. Out of scope.

## Where it applies

The floor never sees the media title today, so the title is plumbed through as an
optional parameter at each hop, defaulting to null = no identity check — mirroring
`Score`, whose `title` is already nullable and skips the title block when absent
(`src/Themearr.API/Services/ThemeMatch.cs:174`):

- `ThemeMatch.IsConfident(score, videoTitle, mediaTitle = null)`
- `ThemeMatch.BestMatchIndex(ranked, mediaTitle = null)`
- `YoutubeService.RankAndMark(raw, title = null)` — forwards to `BestMatchIndex`
- `SearchAsync` forwards its existing `title` parameter to `RankAndMark`
  (`src/Themearr.API/Services/YoutubeService.cs:44`)

All five production search call sites already pass a title, so the check is live
everywhere: `MoviesController.cs:53`, `MoviesController.cs:142` (auto-download),
`ShowsController.cs:59`, `AutoDownloadService.cs:206`, `ShowAutoDownloadService.cs:188`.

Every `bestMatch` consumer reads the flag off the wire and inherits the change with no
code of its own — verified per consumer: `AutoDownloadService.cs:216`,
`ShowAutoDownloadService.cs:197`, `MoviesController.cs:149` (the 422 path),
`src/Themearr.Web/src/lib/media/adapter.ts:69` (show auto-download, composed
client-side), `src/Themearr.Web/src/app/queue/page.tsx:390` (the flag read feeding the toolbar one-click button at `queue/page.tsx:512`),
`queue/page.tsx:547` and `src/Themearr.Web/src/components/media/SearchModal.tsx:112`
(the "Best match" badge). Movies and shows share this single path, so both halves move
together. The wire shape is unchanged — same keys, same types; only how often
`bestMatch` is true changes.

## What happens on rejection

Identical to #39 — every decline path already exists and is already tested: both
auto-download workers back off `NoMatchCooldown` (6h), the movie endpoint returns 422,
manual search lists all results with their own Download buttons, minus the badge and the
one-click button.

## Testing

Fixtures only, never the live API — YouTube's ranking is not reproducible between runs
(measured: the same Barbie query scored 80, then 63, minutes apart).

| Fixture (video title vs media title) | Score | Confident before | Confident after |
|---|---|---|---|
| "Endless Space 2 Original Soundtrack" vs *The Endless* | 40 | **yes — the bug** | no |
| "Stray - Main Menu Theme" vs *The Menu* | 38 | **yes — the bug** | no |
| "Up - Married Life (Official Soundtrack)" vs *Up* | 75 | yes | yes |
| "Her - Official Soundtrack (Arcade Fire)" vs *Her* | 75 | yes | yes |
| "Dune Official Soundtrack \| Main Theme - Hans Zimmer" vs *Dune* | 95 | yes | yes |
| "Blade Runner - Main Theme" vs *Blade Runner 2049* (partial, multi-word) | 51 | yes | yes — deliberately untouched |

*Up*, *Her* and *Dune* are the issue's own too-strict canaries: legitimate short-titled
films whose correct uploads contain the full title, which is precisely why the rule keys
on full-title presence rather than title length alone. *Up* and *Her* have **zero**
significant words — the rule covers ≤1, not ==1, so they take the full-title door too.

The wiring is pinned through `RankAndMark`, not just `ThemeMatch` — #39's direct lesson:
reverting `SearchAsync`'s use of the floor once left all 429 tests green because every
test bound to `ThemeMatch` and none to its use. A `RankAndMark(raw, "The Endless")`
fixture with the wrong-work row on top must mark nothing, and a `RankAndMark(raw,
"Dune")` fixture with a full-match row must still mark row 0 — together they fail if the
title parameter stops flowing to the floor, while the `ThemeMatch` tests stay green,
which localizes a wiring revert. The one hop no offline test can pin is `SearchAsync`
itself forwarding `title` into `RankAndMark`: `SearchAsync` is the network half and
untestable by construction, the same epistemic status its call of `RankAndMark` has had
since #39.

Null back-compat is pinned deliberately: `IsConfident(40, "Endless Space 2 Original
Soundtrack")` with no media title stays true, documenting that the identity check only
exists where the caller supplies the title — which is why the wiring test matters.

## Accepted cost

A single-significant-word film or show whose best upload does not contain the full title
stops auto-downloading — e.g. a hypothetical "Endless (2017) - Ending Theme" would be
declined for *The Endless* because "the endless" is absent. Same trade #39 made with
*Whiplash*, same asymmetry: the false positive writes a video game's soundtrack into
`theme.mp3` where it plays silently wrong forever; the false negative leaves the row in
the pending queue with manual search one click away.

## Out of scope

- Multi-word titles with zero matched significant words (wrong-work music that shares no
  word at all). Real, but unmeasured — no documented failure has this shape, and the
  accept baseline cannot be re-measured against a broader rule.
- Retuning any score weight. Ranking is unchanged.
- Re-examining themes already downloaded under the old floor.
