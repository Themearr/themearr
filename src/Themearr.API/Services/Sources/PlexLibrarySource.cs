using System.Net;
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Plex as a library source. A thin adapter: all of the Plex API work stays in
/// <see cref="PlexService"/>.
/// </summary>
public class PlexLibrarySource(PlexService plex, Database db, IHttpClientFactory factory) : ILibrarySource
{
    /// <summary>Named client, configured in Program.cs with a short timeout.</summary>
    public const string ClientName = "plex-health";

    public string Name => "plex";

    /// <summary>Scanning a Plex library is expensive, so once a day.</summary>
    public TimeSpan SyncInterval => TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
        await plex.FetchMoviesAsync(log);

    public async Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct)
    {
        // Plex needs BOTH identifiers, so source_ref carries "{serverId}:{ratingKey}".
        var parts = (sourceRef ?? "").Split(':', 2);
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty)) return null;
        if (!db.GetPlexServersDict().TryGetValue(parts[0], out var srv)) return null;

        var height = (int)Math.Round(width * 1.5);   // 2:3 poster aspect
        var url = PlexImageUrl.Transcode(srv.Url, parts[1], srv.Token, width, height);

        var http = factory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;

        // Buffer the bytes under a cap rather than handing back resp.Content's stream:
        // the HttpResponseMessage is disposed when this method returns (the `using`
        // above), so its stream must not outlive the call, and the byte cap belongs
        // here now that the source — not PosterController — owns the fetch.
        var buffer = new MemoryStream();
        try
        {
            await StreamLimits.CopyWithLimitAsync(
                await resp.Content.ReadAsStreamAsync(ct), buffer, StreamLimits.MaxPosterBytes, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        buffer.Position = 0;
        return buffer;
    }

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        var servers = db.GetPlexServersDict();
        if (servers.Count == 0) return null;   // nothing configured is not a fault

        var (url, token) = servers.First().Value;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return null;

        var http = factory.CreateClient(ClientName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/identity");
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return "Plex rejected the stored token (401). Sign in to Plex again in Settings.";
            if (!response.IsSuccessStatusCode)
                return $"The Plex server returned HTTP {(int)response.StatusCode}.";
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"The Plex server did not respond within {http.Timeout.TotalSeconds:0} seconds.";
        }
        catch (HttpRequestException)
        {
            return "The Plex server is unreachable. Check it is running and the URL in Settings is correct.";
        }
    }
}
