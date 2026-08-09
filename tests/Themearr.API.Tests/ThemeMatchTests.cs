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
}
