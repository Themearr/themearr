using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins that each result's duration flows from <see cref="YoutubeService.RankAndMark"/>
/// through <see cref="ThemeMatch.BestMatchIndex"/> into the confidence floor's hard
/// duration ceiling (issue #47). This wiring pin is the load-bearing half: a test bound
/// only to ThemeMatch stays green if the plumbing is severed — reverting the
/// <c>bestMatch</c> wiring once left all 429 tests green (#39's exact lesson), and #42's
/// title threading was pinned the same way. Fixtures are deliberately out of score order
/// so a mark-before-sort implementation fails loudly, like the pre-existing RankAndMark
/// tests. SearchAsync's own forward of <c>video.Duration</c> is the one hop no offline
/// test can pin — the same epistemic status the title forward has had since #42.
/// </summary>
public class YoutubeServiceRankAndMarkDurationTests
{
    private static (Dictionary<string, object?> result, int score, string videoTitle, TimeSpan? duration) Row(
        string videoTitle, int score, TimeSpan? duration)
        => (new Dictionary<string, object?>
        {
            ["title"] = videoTitle,
            ["score"] = 0,
            ["bestMatch"] = false,
        }, score, videoTitle, duration);

    [Fact]
    public void RankAndMark_correctWorkFullSoundtrackOutscoresTheField_nothingIsMarked()
    {
        // The issue's exact shape: genuinely music (#39 passes), genuinely this work
        // (#42 passes), and it outranks its own pool — 22 (30 title + 12 soundtrack
        // − 20 soft duration) beats the trailer's 20. Input order is reversed so the
        // soundtrack must win the sort BEFORE the floor judges it; only then does this
        // fixture prove the ceiling was consulted with the winner's duration.
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle, TimeSpan? duration)>
        {
            Row("Interstellar (2014) - Official Trailer", 20, TimeSpan.FromMinutes(2)),
            Row("Interstellar - Complete Soundtrack", 22, TimeSpan.FromHours(2)),
        };

        var results = YoutubeService.RankAndMark(raw, "Interstellar");

        Assert.Equal("Interstellar - Complete Soundtrack", results[0]["title"]);
        Assert.All(results, r => Assert.False((bool)r["bestMatch"]!));
    }

    [Fact]
    public void RankAndMark_normalLengthThemeOnTop_durationSupplied_isStillMarked()
    {
        // The positive control: carrying durations must not over-reject a 3–6 min theme,
        // the measured legitimate distribution.
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle, TimeSpan? duration)>
        {
            Row("Interstellar (2014) - Official Trailer", 20, TimeSpan.FromMinutes(2)),
            Row("Interstellar Official Soundtrack | Main Theme - Hans Zimmer", 95, TimeSpan.FromMinutes(4)),
        };

        var results = YoutubeService.RankAndMark(raw, "Interstellar");

        Assert.True((bool)results[0]["bestMatch"]!);
        Assert.False((bool)results[1]["bestMatch"]!);
    }

    [Fact]
    public void RankAndMark_nullDurations_keepThePreCeilingBehavior()
    {
        // YouTube sometimes reports no duration. Absence of the input must not change
        // the pre-#47 answer — mirroring how a null media title keeps the pre-#42 floor.
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle, TimeSpan? duration)>
        {
            Row("Some Film - Official Trailer", 20, null),
            Row("Some Film Theme (Official Soundtrack)", 82, null),
        };

        var results = YoutubeService.RankAndMark(raw);

        Assert.True((bool)results[0]["bestMatch"]!);
        Assert.False((bool)results[1]["bestMatch"]!);
    }
}
