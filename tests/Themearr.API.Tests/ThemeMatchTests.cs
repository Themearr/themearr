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
