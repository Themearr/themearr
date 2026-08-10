using YoutubeExplode;

namespace Themearr.API.Services;

public class YoutubeService
{
    private readonly YoutubeClient _yt = new();

    public async Task<List<Dictionary<string, object?>>> SearchAsync(
        string query, int maxResults = 8, string? title = null, int? year = null)
    {
        var raw = new List<(Dictionary<string, object?> result, int score, string videoTitle)>();

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

            var score = ThemeMatch.Score(video.Title, video.Author.ChannelTitle, video.Duration, title, year);
            raw.Add((result, score, video.Title));

            if (raw.Count >= maxResults) break;
        }

        // The title rides along so the floor can check work identity (issue #42). This
        // forward is the one hop no offline test can pin — SearchAsync is the network
        // half — the same epistemic status its call of RankAndMark has had since #39.
        return RankAndMark(raw, title);
    }

    /// <summary>
    /// The pure tail of <see cref="SearchAsync"/>: sort by score, decide and mark the best
    /// match, write scores back. Split out because <c>GetVideosAsync</c> above is the only
    /// part of <see cref="SearchAsync"/> that needs the network — everything here is a
    /// function of data already in hand, which is what makes it testable. Nothing bound
    /// SearchAsync's actual use of the confidence floor before this existed: a regression
    /// that moved the <see cref="ThemeMatch.BestMatchIndex"/> call above the sort — judging
    /// the floor on YouTube's raw order instead of the ranked one — passed the full suite.
    /// </summary>
    public static List<Dictionary<string, object?>> RankAndMark(
        List<(Dictionary<string, object?> result, int score, string videoTitle)> raw,
        string? title = null)
    {
        // Sort by score descending
        raw.Sort((a, b) => b.score.CompareTo(a.score));

        // Mark the top result as bestMatch — only when it is plausibly THIS work's music,
        // not merely the least-bad of a poor pool. Both auto-download workers and the
        // manual search badge read this flag, and all of them should decline together.
        var best = ThemeMatch.BestMatchIndex(
            raw.Select(r => (r.videoTitle, r.score)).ToList(), title);
        if (best >= 0)
            raw[best].result["bestMatch"] = true;

        return raw.Select(r => {
            r.result["score"] = r.score;
            return r.result;
        }).ToList();
    }
}
