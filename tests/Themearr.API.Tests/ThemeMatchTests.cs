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

    [Theory]
    // Five of the six measured false positives from issue #39. Each still scores above
    // zero — that is the point: the score ranks candidates, it does not judge them. The
    // sixth, The Endless ("Endless Space 2 Original Soundtrack"), is deliberately not
    // here: it is genuinely music, just for the wrong work, which the design doc calls out
    // as a different bug (weak title matching on short, common-word titles), not this one.
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
        // The caller's answer to -1 is a 6h backoff, which is the correct outcome.
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

    [Fact]
    public void IsConfident_filmTitleIsItselfAMusicWord_theScore_trailerStillVetoed()
    {
        // "The Score" is the film's own title. Its trailer's title match (+30) plus the
        // literal word "score" inside that title (+8, a MusicPhrase) reaches a positive
        // score with music evidence — issue #39's exact failure, a trailer written to
        // theme.mp3 for the *right* film, because the film's own name happens to be a
        // music word. The "trailer" promo marker must veto this regardless.
        var score = ThemeMatch.Score(
            "The Score (2001) Official Trailer", "Movieclips Trailers",
            TimeSpan.FromMinutes(2), "The Score", 2001);

        Assert.Equal(38, score);
        Assert.False(ThemeMatch.IsConfident(score, "The Score (2001) Official Trailer"));
    }

    [Fact]
    public void IsConfident_filmTitleIsItselfAMusicWord_suiteFrancaise_trailerStillVetoed()
    {
        // Same failure mode as "The Score", via a different MusicPhrase: "Suite" is both
        // the film's own title and, everywhere else, evidence the video is a musical
        // suite. HasMusicEvidence alone would certify this trailer as music.
        var score = ThemeMatch.Score(
            "Suite Francaise Official Trailer #1 (2015)", "Movieclips Trailers",
            TimeSpan.FromMinutes(2), "Suite Francaise", 2015);

        Assert.Equal(30, score);
        Assert.False(ThemeMatch.IsConfident(score, "Suite Francaise Official Trailer #1 (2015)"));
    }

    [Fact]
    public void Score_wrongWorkMusic_singleWordPartialMatch_stillRanksPositive()
    {
        // Issue #42's two measured failures, at the ranking layer. The ranking is not the
        // broken part and does not change: a genuine music upload SHOULD outrank the
        // trailers and reactions in its own pool. Identity is the floor's question, and
        // the floor is what declines these — see the IsConfident theory below.

        // 8 partial ("endless") + 12 soundtrack + 5 original + 15 duration.
        var endless = ThemeMatch.Score(
            "Endless Space 2 Original Soundtrack", "Amplitude Studios",
            TimeSpan.FromMinutes(4), "The Endless", 2017);
        Assert.Equal(40, endless);

        // 8 partial ("menu") + 15 theme + 15 duration.
        var menu = ThemeMatch.Score(
            "Stray - Main Menu Theme", "Annapurna Interactive",
            TimeSpan.FromMinutes(2.5), "The Menu", 2022);
        Assert.Equal(38, menu);
    }

    [Theory]
    // 30 full title + 10 official + 12 soundtrack + 15 duration + 8 records channel.
    [InlineData("Up - Married Life (Official Soundtrack)", "Walt Disney Records", 4.0,
        "Up", 2009, 75)]
    // 30 full title + 10 official + 12 soundtrack + 15 duration + 8 music channel.
    [InlineData("Her - Official Soundtrack (Arcade Fire)", "WaterTower Music", 3.0,
        "Her", 2013, 75)]
    // 30 full title + 20 main theme + 10 official + 12 soundtrack + 15 duration + 8 music channel.
    [InlineData("Dune Official Soundtrack | Main Theme - Hans Zimmer", "WaterTower Music", 3.75,
        "Dune", 2021, 95)]
    // 16 partial (blade, runner) + 20 main theme + 15 duration — no full match, no channel bonus.
    [InlineData("Blade Runner - Main Theme", "Some Channel", 3.0,
        "Blade Runner 2049", null, 51)]
    public void Score_shortAndPartialTitledFilms_pinsTheNumbersTheFloorTheoryUses(
        string videoTitle, string channel, double minutes, string title, int? year, int expected)
    {
        // These are the issue's too-strict canaries (Up, Her, Dune) plus a multi-word
        // partial match. Pinned so the IsConfident theory below asserts against measured
        // scores, not hand-waved ones.
        var score = ThemeMatch.Score(videoTitle, channel, TimeSpan.FromMinutes(minutes), title, year);

        Assert.Equal(expected, score);
    }

    [Theory]
    // Issue #42's two measured failures. Both are genuinely music — #39's evidence floor
    // passes them by design — and both matched exactly one significant title word
    // partially ("endless", "menu"; "the" is under the length bar). One generic word is
    // not identity, so neither may be acted on without a human looking.
    [InlineData(40, "Endless Space 2 Original Soundtrack", "The Endless", false)]
    [InlineData(38, "Stray - Main Menu Theme", "The Menu", false)]
    // The too-strict canaries: legitimate short titles whose correct uploads contain the
    // FULL title, which is the door the rule keys on. "Up" and "Her" have zero
    // significant words, so the rule must cover "at most one", not "exactly one".
    [InlineData(75, "Up - Married Life (Official Soundtrack)", "Up", true)]
    [InlineData(75, "Her - Official Soundtrack (Arcade Fire)", "Her", true)]
    [InlineData(95, "Dune Official Soundtrack | Main Theme - Hans Zimmer", "Dune", true)]
    // Multi-word titles are deliberately untouched: "blade" and "runner" matching
    // partially remains sufficient identity, because the #39 accept baseline (mainstream
    // 11/12, shows 9/12) was measured under that behavior and cannot be re-measured.
    [InlineData(51, "Blade Runner - Main Theme", "Blade Runner 2049", true)]
    public void IsConfident_singleSignificantWordTitle_requiresTheFullTitleToAppear(
        int score, string videoTitle, string mediaTitle, bool expected)
    {
        Assert.Equal(expected, ThemeMatch.IsConfident(score, videoTitle, mediaTitle));
    }

    [Fact]
    public void IsConfident_nullMediaTitle_skipsTheIdentityCheck()
    {
        // No media title supplied → the pre-#42 floor, mirroring Score's nullable title.
        // All five production search call sites do pass one; that this parameter keeps
        // flowing is pinned through RankAndMark, because a test bound only to ThemeMatch
        // cannot see the title stop flowing (#39's exact lesson).
        Assert.True(ThemeMatch.IsConfident(40, "Endless Space 2 Original Soundtrack"));
    }

    [Fact]
    public void BestMatchIndex_wrongWorkMusicOnTop_declines()
    {
        // Row 0 or nothing is unchanged by #42: the wrong-work row is refused outright,
        // not skipped over in favor of a lower-ranked result.
        var ranked = new[]
        {
            ("Endless Space 2 Original Soundtrack", 40),
            ("The Endless (2017) - Official Trailer", 20),
        };

        Assert.Equal(-1, ThemeMatch.BestMatchIndex(ranked, "The Endless"));
    }
}
