using System.Security.Cryptography;
using System.Text;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Makes ThemeMatch.RulesVersion honest. The version only triggers a re-check if someone
/// remembers to bump it when the rules change — and a forgotten bump fails nothing: the
/// release ships, no library is revisited, and the improvement reaches only future
/// downloads. That is the exact failure this whole feature exists to undo.
///
/// So the verdicts over a fixed corpus are hashed and pinned beside the version. Change
/// any vocabulary list, any score weight, or any branch of RejectionReason and this goes
/// red; the only way back to green is updating the hash AND the version together.
///
/// Hashing verdicts rather than the rule arrays is what makes it cover the weights and
/// the logic, not just the vocabulary.
/// </summary>
public class RejectionReasonFingerprintTests
{
    private static readonly (string VideoTitle, string? MediaTitle, int? Year, int? StoredScore)[] Corpus =
    {
        // ── Promo markers ────────────────────────────────────────────────────
        ("Interstellar - Official Trailer",              "Interstellar",      2014, 45),
        ("The Score (2001) Official Trailer",            "The Score",         2001, 38),
        ("Suite Francaise Official Trailer #1 (2015)",   "Suite Francaise",   2015, 40),
        ("Dune - Behind The Scenes",                     "Dune",              2021, 30),
        ("Arrival Featurette",                           "Arrival",           2016, 25),
        ("Blade Runner 2049 Cast Interview",             "Blade Runner 2049", 2017, 20),
        ("Alien - Chestburster clip",                    "Alien",             1979, 20),
        ("Heat - Diner scene",                           "Heat",              1995, 20),
        // ── Each MusicPhrase, as evidence ────────────────────────────────────
        ("Interstellar Main Theme - Hans Zimmer",        "Interstellar",      2014, 65),
        ("Dune (Original Soundtrack)",                   "Dune",              2021, 57),
        ("Arrival - Original Score",                     "Arrival",           2016, 54),
        ("Lawrence of Arabia Main Title",                "Lawrence of Arabia",1962, null),
        ("Holst - Jupiter Suite",                        "Jupiter",           1914, null),
        ("Overture",                                     "Fantasia",          1940, null),
        ("Twin Peaks Title Sequence",                    "Twin Peaks",        1990, null),
        ("Opening Credits [OST]",                        "Dune",              2021, null),
        ("End Credits",                                  "Se7en",             1995, null),
        // ── Short words, boundary-matched ────────────────────────────────────
        ("Akira (1988) OST",                             "Akira",             1988, null),
        ("Ghostbusters (1984)",                          "Ghostbusters",      1984, null),
        ("Serial Experiments Lain - Intro",              "Serial Experiments Lain", 1998, null),
        ("An Introduction to Dune",                      "Dune",              2021, null),
        // ── Negative markers ─────────────────────────────────────────────────
        ("Every Star Wars Theme Ranked",                 "Star Wars",         1977, null),
        ("Top 10 Movie Themes",                          "Star Wars",         1977, null),
        ("Star Wars Theme Compilation",                  "Star Wars",         1977, null),
        ("Star Wars Theme REACTION",                     "Star Wars",         1977, null),
        ("Star Wars Theme - Piano Cover",                "Star Wars",         1977, null),
        ("Star Wars Theme (Guitar Remix)",               "Star Wars",         1977, null),
        ("A Tribute to John Williams' Themes",           "Star Wars",         1977, null),
        ("Star Wars Theme Parody",                       "Star Wars",         1977, null),
        // ── Stored-score branch ──────────────────────────────────────────────
        ("Some Theme",                                   "Some Film",         2001, 0),
        ("Some Theme",                                   "Some Film",         2001, -5),
        ("Some Theme",                                   "Some Film",         2001, 1),
        // ── No evidence at all ───────────────────────────────────────────────
        ("Blade Runner 2049 - Mesa",                     "Blade Runner 2049", 2017, 45),
    };

    [Fact]
    public void Verdicts_matchThePinnedFingerprintForThisRulesVersion()
    {
        var verdicts = string.Join("\n", Corpus.Select(c =>
            ThemeMatch.RejectionReason(c.VideoTitle, c.MediaTitle, c.Year, c.StoredScore) ?? "KEEP"));

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(verdicts)))[..16];

        Assert.Equal(1, ThemeMatch.RulesVersion);

        // Measured, not predicted: run the test, read the actual value from the failure
        // message, and paste it here. If you are changing the rules, this number moving
        // is the signal to bump RulesVersion in the same commit.
        Assert.Equal("ECF4769215001241", fingerprint);
    }
}
