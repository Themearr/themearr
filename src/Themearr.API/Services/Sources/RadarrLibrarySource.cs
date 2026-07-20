using System.Net;
using System.Text.Json;
using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Radarr as a library source. Radarr knows every movie's folder, title, year and
/// whether the film is actually downloaded — everything Themearr needs — so a Radarr
/// user needs no Plex at all. Because theme.mp3 is read by Jellyfin, Emby and Kodi
/// too, this is what makes Themearr useful to them.
/// </summary>
public class RadarrLibrarySource(Database db, LocalFolderResolver folders, IHttpClientFactory factory)
    : ILibrarySource
{
    /// <summary>Named client, configured in Program.cs with a short timeout.</summary>
    public const string ClientName = "radarr";

    public string Name => "radarr";

    /// <summary>Radarr is local and cheap to poll, so a new import gets its theme quickly.</summary>
    public TimeSpan SyncInterval => TimeSpan.FromMinutes(15);

    private (string Url, string Key) Config() =>
        (db.GetSetting("radarr_url", "").TrimEnd('/'), db.GetSetting("radarr_api_key", ""));

    private HttpRequestMessage Request(string url, string key, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{url}{path}");
        // Header, never a query parameter — the key must not end up in a URL that could
        // be logged by a proxy.
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        return request;
    }

    public async Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct)
    {
        var (url, key) = Config();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Radarr is not configured — set its URL and API key in Settings.");

        log($"Fetching movies from Radarr at {url}");

        var http = factory.CreateClient(ClientName);
        using var request = Request(url, key, "/api/v3/movie");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Radarr returned HTTP {(int)response.StatusCode} listing movies.");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var movies = new List<MovieRecord>();
        var unresolvedCount = 0;
        var unresolvedSample = "";

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // Monitored but not downloaded: a folder may exist, but there is no film for
            // a theme to accompany yet.
            if (!item.TryGetProperty("hasFile", out var hasFile) || !hasFile.GetBoolean()) continue;

            var reported = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(reported)) continue;

            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var year  = item.TryGetProperty("year", out var y) && y.TryGetInt32(out var yr) && yr > 0
                ? yr : (int?)null;
            var id    = item.TryGetProperty("id", out var i) ? i.GetRawText().Trim('"') : "";

            // Radarr reports paths from its own filesystem's perspective, exactly as Plex
            // does — a container may call it /movies where Themearr sees /mnt/media.
            // LocalFolderResolver.Resolve expects a *file* path and returns its containing
            // folder, but Radarr reports the folder directly, so a dummy filename is
            // appended here to reuse the existing resolver unchanged rather than
            // duplicating its logic.
            var (folder, _) = folders.Resolve(reported + "/placeholder.mkv");
            if (string.IsNullOrEmpty(folder))
            {
                unresolvedCount++;
                if (unresolvedSample.Length == 0) unresolvedSample = reported;
                log($"Skipping {title} — unresolved path: {reported}  (add a Path Mapping from this path to where it's mounted in Themearr)");
                continue;
            }

            movies.Add(new MovieRecord(folder, "radarr", id, title, year, reported));
        }

        // Read by LibraryPathsCheck; overwritten every sync so a fixed mapping clears it.
        db.SetSetting("last_sync_unresolved_count",  unresolvedCount.ToString());
        db.SetSetting("last_sync_unresolved_sample", unresolvedSample);

        log($"Radarr reported {movies.Count} downloaded movies");
        return movies;
    }

    public async Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct)
    {
        var (url, key) = Config();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(sourceRef))
            return null;

        var http = factory.CreateClient(ClientName);
        using var request = Request(url, key, $"/api/v3/mediacover/{Uri.EscapeDataString(sourceRef)}/poster.jpg");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        // Buffer the bytes under a cap rather than handing back response.Content's
        // stream: the HttpResponseMessage is disposed when this method returns (the
        // `using` above), so its stream must not outlive the call.
        var buffer = new MemoryStream();
        try
        {
            await StreamLimits.CopyWithLimitAsync(
                await response.Content.ReadAsStreamAsync(ct), buffer, StreamLimits.MaxPosterBytes, ct);
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
        var (url, key) = Config();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            return "Radarr is not configured — set its URL and API key in Settings.";

        var http = factory.CreateClient(ClientName);
        try
        {
            using var request = Request(url, key, "/api/v3/system/status");
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return "Radarr rejected the API key (401). Check the key in Settings → Library source.";
            if (!response.IsSuccessStatusCode)
                return $"Radarr returned HTTP {(int)response.StatusCode}.";
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Radarr did not respond within {http.Timeout.TotalSeconds:0} seconds.";
        }
        catch (HttpRequestException)
        {
            return "Radarr is unreachable. Check it is running and the URL in Settings is correct.";
        }
    }
}
