using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the rule that decides whether a theme ALREADY on disk still passes today's
/// standards. Distinct from ThemeMatchTests, which pins the rule for choosing a new
/// one: this one judges a single stored pick with no candidate field to rank it
/// against, and with only the evidence `theme_history` kept.
/// </summary>
public class RejectionReasonTests
{
    [Fact]
    public void StoredPick_thatIsATrailer_isRejected()
    {
        // Issue #39's exact failure, still on disk in libraries synced before #41.
        var reason = ThemeMatch.RejectionReason(
            "Interstellar - Official Trailer", "Interstellar", 2014, storedScore: 45);

        Assert.Equal("looks like a trailer, clip or promo", reason);
    }

    [Fact]
    public void StoredPick_titledLikeTheFilmItself_isRejectedForPromoBeforeEvidence()
    {
        // "The Score (2001) Official Trailer" certifies itself as music via its own
        // title (ThemeMatch.cs:42-45). The promo veto must run FIRST, or the film's
        // name launders the trailer through the evidence check.
        var reason = ThemeMatch.RejectionReason(
            "The Score (2001) Official Trailer", "The Score", 2001, storedScore: 38);

        Assert.Equal("looks like a trailer, clip or promo", reason);
    }

    [Fact]
    public void StoredPick_withNoMusicWordAtAll_isRejected()
    {
        var reason = ThemeMatch.RejectionReason(
            "Blade Runner 2049 - Mesa", "Blade Runner 2049", 2017, storedScore: 45);

        Assert.Equal("nothing in the title says it is the work's music", reason);
    }

    [Fact]
    public void StoredPick_withEvidenceAndAPositiveStoredScore_isKept()
    {
        Assert.Null(ThemeMatch.RejectionReason(
            "Interstellar Main Theme - Hans Zimmer", "Interstellar", 2014, storedScore: 65));
    }

    [Fact]
    public void StoredPick_withAStoredScoreAtOrBelowZero_isRejected()
    {
        // Reproduces IsConfident's `score > 0` exactly (ThemeMatch.cs:104) — which is
        // the entire point of persisting the score.
        Assert.Equal("ranked below the confidence floor",
            ThemeMatch.RejectionReason("Some Theme", "Some Film", 2001, storedScore: 0));
    }

    [Fact]
    public void LegacyPick_scoringZeroOnlyForLackOfADurationBonus_isKept()
    {
        // THE false-flag guard. "opening credits" is a MusicPhrase (ThemeMatch.cs:25)
        // but contributes nothing to Score, and "[ost]" misses the literal " ost"
        // (ThemeMatch.cs:147) that HasMusicEvidence matches via ContainsWord. So this
        // scored 15 at download time on its duration bonus alone and recomputes to 0.
        // Rejecting at `<= 0` here would re-download a theme that was always fine.
        Assert.Null(ThemeMatch.RejectionReason(
            "Opening Credits [OST]", "Dune", 2021, storedScore: null));
    }

    [Fact]
    public void LegacyPick_withActiveNegativeMarkers_isRejected()
    {
        // 30 title + 15 theme - 30 ranked - 20 "every " = -5. A negative score means
        // the title carries real negative signal, not a missing bonus.
        Assert.Equal("the title carries negative match signals",
            ThemeMatch.RejectionReason(
                "Every Star Wars Theme Ranked", "Star Wars", 1977, storedScore: null));
    }

    [Fact]
    public void LegacyPick_withCleanEvidence_isKept()
    {
        Assert.Null(ThemeMatch.RejectionReason(
            "Dune Main Theme (Original Soundtrack)", "Dune", 2021, storedScore: null));
    }
}
