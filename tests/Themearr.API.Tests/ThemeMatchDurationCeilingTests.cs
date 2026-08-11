using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the hard duration ceiling in the confidence floor (issue #47). Every earlier
/// guard asks "is this music, for this film?" — none asks "is this a theme or three
/// hours of one?". A full-soundtrack upload of the CORRECT work passes #39's music
/// evidence and #42's identity rule, absorbs the −20 soft duration penalty, and was
/// auto-downloaded. The ceiling is 15 minutes because that is the boundary the scorer
/// already calls pathological (Score's <c>mins &gt; 15.0</c> penalty band): acceptance
/// now binds where ranking already judged, with 2.5× margin over the longest measured
/// legitimate theme (3–6 min) and far under any "complete score" upload. The −20 soft
/// score is deliberately untouched — ranking and acceptance answer different questions,
/// which the first test here demonstrates in one number.
/// </summary>
public class ThemeMatchDurationCeilingTests
{
    [Fact]
    public void Score_fullSoundtrackOfTheCorrectWork_staysPositive_theSoftPenaltyIsNotAGate()
    {
        // 30 full title + 12 soundtrack − 20 over-duration = 22. Still positive, still
        // music evidence, still the right work — so before #47 IsConfident accepted it.
        // The issue's production evidence is this exact shape: a 3h33m file auto-attached
        // as a "theme". The score MUST stay 22 (ranking is not the broken part); only the
        // floor below refuses it.
        var score = ThemeMatch.Score(
            "Interstellar - Complete Soundtrack", "Some Channel",
            TimeSpan.FromHours(2), "Interstellar", 2014);

        Assert.Equal(22, score);
    }

    [Theory]
    // The measured legitimate distribution (3–6 min) must be untouched: these are the
    // pinned accept fixtures from the #39/#42 tests, now with their durations supplied.
    [InlineData(82, "The Nice Guys Theme | The Nice Guys (Official Soundtrack)", "The Nice Guys", 3.0, true)]
    [InlineData(95, "Dune Official Soundtrack | Main Theme - Hans Zimmer", "Dune", 3.75, true)]
    // The ceiling is inclusive: exactly 15:00 still passes, matching the scorer's own
    // band edge (> 15.0 is where the −20 starts).
    [InlineData(22, "Interstellar - Complete Soundtrack", "Interstellar", 15.0, true)]
    // Just past the band edge is refused — the gate, not the penalty, is what refuses.
    [InlineData(22, "Interstellar - Complete Soundtrack", "Interstellar", 15.0167, false)]
    // A two-hour correct-work soundtrack: the issue's headline case.
    [InlineData(22, "Interstellar - Complete Soundtrack", "Interstellar", 120.0, false)]
    public void IsConfident_hardDurationCeiling_refusesLongUploads_only(
        int score, string videoTitle, string mediaTitle, double minutes, bool expected)
    {
        Assert.Equal(expected, ThemeMatch.IsConfident(
            score, videoTitle, mediaTitle, TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void IsConfident_productionWorstCase_threeAndAHalfHours_refused()
    {
        // The measured worst case from issue #47: a 12,788 s (3h33m) file attached to an
        // obscure documentary. At ~100 kbps it also clears the 100 MB byte cap, which is
        // why the ceiling is not redundant with StreamLimits.
        Assert.False(ThemeMatch.IsConfident(
            22, "Interstellar - Complete Soundtrack", "Interstellar",
            TimeSpan.FromSeconds(12788)));
    }

    [Fact]
    public void IsConfident_nullDuration_keepsThePreCeilingFloor()
    {
        // No duration supplied → the pre-#47 floor, mirroring Score's HasValue skip and
        // #42's null media title: the #39 accept baseline (mainstream 11/12, shows 9/12)
        // was measured without a ceiling and cannot be re-measured, so absence of the
        // input must not change the answer.
        Assert.True(ThemeMatch.IsConfident(
            82, "The Nice Guys Theme | The Nice Guys (Official Soundtrack)", "The Nice Guys"));
        Assert.True(ThemeMatch.IsConfident(
            82, "The Nice Guys Theme | The Nice Guys (Official Soundtrack)", "The Nice Guys", null));
    }

    [Fact]
    public void BestMatchIndex_longSoundtrackOnTop_declinesRatherThanScanningDown()
    {
        // Row 0 or nothing is unchanged by #47: the over-ceiling row is refused outright,
        // and the perfectly-sized theme below it is NOT promoted — the ranking already
        // judged it the weaker candidate. The caller's answer to -1 is the 6h backoff.
        var ranked = new[]
        {
            ("Interstellar - Complete Soundtrack", 22, (TimeSpan?)TimeSpan.FromHours(2)),
            ("Interstellar - Main Theme", 20, (TimeSpan?)TimeSpan.FromMinutes(4)),
        };

        Assert.Equal(-1, ThemeMatch.BestMatchIndex(ranked, "Interstellar"));
    }

    [Fact]
    public void BestMatchIndex_normalLengthThemeOnTop_isStillMarked()
    {
        // The positive half: supplying durations must not over-reject.
        var ranked = new[]
        {
            ("Dune Official Soundtrack | Main Theme - Hans Zimmer", 95, (TimeSpan?)TimeSpan.FromMinutes(3.75)),
            ("Dune (2021) - Official Trailer", 30, (TimeSpan?)TimeSpan.FromMinutes(2)),
        };

        Assert.Equal(0, ThemeMatch.BestMatchIndex(ranked, "Dune"));
    }

    [Fact]
    public void BestMatchIndex_nullDurationRows_keepThePreCeilingBehavior()
    {
        // Rows whose duration YouTube did not report flow through the new shape as null
        // and keep the pre-#47 answer.
        var ranked = new[]
        {
            ("The Nice Guys Theme | The Nice Guys (Official Soundtrack)", 82, (TimeSpan?)null),
        };

        Assert.Equal(0, ThemeMatch.BestMatchIndex(ranked, "The Nice Guys"));
    }
}
