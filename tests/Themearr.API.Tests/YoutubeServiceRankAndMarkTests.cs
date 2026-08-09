using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins <see cref="YoutubeService.RankAndMark"/> — the pure tail of <c>SearchAsync</c>
/// that sorts results, applies the confidence floor via <see cref="ThemeMatch.BestMatchIndex"/>,
/// and writes scores back onto the wire dictionaries. Before this was extracted, nothing
/// bound SearchAsync's actual *use* of ThemeMatch: every test targeted ThemeMatch directly,
/// so a regression that judged the floor on the wrong ordering (e.g. before the sort
/// instead of after) would still pass the full suite. These fixtures are deliberately
/// constructed out of score order so a mark-before-sort implementation fails loudly.
/// </summary>
public class YoutubeServiceRankAndMarkTests
{
    // A minimal wire-shaped dictionary — only "title" and "score" are asserted on below,
    // but real callers also carry videoId/thumbnail/duration/channel, which RankAndMark
    // never reads, so they are omitted here.
    private static (Dictionary<string, object?> result, int score, string videoTitle) Row(
        string videoTitle, int score)
        => (new Dictionary<string, object?>
        {
            ["title"] = videoTitle,
            ["score"] = 0,
            ["bestMatch"] = false,
        }, score, videoTitle);

    [Fact]
    public void RankAndMark_sortsResultsByScoreDescending()
    {
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle)>
        {
            Row("Low", 10),
            Row("High", 90),
            Row("Mid", 50),
        };

        var results = YoutubeService.RankAndMark(raw);

        Assert.Equal(new[] { "High", "Mid", "Low" }, results.Select(r => (string)r["title"]!));
    }

    [Fact]
    public void RankAndMark_marksTheTopRankedResultAfterSorting_notTheInputOrder()
    {
        // Input order is deliberately the opposite of score order: the trailer is row 0
        // going in, but the soundtrack outranks it. A mark-before-sort bug would flag the
        // trailer (input row 0) instead of the actual winner, and this fixture is built so
        // that mistake is visible in the assertions below rather than passing by accident.
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle)>
        {
            Row("Some Film - Official Trailer", 20),
            Row("Some Film Theme (Official Soundtrack)", 82),
        };

        var results = YoutubeService.RankAndMark(raw);

        Assert.Equal("Some Film Theme (Official Soundtrack)", results[0]["title"]);
        Assert.True((bool)results[0]["bestMatch"]!);
        Assert.False((bool)results[1]["bestMatch"]!);
    }

    [Fact]
    public void RankAndMark_topResultFailsTheFloor_nothingIsMarked()
    {
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle)>
        {
            Row("Some Film Trailer #1", 45),
            Row("Some Film REACTION", 5),
        };

        var results = YoutubeService.RankAndMark(raw);

        Assert.All(results, r => Assert.False((bool)r["bestMatch"]!));
    }

    [Fact]
    public void RankAndMark_atMostOneResultIsEverMarked()
    {
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle)>
        {
            Row("Some Film Theme (Official Soundtrack)", 82),
            Row("Some Film OST", 60),
            Row("Some Film - Main Theme (Cover)", 40),
        };

        var results = YoutubeService.RankAndMark(raw);

        Assert.Equal(1, results.Count(r => (bool)r["bestMatch"]!));
    }

    [Fact]
    public void RankAndMark_writesEachResultsScoreBackAfterSorting()
    {
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle)>
        {
            Row("Low", 10),
            Row("High", 90),
            Row("Mid", 50),
        };

        var results = YoutubeService.RankAndMark(raw);

        Assert.Equal(90, results[0]["score"]);
        Assert.Equal(50, results[1]["score"]);
        Assert.Equal(10, results[2]["score"]);
    }
}
