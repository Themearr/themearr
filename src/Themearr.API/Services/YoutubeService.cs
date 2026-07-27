using YoutubeExplode;

namespace Themearr.API.Services;

public class YoutubeService
{
    private readonly YoutubeClient _yt = new();

    public async Task<List<Dictionary<string, object?>>> SearchAsync(
        string query, int maxResults = 8, string? title = null, int? year = null)
    {
        var raw = new List<(Dictionary<string, object?> result, int score)>();

        await foreach (var video in _yt.Search.GetVideosAsync(query))
        {
            var thumbnail = video.Thumbnails
                .OrderByDescending(t => t.Resolution.Area)
                .FirstOrDefault();

            var result = new Dictionary<string, object?>
            {
                ["videoId"]   = video.Id.Value,
                ["title"]     = video.Title,
                ["thumbnail"] = thumbnail?.Url,
                ["duration"]  = video.Duration.HasValue
                    ? (video.Duration.Value.Hours > 0
                        ? video.Duration.Value.ToString(@"h\:mm\:ss")
                        : video.Duration.Value.ToString(@"m\:ss"))
                    : null,
                ["channel"]   = video.Author.ChannelTitle,
                ["score"]     = 0,
                ["bestMatch"] = false,
            };

            var score = Score(video.Title, video.Author.ChannelTitle, video.Duration, title, year);
            raw.Add((result, score));

            if (raw.Count >= maxResults) break;
        }

        // Sort by score descending
        raw.Sort((a, b) => b.score.CompareTo(a.score));

        // Mark the top result as bestMatch (only if it has a positive score)
        if (raw.Count > 0 && raw[0].score > 0)
            raw[0].result["bestMatch"] = true;

        var results = raw.Select(r => {
            r.result["score"] = r.score;
            return r.result;
        }).ToList();

        return results;
    }

    private static int Score(string videoTitle, string channel, TimeSpan? duration,
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
