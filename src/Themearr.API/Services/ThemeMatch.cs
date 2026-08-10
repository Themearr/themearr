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
    /// Markers that identify a trailer, promo or clip rather than the work's own music.
    /// Shared verbatim between the penalty block in <see cref="Score"/> and
    /// <see cref="IsConfident"/> so the two can never drift apart. A title carrying one of
    /// these is disqualified from the confidence floor no matter what music word sits
    /// beside it — a music word can land in a promo's title for a reason that has nothing
    /// to do with the video being music, when the work itself is called "The Score" or
    /// "Suite Francaise". Measured directly: "The Score (2001) Official Trailer" scores 38
    /// with "score" as its own MusicPhrase, and "Suite Francaise Official Trailer #1
    /// (2015)" certifies itself via "suite" the same way — issue #39's exact failure, a
    /// trailer written to theme.mp3 for the right film.
    /// </summary>
    private static readonly string[] PromoMarkers =
    {
        "trailer", "featurette", "behind the scenes", "interview",
        // Leading space matches "clip"/"clips"/"scenes" without firing on "eclipse" (a
        // real film title) or "obscene". That is a plain Contains, weaker than
        // ContainsWord's word-boundary match (used above for "ost" and "intro") — a
        // hyphen or bracket right before "clip"/"scene" would still slip past this guard,
        // where ContainsWord would catch it. Good enough here because a title containing
        // "eclipse"/"obscene" in the first place is rare, so the common case is what
        // matters, not an airtight one.
        " clip", " scene",
    };

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

    /// <summary>
    /// The words a media title can be recognised by: split on spaces, keep those longer
    /// than three letters — "the", "of", "up" carry no identity on their own. One
    /// definition shared between <see cref="Score"/>'s partial-match branch and the
    /// identity check in <see cref="IsConfident"/>, the same move
    /// <see cref="PromoMarkers"/> makes for the penalty block and the veto, so the
    /// scorer and the floor can never disagree about which words count.
    /// </summary>
    private static string[] SignificantWords(string lowerMediaTitle)
        => lowerMediaTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Where(w => w.Length > 3)
                          .ToArray();

    /// <summary>
    /// True when the video title establishes that the music is for THIS work rather than
    /// another one sharing a word with it. A full-title match always establishes it —
    /// judged by the same plain Contains as <see cref="Score"/>'s +30 branch, so the two
    /// can never disagree about what "full" means, and deliberately not word-bounded:
    /// two-letter titles like "Up" rely on the loose form. Without a full match,
    /// identity rests on the title's significant words, and a title with at most one of
    /// them has nothing left to be identified by — its single generic word appears in
    /// thousands of unrelated uploads, which is how The Endless was assigned "Endless
    /// Space 2 Original Soundtrack" and The Menu got Stray's "Main Menu Theme" (issue
    /// #42): genuinely music, so the music-evidence floor is blind to the mistake by
    /// design; just music for a video game, not the film. Titles with two or more
    /// significant words keep the pre-#42 behavior untouched — both measured failures
    /// share the one-word shape, and the #39 accept baseline (mainstream 11/12, shows
    /// 9/12) cannot be re-measured against a broader rule. A null media title skips the
    /// check, mirroring <see cref="Score"/>, which makes no title contribution when the
    /// caller supplies none.
    /// </summary>
    private static bool TitleIdentityHolds(string vt, string? mediaTitle)
    {
        if (string.IsNullOrEmpty(mediaTitle)) return true;
        var mt = mediaTitle.ToLowerInvariant();
        if (vt.Contains(mt)) return true;
        return SignificantWords(mt).Length >= 2;
    }

    /// <summary>
    /// Whether a ranked result may be acted on with no human looking at it: it must
    /// out-rank the field, say it is music, not itself be a trailer/promo/clip, and —
    /// when the caller supplies the media title — identify THIS work
    /// (<see cref="TitleIdentityHolds"/>). The score alone was never a quality bar — it
    /// exists to order candidates against each other, so "best of a bad pool" and "good"
    /// were the same answer, and low-profile films got trailers. The promo check is not
    /// redundant with the music-evidence check: a promo's title can legitimately contain
    /// a music word (the film IS called "The Score") without the video itself being the
    /// film's music, so evidence alone is not enough — a marker in
    /// <see cref="PromoMarkers"/> vetoes the result outright. The identity check is not
    /// redundant with either: "Endless Space 2 Original Soundtrack" is real music with
    /// no promo marker, and still the wrong work (issue #42).
    /// </summary>
    public static bool IsConfident(int score, string videoTitle, string? mediaTitle = null)
    {
        var vt = videoTitle.ToLowerInvariant();
        return score > 0
            && !PromoMarkers.Any(m => vt.Contains(m, StringComparison.Ordinal))
            && HasMusicEvidence(videoTitle)
            && TitleIdentityHolds(vt, mediaTitle);
    }

    /// <summary>
    /// Index of the result to mark as the best match, or -1 for none. Row 0 or nothing:
    /// a lower-ranked result is never promoted just because it clears the floor, because
    /// the ranking already judged it the weaker candidate. Declining is a supported
    /// outcome everywhere — both auto-download workers back off 6h and the movie
    /// endpoint returns 422. The media title rides along for the identity half of the
    /// floor (issue #42); null keeps the pre-#42 floor, like <see cref="IsConfident"/>.
    /// </summary>
    public static int BestMatchIndex(IReadOnlyList<(string VideoTitle, int Score)> ranked,
        string? mediaTitle = null)
        => ranked.Count > 0 && IsConfident(ranked[0].Score, ranked[0].VideoTitle, mediaTitle) ? 0 : -1;

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
                // Partial: count significant words that appear in the video title. For
                // ranking only — a single matched word is worth +8 here and nothing to
                // the confidence floor, which is what stops one generic word ("menu")
                // certifying another work's music as this one's (issue #42).
                score += SignificantWords(mt).Count(w => vt.Contains(w)) * 8;
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
        // ── Trailers, promos and clips ────────────────────────────────────────
        // There was no penalty for a plain "trailer" at all: an exact title match (+30)
        // plus a 1-6 min runtime (+15) reached 45, and 45 cleared the old `> 0` gate, so
        // "<Film> Trailer #1" was downloaded as the theme (issue #39). The marker strings
        // live in PromoMarkers, shared with IsConfident's veto — only the penalty size is
        // local to this method, since a scaled deduction and a hard veto are different
        // uses of the same vocabulary.
        foreach (var marker in PromoMarkers)
        {
            if (!vt.Contains(marker, StringComparison.Ordinal)) continue;
            score -= marker is "trailer" or "featurette" or "behind the scenes" or "interview" ? 25 : 20;
        }

        return score;
    }
}
