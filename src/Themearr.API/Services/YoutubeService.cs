using YoutubeExplode;
using YoutubeExplode.Common;

namespace Themearr.API.Services;

public class YoutubeService
{
    private readonly YoutubeClient _yt = new();

    public async Task<List<Dictionary<string, object?>>> SearchAsync(string query, int maxResults = 3)
    {
        var results = new List<Dictionary<string, object?>>();
        await foreach (var video in _yt.Search.GetVideosAsync(query))
        {
            var thumbnail = video.Thumbnails
                .OrderByDescending(t => t.Resolution.Area)
                .FirstOrDefault();

            results.Add(new Dictionary<string, object?>
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
            });

            if (results.Count >= maxResults) break;
        }
        return results;
    }
}
