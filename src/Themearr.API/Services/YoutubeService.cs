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

            var score = ThemeMatch.Score(video.Title, video.Author.ChannelTitle, video.Duration, title, year);
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

}
