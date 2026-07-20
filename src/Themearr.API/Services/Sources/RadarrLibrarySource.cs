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

    /// <summary>
    /// The one message shown for any malformed Radarr body — invalid JSON, a JSON value
    /// that isn't the expected array, or a field of an unexpected type. Deliberately does
    /// not include the underlying parser exception's text, matching every other message
    /// in this class: raw framework text is either cryptic or (in other classes) capable
    /// of leaking internals, so callers only ever see this hand-written sentence.
    /// </summary>
    private const string MalformedResponseMessage =
        "Radarr returned an unexpected response. Check the URL points at Radarr and not another service.";

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

        // A malformed body (truncated JSON, an HTML error page from a misconfigured
        // reverse proxy, etc.) throws JsonException here. Converted to the same clean,
        // hand-written message used everywhere else in this class rather than letting the
        // parser's own text — meaningless to a user picking a URL in Settings — reach them
        // through SyncService's generic catch.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(MalformedResponseMessage);
        }

        using (doc)
        {
            // A well-formed JSON value that isn't an array (e.g. an error object from a
            // service that merely happens to speak JSON) — checked explicitly rather than
            // letting EnumerateArray() throw, so the message stays the same clean sentence
            // instead of "...requires an element of type 'Array'...".
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(MalformedResponseMessage);

            var movies = new List<MovieRecord>();
            var unresolvedCount = 0;
            var unresolvedSample = "";

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                try
                {
                    // Monitored but not downloaded: a folder may exist, but there is no
                    // film for a theme to accompany yet.
                    if (!item.TryGetProperty("hasFile", out var hasFile) || !hasFile.GetBoolean()) continue;

                    var reported = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    // Trim a trailing separator before it's used for anything below: left
                    // in, it would double up with the dummy filename appended just below
                    // ("...//placeholder.mkv"), and PlexPath.ParentDir (which trims only
                    // one trailing separator) would then hand back a folder string with a
                    // trailing slash baked in — splitting this movie's identity from the
                    // same directory resolved without the slash.
                    reported = reported.TrimEnd('/', '\\');
                    if (string.IsNullOrEmpty(reported)) continue;

                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var year  = item.TryGetProperty("year", out var y) && y.TryGetInt32(out var yr) && yr > 0
                        ? yr : (int?)null;
                    var id    = item.TryGetProperty("id", out var i) ? i.GetRawText().Trim('"') : "";

                    // Radarr reports paths from its own filesystem's perspective, exactly
                    // as Plex does — a container may call it /movies where Themearr sees
                    // /mnt/media. LocalFolderResolver.Resolve expects a *file* path and
                    // returns its containing folder, but Radarr reports the folder
                    // directly, so a dummy filename is appended here to reuse the existing
                    // resolver unchanged rather than duplicating its logic.
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
                catch (InvalidOperationException)
                {
                    // A field had a type Radarr's own API never sends (e.g. hasFile as a
                    // string, path as a number) — most likely a single corrupt entry
                    // rather than a wrong URL, since the response as a whole did parse as
                    // the expected array. Skip just this movie so one bad entry doesn't
                    // cost every other movie in the library its theme.
                    log("Skipping a movie entry from Radarr — one of its fields had an unexpected type.");
                }
            }

            // Read by LibraryPathsCheck; overwritten every sync so a fixed mapping clears it.
            db.SetSetting("last_sync_unresolved_count",  unresolvedCount.ToString());
            db.SetSetting("last_sync_unresolved_sample", unresolvedSample);

            log($"Radarr reported {movies.Count} downloaded movies");
            return movies;
        }
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

    /// <summary>Mirrors the guard at the top of <see cref="FetchAsync"/> — a sync must fail
    /// this fast, before a background task even starts, rather than only inside it.</summary>
    public string? SyncBlockedReason
    {
        get
        {
            var (url, key) = Config();
            return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)
                ? "Radarr is not configured — set its URL and API key in Settings."
                : null;
        }
    }

    public Task<string?> CheckAsync(CancellationToken ct)
    {
        var (url, key) = Config();
        return ProbeAsync(url, key, ct);
    }

    /// <summary>
    /// Probes Radarr at the given URL/key without touching stored settings — used both by
    /// <see cref="CheckAsync"/> (stored config) and by the Settings "Test" endpoint (the
    /// values the user just typed, before they've been saved). Never writes to the
    /// database, so a test can never race a scheduled sync or corrupt saved credentials.
    /// </summary>
    public async Task<string?> ProbeAsync(string url, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(apiKey))
            return "Radarr is not configured — set its URL and API key in Settings.";
        url = url.TrimEnd('/');

        var http = factory.CreateClient(ClientName);
        try
        {
            using var request = Request(url, apiKey, "/api/v3/system/status");
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
