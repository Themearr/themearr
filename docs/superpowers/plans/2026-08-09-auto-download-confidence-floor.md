# Auto-Download Confidence Floor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop auto-download writing trailers, clips and promos into `theme.mp3` by requiring the top-ranked YouTube result to carry positive evidence that it is music, not merely to out-rank a bad pool.

**Architecture:** The scoring function and the new confidence floor move out of `YoutubeService` into a new pure static class `ThemeMatch`, because `YoutubeService.SearchAsync` cannot be tested — it hits the live YouTube API, whose result set is not stable between runs. `SearchAsync` keeps its network loop and delegates every judgement to `ThemeMatch`. No consumer changes: all four decision consumers (`AutoDownloadService`, `ShowAutoDownloadService`, `MoviesController.AutoDownload`, `lib/media/adapter.ts`) and both hint consumers (`app/queue/page.tsx`, `components/media/SearchModal.tsx`) read `bestMatch` off the wire and inherit the tightening automatically.

**Tech Stack:** .NET 10, xUnit, YoutubeExplode.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-08-09-auto-download-confidence-floor-design.md`. Read it before starting. Issue [#39](https://github.com/Themearr/themearr/issues/39).
- **No test may touch the network.** YouTube's ranking is not reproducible — the same Barbie query returned "Pink (Barbie Opening Theme)" at score 80 in one probe and "I'm Just Ken" at 63 minutes later. Every test in this plan is a pure function over a fixed fixture.
- **The fixtures below are constructed**, modelled on the measured cases in the issue. Their expected scores are computed from the scoring table in this plan, *not* copied from the live probe — the issue's reported score for a case may differ because the real video title and channel differ from the fixture. Assert what the plan states.
- **`bestMatch` is row 0 or nothing.** If the top-ranked result fails the floor, nothing is marked, even when a lower-ranked result would pass. Decided deliberately: the 12-film measurement that validated this rule was taken under these semantics, and re-measuring is expensive and non-reproducible. Task 4 pins this with a test.
- **`"official"` must never satisfy the floor.** Trailers are titled "Official Trailer"; treating it as evidence is what let *Sun Choke* and *Bad Milo* through. It stays a general `+10` scoring bonus only.
- **Never edit an existing test to make a change pass** (CLAUDE.md). Task 1 is a behaviour-preserving refactor; if it breaks a test, the refactor is wrong.
- **Comments explain _why_, not _what_.** Match the density of `HostGuard.cs` / `PollBackoff.cs`.
- Full gate before the final commit: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo` (389 pre-existing tests must still pass; the new ones are additive).

## File Structure

| File | Responsibility |
|---|---|
| `src/Themearr.API/Services/ThemeMatch.cs` | **New.** Pure ranking + confidence policy. `Score`, `HasMusicEvidence`, `IsConfident`, `BestMatchIndex`. No I/O, no state. |
| `src/Themearr.API/Services/YoutubeService.cs` | **Modified.** Keeps the YouTube search loop and result-dictionary shape; delegates all judgement to `ThemeMatch`. |
| `tests/Themearr.API.Tests/ThemeMatchTests.cs` | **New.** All tests for the above. |

No frontend change. No consumer change. `src/Themearr.Web/src/app/queue-race.test.tsx` uses a `bestMatch: false` fixture and is unaffected.

---

### Task 1: Extract scoring into `ThemeMatch` (behaviour-preserving)

Move `YoutubeService.Score` verbatim into a new public static class so it can be tested at all. **No scoring change in this task** — characterization tests first, so Tasks 2–4 have a green baseline to change against.

**Files:**
- Create: `src/Themearr.API/Services/ThemeMatch.cs`
- Create: `tests/Themearr.API.Tests/ThemeMatchTests.cs`
- Modify: `src/Themearr.API/Services/YoutubeService.cs:35` (call site), `:56-119` (delete the private method)

**Interfaces:**
- Produces: `public static int ThemeMatch.Score(string videoTitle, string channel, TimeSpan? duration, string? title, int? year)` — identical signature to the private method it replaces. `year` is accepted but unused; that is pre-existing and stays, so the call site does not change shape.

- [ ] **Step 1: Write the failing characterization tests**

Create `tests/Themearr.API.Tests/ThemeMatchTests.cs`:

```csharp
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the ranking score and the confidence floor that decides whether auto-download
/// may act on a search result without a human looking at it (issue #39). Every case is a
/// constructed fixture: YouTube's ranking is not reproducible between runs, so a
/// live-network assertion would be flaky by construction.
/// </summary>
public class ThemeMatchTests
{
    [Fact]
    public void Score_promoClipWithExactTitleAndIdealRuntime_reaches45WithNoMusicSignal()
    {
        // The bug in one number: title match (+30) plus a 1-6 min runtime (+15) reaches 45
        // before anything establishes the video is music, and 45 cleared the old `> 0` gate.
        // This fixture stays at 45 through every later task — no word in it is penalised,
        // so only the confidence floor of Task 4 can reject it. That is the point: the
        // penalties are not what catches this case.
        var score = ThemeMatch.Score(
            "Hell Baby - Blazed Cable Guy", "Comedy Central",
            TimeSpan.FromMinutes(2), "Hell Baby", 2013);

        Assert.Equal(45, score);
    }

    [Fact]
    public void Score_soundtrackUpload_outranksTheTrailer()
    {
        // 30 title + 15 theme + 10 official + 12 soundtrack + 15 duration.
        var score = ThemeMatch.Score(
            "The Nice Guys Theme | The Nice Guys (Official Soundtrack)", "Various Artists",
            TimeSpan.FromMinutes(3), "The Nice Guys", 2016);

        Assert.Equal(82, score);
    }

    [Fact]
    public void Score_reactionVideo_isPushedNegative()
    {
        // 30 title + 15 duration - 40 reaction.
        var score = ThemeMatch.Score(
            "Hell Baby REACTION", "Some Channel",
            TimeSpan.FromMinutes(2), "Hell Baby", 2013);

        Assert.Equal(5, score);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter "FullyQualifiedName~ThemeMatchTests"`

Expected: FAIL to build — `error CS0103: The name 'ThemeMatch' does not exist in the current context`.

- [ ] **Step 3: Create `ThemeMatch` with the scoring logic moved verbatim**

Create `src/Themearr.API/Services/ThemeMatch.cs`:

```csharp
namespace Themearr.API.Services;

/// <summary>
/// Ranks YouTube search results against a media title, and decides whether the top-ranked
/// one is good enough to act on with no human looking at it.
///
/// Deliberately separate from <see cref="YoutubeService"/>, which cannot be tested: it
/// calls the live YouTube API, whose ranking is not stable between runs — the same query
/// returned "Pink (Barbie Opening Theme)" at 80 in one probe and "I'm Just Ken" at 63
/// minutes later. Keeping the judgement pure is what makes it assertable.
/// </summary>
public static class ThemeMatch
{
    /// <param name="year">Accepted for caller symmetry; the weights do not use it today.</param>
    public static int Score(string videoTitle, string channel, TimeSpan? duration,
        string? title, int? year)
    {
        int score = 0;
        var vt = videoTitle.ToLowerInvariant();
        var ch = channel.ToLowerInvariant();

        // ── Title match ───────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(title))
        {
            var mt = title.ToLowerInvariant();
            if (vt.Contains(mt))
                score += 30;
            else
            {
                // Partial: count significant words that appear in the video title
                var words = mt.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                              .Where(w => w.Length > 3);
                score += words.Count(w => vt.Contains(w)) * 8;
            }
        }

        // ── Good keywords ─────────────────────────────────────────────────────
        if (vt.Contains("main theme"))      score += 20;
        else if (vt.Contains("theme"))      score += 15;
        if (vt.Contains("official"))        score += 10;
        if (vt.Contains("soundtrack"))      score += 12;
        if (vt.Contains(" ost"))            score += 12;
        if (vt.Contains("original score"))  score += 12;
        if (vt.Contains("score"))           score +=  8;
        if (vt.Contains("original"))        score +=  5;

        // ── Duration scoring (ideal 1–6 minutes) ─────────────────────────────
        if (duration.HasValue)
        {
            var mins = duration.Value.TotalMinutes;
            if      (mins >= 1.0 && mins <= 6.0)  score += 15;
            else if (mins >= 0.5 && mins <= 10.0) score +=  8;
            else if (mins < 0.5 || mins > 15.0)   score -= 20;
        }

        // ── Channel signals ───────────────────────────────────────────────────
        if (ch.Contains("music")      || ch.Contains("records") ||
            ch.Contains("soundtrack") || ch.Contains("score")   ||
            ch.Contains("film")       || ch.Contains("cinema"))
            score += 8;

        // ── Negative signals ──────────────────────────────────────────────────
        if (vt.Contains("top 10") || vt.Contains("top10")) score -= 40;
        if (vt.Contains("compilation"))                     score -= 30;
        if (vt.Contains("reaction"))                        score -= 40;
        if (vt.Contains("ranked"))                          score -= 30;
        if (vt.Contains("every "))                          score -= 20;
        if (vt.Contains("all ") && vt.Contains("theme"))    score -= 20;
        if (vt.Contains("tribute"))                         score -= 20;
        if (vt.Contains("parody"))                          score -= 40;
        if (vt.Contains("cover"))                           score -= 15;
        if (vt.Contains("remix"))                           score -= 10;
        if (vt.Contains("piano version") || vt.Contains("piano cover")) score -= 15;
        if (vt.Contains("guitar"))                          score -= 10;
        if (vt.Contains("trailer music") || vt.Contains("trailer theme")) score -= 10;

        return score;
    }
}
```

- [ ] **Step 4: Point `YoutubeService` at it and delete the private copy**

In `src/Themearr.API/Services/YoutubeService.cs`, change the call site on line 35 from:

```csharp
            var score = Score(video.Title, video.Author.ChannelTitle, video.Duration, title, year);
```

to:

```csharp
            var score = ThemeMatch.Score(video.Title, video.Author.ChannelTitle, video.Duration, title, year);
```

Then delete the entire `private static int Score(...)` method (lines 56–119 in the original file), leaving the class with only `SearchAsync`.

- [ ] **Step 5: Run the new tests and the full suite**

Run: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo`

Expected: PASS. All 389 pre-existing tests still pass — this task changed no behaviour. If any pre-existing test fails, the extraction dropped or reordered a rule; fix the extraction, do not touch the test.

- [ ] **Step 6: Commit**

```bash
git add src/Themearr.API/Services/ThemeMatch.cs src/Themearr.API/Services/YoutubeService.cs tests/Themearr.API.Tests/ThemeMatchTests.cs
git commit -m "refactor: extract YouTube result scoring into a testable ThemeMatch"
```

---

### Task 2: Music evidence with word-boundary matching

Add the positive-evidence test that the floor will use. This is the heart of the fix: a title must say something that means *music* before auto-download may act on it.

**Files:**
- Modify: `src/Themearr.API/Services/ThemeMatch.cs`
- Modify: `tests/Themearr.API.Tests/ThemeMatchTests.cs`

**Interfaces:**
- Consumes: `ThemeMatch.Score` from Task 1 (unchanged here).
- Produces: `public static bool ThemeMatch.HasMusicEvidence(string videoTitle)` — case-insensitive, title only. Task 4 calls it from `IsConfident`.

Two keywords need word-boundary matching rather than plain `Contains`:

| Keyword | Why a bare `Contains` is wrong |
|---|---|
| `ost` | substring of *gh**ost**busters*, *l**ost***, *m**ost***, *p**ost*** — "Ghostbusters (1984)" would certify itself as the film's score |
| `intro` | prefix of *intro**duction***, *intro**ducing*** — "Severance - Introducing the Cast" would certify itself as the show's theme |

The spec asserts every keyword other than `ost` is long enough for a plain substring match. That is not true of `intro`; it is the same bug class and gets the same guard.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Themearr.API.Tests/ThemeMatchTests.cs`, inside the `ThemeMatchTests` class:

```csharp
    [Theory]
    [InlineData("The Nice Guys Theme | The Nice Guys (Official Soundtrack)")]
    [InlineData("Prisoners (2013) OST - Main Theme")]
    [InlineData("Interstellar - Main Title")]
    [InlineData("Star Wars Suite")]
    [InlineData("The Phantom of the Opera - Overture")]
    // Show vocabulary: a series' theme is routinely labelled as its titles or intro
    // rather than as a soundtrack. Severance's correct result is this exact shape, and
    // ShowAutoDownloadService shares this code path — a movie-only list breaks shows.
    [InlineData("Severance - Official Intro Title Sequence")]
    [InlineData("Succession - Opening Credits")]
    [InlineData("Fleabag - End Credits Music")]
    public void HasMusicEvidence_musicTitles_areEvidence(string videoTitle)
    {
        Assert.True(ThemeMatch.HasMusicEvidence(videoTitle));
    }

    [Theory]
    [InlineData("Applecart Trailer #1 (2017) - Movie Trailer")]
    [InlineData("Hell Baby - Blazed Cable Guy")]
    [InlineData("Some Kind of Hate | RLJ Entertainment")]
    // "official" is a trailer's own vocabulary — it must not certify one as music.
    [InlineData("Bad Milo Official Red Band Trailer")]
    [InlineData("Sun Choke - Official Trailer (HD)")]
    public void HasMusicEvidence_trailersAndClips_areNotEvidence(string videoTitle)
    {
        Assert.False(ThemeMatch.HasMusicEvidence(videoTitle));
    }

    [Theory]
    // "ost" inside ghost/lost, "intro" inside introducing — a bare Contains would let
    // each of these certify itself as music on the strength of its own film title.
    [InlineData("Ghostbusters (1984)", false)]
    [InlineData("Ghost Rider", false)]
    [InlineData("Lost in Translation", false)]
    [InlineData("Severance - Introducing the Cast", false)]
    // ...while the real thing still counts, delimited by punctuation or a string edge.
    [InlineData("Prisoners (2013) OST", true)]
    [InlineData("Drive (2011) [OST]", true)]
    [InlineData("Severance Intro", true)]
    public void HasMusicEvidence_matchesShortKeywordsOnWordBoundaries(string videoTitle, bool expected)
    {
        Assert.Equal(expected, ThemeMatch.HasMusicEvidence(videoTitle));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter "FullyQualifiedName~HasMusicEvidence"`

Expected: FAIL to build — `error CS0117: 'ThemeMatch' does not contain a definition for 'HasMusicEvidence'`.

- [ ] **Step 3: Implement `HasMusicEvidence` and `ContainsWord`**

Add to `src/Themearr.API/Services/ThemeMatch.cs`, inside the class, above `Score`:

```csharp
    /// <summary>
    /// Words that mean "this is the work's music". Long enough that a plain substring
    /// match is safe. "official" is deliberately absent — trailers are titled "Official
    /// Trailer", and accepting it as evidence is exactly what let Sun Choke and Bad Milo
    /// through as themes.
    /// </summary>
    private static readonly string[] MusicPhrases =
    {
        "theme", "soundtrack", "score", "main title", "suite", "overture",
        // Show vocabulary. A series labels its theme as its titles or intro far more
        // often than as a soundtrack, and ShowAutoDownloadService shares this path.
        "title sequence", "opening credits", "end credits",
    };

    /// <summary>
    /// Evidence words short enough to hide inside an unrelated word: "ost" in ghost,
    /// lost, most and post; "intro" in introduction and introducing. Matched on word
    /// boundaries so "Ghostbusters (1984)" cannot certify itself as its own score.
    /// </summary>
    private static readonly string[] MusicWords = { "ost", "intro" };

    /// <summary>
    /// True when the video title positively claims to be the work's music. This is the
    /// question the ranking score was never built to answer, and answering it with the
    /// score is what wrote trailers into theme.mp3 (issue #39).
    /// </summary>
    public static bool HasMusicEvidence(string videoTitle)
    {
        var vt = videoTitle.ToLowerInvariant();
        return MusicPhrases.Any(p => vt.Contains(p, StringComparison.Ordinal))
            || MusicWords.Any(w => ContainsWord(vt, w));
    }

    /// <summary>
    /// True when <paramref name="word"/> appears delimited by a non-letter, or a string
    /// edge, on both sides. Both arguments must already be lowercase. Digits and
    /// punctuation count as delimiters, so "(2013) OST", "[OST]" and a title-final "OST"
    /// all match while "ghost" does not.
    /// </summary>
    private static bool ContainsWord(string haystack, string word)
    {
        for (var i = haystack.IndexOf(word, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            var startsWord = i == 0 || !char.IsLetter(haystack[i - 1]);
            var end = i + word.Length;
            var endsWord = end == haystack.Length || !char.IsLetter(haystack[end]);
            if (startsWord && endsWord) return true;
        }
        return false;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo`

Expected: PASS, including all 389 pre-existing tests. `HasMusicEvidence` is not wired into anything yet, so nothing else can change.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/ThemeMatch.cs tests/Themearr.API.Tests/ThemeMatchTests.cs
git commit -m "feat: add music-evidence test for YouTube results"
```

---

### Task 3: Penalise trailers, promos and clips

The scorer has no penalty for a plain `trailer` today — only for `trailer music` / `trailer theme`. Add the missing markers. This is a ranking change, separate from the floor: it demotes a trailer *among* candidates, while Task 4 stops it being downloaded at all.

**Files:**
- Modify: `src/Themearr.API/Services/ThemeMatch.cs` (the `Score` negative-signals block)
- Modify: `tests/Themearr.API.Tests/ThemeMatchTests.cs`

**Interfaces:** No signature change. `ThemeMatch.Score` returns lower values for the titles below.

| Marker | Penalty | Match form |
|---|---|---|
| `trailer` | −25 | plain substring |
| `featurette`, `behind the scenes`, `interview` | −25 each | plain substring |
| `clip`, `scene` | −20 each | **leading space** — `" clip"`, `" scene"` |

The leading space on `" clip"` and `" scene"` is the same guard the existing `" ost"` bonus uses: it matches "clip", "clips" and "scenes" without firing on *e**clip**se* (a real film title — *The Twilight Saga: Eclipse*) or *ob**scene***.

Penalties stack by design: "Trailer Music" takes both the new −25 and the existing −10, and "Featurette - Behind the Scenes" takes −25, −25 and −20. Trailer music is not the film's theme either way, so a heavier demotion is correct.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Themearr.API.Tests/ThemeMatchTests.cs`, inside the class:

```csharp
    [Fact]
    public void Score_plainTrailer_isPenalised()
    {
        // 30 title + 15 duration - 25 trailer. Scored 45 before this task, which cleared
        // the old `> 0` gate and got downloaded as Applecart's theme.
        var score = ThemeMatch.Score(
            "Applecart Trailer #1 (2017) - Movie Trailer", "Indie Rights",
            TimeSpan.FromMinutes(2), "Applecart", 2015);

        Assert.Equal(20, score);
    }

    [Fact]
    public void Score_featurette_isPenalised()
    {
        // 30 title + 15 duration - 25 featurette.
        var score = ThemeMatch.Score(
            "Sun Choke Featurette", "Some Channel",
            TimeSpan.FromMinutes(2), "Sun Choke", 2015);

        Assert.Equal(20, score);
    }

    [Fact]
    public void Score_clip_isPenalised()
    {
        // 30 title + 15 duration - 20 clip.
        var score = ThemeMatch.Score(
            "Hell Baby - Blazed Cable Guy Clip", "Some Channel",
            TimeSpan.FromMinutes(2), "Hell Baby", 2013);

        Assert.Equal(25, score);
    }

    [Fact]
    public void Score_filmWithClipInsideItsTitle_isNotPenalised()
    {
        // "eclipse" contains "clip". The leading-space guard is why this keeps its score:
        // 30 title + 20 main theme + 15 duration + 8 music channel, no penalty.
        var score = ThemeMatch.Score(
            "The Twilight Saga: Eclipse - Main Theme", "Summit Music",
            TimeSpan.FromMinutes(3), "Eclipse", 2010);

        Assert.Equal(73, score);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter "FullyQualifiedName~ThemeMatchTests"`

Expected: FAIL — `Score_plainTrailer_isPenalised` asserts 20 but gets 45; `Score_featurette_isPenalised` gets 45; `Score_clip_isPenalised` gets 45. `Score_filmWithClipInsideItsTitle_isNotPenalised` already passes.

- [ ] **Step 3: Add the penalties**

In `src/Themearr.API/Services/ThemeMatch.cs`, in the `── Negative signals ──` block of `Score`, immediately after the existing `trailer music` line, add:

```csharp
        // ── Trailers, promos and clips ────────────────────────────────────────
        // There was no penalty for a plain "trailer" at all: an exact title match (+30)
        // plus a 1-6 min runtime (+15) reached 45, and 45 cleared the old `> 0` gate, so
        // "<Film> Trailer #1" was downloaded as the theme (issue #39).
        if (vt.Contains("trailer"))           score -= 25;
        if (vt.Contains("featurette"))        score -= 25;
        if (vt.Contains("behind the scenes")) score -= 25;
        if (vt.Contains("interview"))         score -= 25;
        // Leading space matches "clip"/"clips"/"scenes" without firing on "eclipse" (a
        // real film title) or "obscene" — the same guard the " ost" bonus above uses.
        if (vt.Contains(" clip"))             score -= 20;
        if (vt.Contains(" scene"))            score -= 20;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo`

Expected: PASS, all 389 pre-existing tests included.

- [ ] **Step 5: Commit**

```bash
git add src/Themearr.API/Services/ThemeMatch.cs tests/Themearr.API.Tests/ThemeMatchTests.cs
git commit -m "feat: penalise trailers, featurettes and clips when ranking themes"
```

---

### Task 4: Apply the floor to `bestMatch`

Wire the floor into the one place `bestMatch` is set. This is the behaviour change the issue asks for.

**Files:**
- Modify: `src/Themearr.API/Services/ThemeMatch.cs`
- Modify: `src/Themearr.API/Services/YoutubeService.cs:12` (tuple shape), `:36` (add), `:44-46` (the decision)
- Modify: `tests/Themearr.API.Tests/ThemeMatchTests.cs`

**Interfaces:**
- Consumes: `ThemeMatch.HasMusicEvidence` (Task 2), `ThemeMatch.Score` (Tasks 1, 3).
- Produces:
  - `public static bool ThemeMatch.IsConfident(int score, string videoTitle)`
  - `public static int ThemeMatch.BestMatchIndex(IReadOnlyList<(string VideoTitle, int Score)> ranked)` — returns `0` or `-1`, never any other index.

`BestMatchIndex` exists so the row-0-or-nothing rule is testable. Inlining `IsConfident` into `SearchAsync` would leave that rule unasserted, and `SearchAsync` cannot be tested — it hits the network. The obvious alternative implementation ("scan down for the first result that passes") was considered and rejected: the 12-film measurement validating this rule was taken under row-0 semantics, and YouTube results are not reproducible, so it cannot be re-validated cheaply.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Themearr.API.Tests/ThemeMatchTests.cs`, inside the class:

```csharp
    [Theory]
    // The six measured false positives from issue #39. Each still scores above zero —
    // that is the point: the score ranks candidates, it does not judge them.
    [InlineData(20, "Applecart Trailer #1 (2017) - Movie Trailer", false)]
    [InlineData(45, "Hell Baby - Blazed Cable Guy", false)]
    [InlineData(45, "Some Kind of Hate | RLJ Entertainment", false)]
    [InlineData(30, "Bad Milo Official Red Band Trailer", false)]
    [InlineData(30, "Sun Choke - Official Trailer (HD)", false)]
    // ...and the results that must keep working.
    [InlineData(82, "The Nice Guys Theme | The Nice Guys (Official Soundtrack)", true)]
    [InlineData(77, "Prisoners (2013) OST - Main Theme", true)]
    [InlineData(55, "Severance - Official Intro Title Sequence", true)]
    public void IsConfident_requiresMusicEvidenceOnTopOfAPositiveScore(
        int score, string videoTitle, bool expected)
    {
        Assert.Equal(expected, ThemeMatch.IsConfident(score, videoTitle));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void IsConfident_keepsTheExistingScoreGate(int score)
    {
        // Music evidence is added to the old bar, not substituted for it.
        Assert.False(ThemeMatch.IsConfident(score, "Some Film - Main Theme"));
    }

    [Fact]
    public void BestMatchIndex_topResultIsMusic_marksIt()
    {
        var ranked = new[]
        {
            ("The Nice Guys Theme (Official Soundtrack)", 82),
            ("The Nice Guys - Official Trailer", 30),
        };

        Assert.Equal(0, ThemeMatch.BestMatchIndex(ranked));
    }

    [Fact]
    public void BestMatchIndex_topResultIsATrailer_declinesRatherThanScanningDown()
    {
        // Row 0 or nothing. A lower-ranked result that would pass the floor is NOT
        // promoted: the ranking already said it was the weaker candidate, and the
        // measurement that validated this rule was taken under these semantics.
        // The caller's answer to -1 is a 24h backoff, which is the correct outcome.
        var ranked = new[]
        {
            ("Applecart Trailer #1 (2017) - Movie Trailer", 20),
            ("Applecart - Main Theme", 18),
        };

        Assert.Equal(-1, ThemeMatch.BestMatchIndex(ranked));
    }

    [Fact]
    public void BestMatchIndex_noResults_declines()
    {
        Assert.Equal(-1, ThemeMatch.BestMatchIndex(Array.Empty<(string, int)>()));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo --filter "FullyQualifiedName~ThemeMatchTests"`

Expected: FAIL to build — `'ThemeMatch' does not contain a definition for 'IsConfident'` and `... for 'BestMatchIndex'`.

- [ ] **Step 3: Implement the floor**

Add to `src/Themearr.API/Services/ThemeMatch.cs`, inside the class, after `HasMusicEvidence`:

```csharp
    /// <summary>
    /// Whether a ranked result may be acted on with no human looking at it: it must both
    /// out-rank the field and say it is music. The score alone was never a quality bar —
    /// it exists to order candidates against each other, so "best of a bad pool" and
    /// "good" were the same answer, and low-profile films got trailers.
    /// </summary>
    public static bool IsConfident(int score, string videoTitle)
        => score > 0 && HasMusicEvidence(videoTitle);

    /// <summary>
    /// Index of the result to mark as the best match, or -1 for none. Row 0 or nothing:
    /// a lower-ranked result is never promoted just because it clears the floor, because
    /// the ranking already judged it the weaker candidate. Declining is a supported
    /// outcome everywhere — both auto-download workers back off 24h and the movie
    /// endpoint returns 422.
    /// </summary>
    public static int BestMatchIndex(IReadOnlyList<(string VideoTitle, int Score)> ranked)
        => ranked.Count > 0 && IsConfident(ranked[0].Score, ranked[0].VideoTitle) ? 0 : -1;
```

- [ ] **Step 4: Wire it into `SearchAsync`**

In `src/Themearr.API/Services/YoutubeService.cs`, carry the video title alongside the result so the decision does not have to dig it back out of the dictionary.

Change line 12 from:

```csharp
        var raw = new List<(Dictionary<string, object?> result, int score)>();
```

to:

```csharp
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle)>();
```

Change line 36 from:

```csharp
            raw.Add((result, score));
```

to:

```csharp
            raw.Add((result, score, video.Title));
```

Replace lines 44–46:

```csharp
        // Mark the top result as bestMatch (only if it has a positive score)
        if (raw.Count > 0 && raw[0].score > 0)
            raw[0].result["bestMatch"] = true;
```

with:

```csharp
        // Mark the top result as bestMatch — only when it is plausibly the work's music,
        // not merely the least-bad of a poor pool. Both auto-download workers and the
        // manual search badge read this flag, and all of them should decline together.
        var best = ThemeMatch.BestMatchIndex(
            raw.Select(r => (r.videoTitle, r.score)).ToList());
        if (best >= 0)
            raw[best].result["bestMatch"] = true;
```

- [ ] **Step 5: Run the full backend suite**

Run: `dotnet test tests/Themearr.API.Tests/Themearr.API.Tests.csproj --nologo`

Expected: PASS. All 389 pre-existing tests still pass — no consumer signature changed and the wire shape is identical.

- [ ] **Step 6: Run the rest of the gate**

The change is backend-only, but the frontend and E2E suites gate the release, so confirm they are untouched rather than assuming it.

```bash
cd src/Themearr.Web && npm test && npx tsc --noEmit && npm run lint && npm run build
cd ../../tests/e2e && npm test
```

Expected: vitest 77 passing; `tsc` clean; eslint at the 0 errors / 3 warnings baseline; E2E 10 passing.

- [ ] **Step 7: Commit**

```bash
git add src/Themearr.API/Services/ThemeMatch.cs src/Themearr.API/Services/YoutubeService.cs tests/Themearr.API.Tests/ThemeMatchTests.cs
git commit -m "fix: require music evidence before auto-downloading a theme (#39)"
```

---

## Verification against the issue

After Task 4, each measured false positive from issue #39 is rejected by a specific rule. Confirm the reasoning holds when reviewing:

| Case | Why it is now rejected |
|---|---|
| *Applecart* → "Trailer #1" | no music evidence; also −25 trailer |
| *Some Kind of Hate* → "RLJ Entertainment" | no music evidence |
| *Hell Baby* → "Blazed Cable Guy" | no music evidence (unpenalised — the floor, not the penalty, catches this one) |
| *Sun Choke* → "Official Trailer (HD)" | `official` is not evidence; −25 trailer |
| *Bad Milo* → "Official Red Band Trailer" | `official` is not evidence; −25 trailer |
| *Severance* → "Official Intro Title Sequence" | **accepted** — show vocabulary, via both `intro` and `title sequence` |

Out of scope, as the spec states, and expected to remain broken: *The Endless* → "Endless Space 2 Original Soundtrack" and *The Menu* → "Main Menu Theme" both carry genuine music evidence for the wrong work. That is weak title matching on short titles and needs its own issue. *Whiplash* is the accepted false negative — its best result is the bare title with no music evidence.

## Follow-up

Open a new issue for the wrong-work title matches (*The Endless*, *The Menu*) before closing #39, so the known gap is tracked rather than lost.
