using System.Text.Json;
using System.Web;
using System.Xml.Linq;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class PlexService(HttpClient http, Database db, LocalFolderResolver folders)
{
    private const string ApiBase  = "https://plex.tv/api/v2";
    private const string Product  = "Themearr";
    private const string Platform = "Web";

    // ── Client identifier ────────────────────────────────────────────────────

    public string GetClientIdentifier()
    {
        var id = db.GetSetting("plex_client_identifier").Trim();
        if (!string.IsNullOrEmpty(id)) return id;
        id = Guid.NewGuid().ToString();
        db.SetSetting("plex_client_identifier", id);
        return id;
    }

    private Dictionary<string, string> ClientHeaders(string clientId, string? token = null, bool json = false)
    {
        var h = new Dictionary<string, string>
        {
            ["Accept"]                  = json ? "application/json" : "application/xml",
            ["X-Plex-Product"]          = Product,
            ["X-Plex-Platform"]         = Platform,
            ["X-Plex-Device"]           = Product,
            ["X-Plex-Client-Identifier"] = clientId,
            ["X-Plex-Version"]          = db.GetSetting("app_version", "dev"),
        };
        if (!string.IsNullOrEmpty(token)) h["X-Plex-Token"] = token;
        return h;
    }

    private Dictionary<string, string> ClientParams(string clientId) => new()
    {
        ["X-Plex-Product"]           = Product,
        ["X-Plex-Platform"]          = Platform,
        ["X-Plex-Device"]            = Product,
        ["X-Plex-Client-Identifier"] = clientId,
        ["X-Plex-Version"]           = db.GetSetting("app_version", "dev"),
    };

    // ── PIN login ────────────────────────────────────────────────────────────

    public async Task<Dictionary<string, object>> CreateLoginPinAsync(string forwardUrl)
    {
        var clientId = GetClientIdentifier();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/pins");
        foreach (var (k, v) in ClientHeaders(clientId, json: true)) req.Headers.TryAddWithoutValidation(k, v);

        var bodyParams = ClientParams(clientId);
        bodyParams["strong"] = "true";
        req.Content = new FormUrlEncodedContent(bodyParams);

        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var payload = await CoercePayloadAsync(resp);
        var pinId = Convert.ToInt32(payload.GetValueOrDefault("id", 0));
        var code  = payload.GetValueOrDefault("code", "")?.ToString() ?? "";

        if (pinId == 0 || string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Plex did not return a valid login PIN");

        var effectiveForward = AugmentForwardUrl(forwardUrl, pinId, code);
        return new Dictionary<string, object>
        {
            ["pinId"]            = pinId,
            ["code"]             = code,
            ["clientIdentifier"] = clientId,
            ["authUrl"]          = BuildAuthUrl(code, clientId, effectiveForward),
        };
    }

    public async Task<Dictionary<string, object>> CheckLoginPinAsync(int pinId, string code)
    {
        var clientId = GetClientIdentifier();
        var url = $"{ApiBase}/pins/{pinId}?" + BuildQuery(ClientParams(clientId), ("code", code));

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in ClientHeaders(clientId, json: true)) req.Headers.TryAddWithoutValidation(k, v);

        var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode == 404)
            throw new InvalidOperationException("The Plex login PIN expired. Please try again.");
        resp.EnsureSuccessStatusCode();

        var payload = await CoercePayloadAsync(resp);
        // Plex v2 JSON returns camelCase "authToken"; XML returns snake_case "auth_token"
        var authToken = (payload.GetValueOrDefault("authToken")
                      ?? payload.GetValueOrDefault("auth_token"))?.ToString()?.Trim() ?? "";

        return new Dictionary<string, object>
        {
            ["claimed"]   = !string.IsNullOrEmpty(authToken),
            ["authToken"] = authToken,
        };
    }

    // ── User info ────────────────────────────────────────────────────────────

    public async Task<string> GetAccountNameAsync(string accessToken)
    {
        var clientId = GetClientIdentifier();
        var url = $"{ApiBase}/user?" + BuildQuery(ClientParams(clientId), ("X-Plex-Token", accessToken));

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in ClientHeaders(clientId, accessToken, json: true)) req.Headers.TryAddWithoutValidation(k, v);

        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var payload = await CoercePayloadAsync(resp);
        return (payload.GetValueOrDefault("username")
             ?? payload.GetValueOrDefault("title")
             ?? payload.GetValueOrDefault("email")
             ?? "Plex user")?.ToString()?.Trim() ?? "Plex user";
    }

    // ── Server discovery ─────────────────────────────────────────────────────

    public async Task<List<Dictionary<string, object>>> DiscoverServersAsync(string accessToken)
    {
        var clientId = GetClientIdentifier();
        var url = "https://plex.tv/api/resources?" + BuildQuery(
            ClientParams(clientId),
            ("includeHttps", "1"), ("includeRelay", "1"), ("X-Plex-Token", accessToken));

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in ClientHeaders(clientId, accessToken)) req.Headers.TryAddWithoutValidation(k, v);

        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var resources = ParseResources(await resp.Content.ReadAsStringAsync());
        var servers = new List<Dictionary<string, object>>();

        foreach (var resource in resources)
        {
            var provides = resource.GetValueOrDefault("provides", "")?.ToString() ?? "";
            if (!provides.Contains("server", StringComparison.OrdinalIgnoreCase)) continue;

            var serverId = resource.GetValueOrDefault("clientIdentifier", "")?.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(serverId)) continue;

            var urls = RankConnections(resource);
            if (urls.Count == 0) continue;

            servers.Add(new Dictionary<string, object>
            {
                ["id"]       = serverId,
                ["name"]     = resource.GetValueOrDefault("name", "")?.ToString()?.Trim() ?? urls[0],
                ["url"]      = urls[0],
                ["urls"]     = urls,
                ["token"]    = resource.GetValueOrDefault("accessToken", "")?.ToString()?.Trim() ?? accessToken,
                ["owned"]    = CoerceBool(resource.GetValueOrDefault("owned", "")?.ToString()),
                ["presence"] = CoerceBool(resource.GetValueOrDefault("presence", "")?.ToString()),
            });
        }

        return servers
            .OrderBy(s => !(bool)s["owned"])
            .ThenBy(s => !(bool)s["presence"])
            .ThenBy(s => s["name"])
            .ToList();
    }

    // ── Libraries ────────────────────────────────────────────────────────────

    /// <summary>
    /// Libraries on the server. <paramref name="libraryType"/> filters to one Plex type;
    /// pass <c>null</c> for every type, which the Settings pickers need — they render one
    /// list per media type from a single response and filter client-side.
    /// </summary>
    public async Task<List<Dictionary<string, object>>> ListLibrariesAsync(
        List<string> serverUrls, string serverToken, string? libraryType = "movie")
    {
        var clientId = GetClientIdentifier();
        Exception? last = null;

        foreach (var url in serverUrls)
        {
            try
            {
                var endpoint = $"{url.TrimEnd('/')}/library/sections?" +
                    BuildQuery(ClientParams(clientId), ("X-Plex-Token", serverToken));
                var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                foreach (var (k, v) in ClientHeaders(clientId, serverToken)) req.Headers.TryAddWithoutValidation(k, v);
                var resp = await http.SendAsync(req);
                resp.EnsureSuccessStatusCode();

                var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
                return xml.Descendants("Directory")
                    .Where(d => libraryType == null || d.Attribute("type")?.Value?.ToLower() == libraryType)
                    // Report the type Plex actually gave, not the one that was asked for —
                    // with no filter they differ, and the pickers key off this value.
                    .Select(d => (dir: d, type: d.Attribute("type")?.Value?.ToLower() ?? ""))
                    .Select(x => new Dictionary<string, object>
                    {
                        ["key"]   = x.dir.Attribute("key")?.Value ?? "",
                        ["title"] = x.dir.Attribute("title")?.Value ?? (x.type == "movie" ? "Movies" : "TV Shows"),
                        ["type"]  = x.type,
                    })
                    .Where(lib => !string.IsNullOrEmpty(lib["key"]?.ToString()))
                    .ToList();
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("No usable Plex server URL");
    }

    // ── Movie fetch ───────────────────────────────────────────────────────────

    public async Task<List<MovieRecord>> FetchMoviesAsync(Action<string>? logFn = null)
    {
        var accessToken = db.GetSetting("plex_access_token").Trim();
        var clientId    = db.GetSetting("plex_client_identifier").Trim();
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException("Plex sign-in has not been completed");

        var servers   = db.GetPlexServers();
        var libMap    = db.GetSelectedLibraries();
        var result    = new List<MovieRecord>();
        var seen      = new HashSet<string>();

        // Movies skipped because no local folder could be resolved. Recorded into
        // settings at the end so LibraryPathsCheck can warn about a broken Path
        // Mapping — these movies never enter the DB, so they cannot be counted later.
        var unresolvedCount  = 0;
        var unresolvedSample = "";

        try
        {
            foreach (var srv in servers)
            {
                var serverId  = srv.GetValueOrDefault("id", "")?.ToString()?.Trim() ?? "";
                var serverName = srv.GetValueOrDefault("name", "")?.ToString()?.Trim() ?? "";
                var primaryUrl = srv.GetValueOrDefault("url", "")?.ToString()?.Trim() ?? "";
                var urlList   = srv.GetValueOrDefault("urls") is JsonElement je && je.ValueKind == JsonValueKind.Array
                    ? je.EnumerateArray().Select(u => u.GetString() ?? "").Where(u => !string.IsNullOrEmpty(u)).ToList()
                    : new List<string> { primaryUrl };
                var serverToken = srv.GetValueOrDefault("token", "")?.ToString()?.Trim() ?? "";

                if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(serverToken)) continue;
                logFn?.Invoke($"Using Plex server: {serverName}");

                var libs = await ListLibrariesAsync(urlList, serverToken);
                var selectedKeys = libMap.GetValueOrDefault(serverId, []);
                if (selectedKeys.Count > 0) libs = libs.Where(l => selectedKeys.Contains(l["key"]?.ToString() ?? "")).ToList();

                logFn?.Invoke($"Found {libs.Count} selected movie libraries on {serverName}");

                foreach (var lib in libs)
                {
                    var sectionKey = lib["key"]?.ToString() ?? "";
                    logFn?.Invoke($"Scanning library: {lib["title"]}");

                    var items = await FetchMoviesForSectionAsync(urlList, sectionKey, serverToken, clientId);
                    foreach (var item in items)
                    {
                        var ratingKey = item.Attribute("ratingKey")?.Value?.Trim() ?? "";
                        if (string.IsNullOrEmpty(ratingKey)) continue;

                        var movieId = $"{serverId}:{ratingKey}";
                        if (!seen.Add(movieId)) continue;

                        var filePath = item.Descendants("Part").FirstOrDefault()?.Attribute("file")?.Value?.Trim() ?? "";
                        if (string.IsNullOrEmpty(filePath)) { logFn?.Invoke($"Skipping — no media path"); continue; }

                        var title = item.Attribute("title")?.Value?.Trim() ?? "";
                        var yearStr = item.Attribute("year")?.Value;
                        var year = int.TryParse(yearStr, out var y) ? y : (int?)null;

                        var (folder, mode) = folders.Resolve(filePath);
                        if (string.IsNullOrEmpty(folder))
                        {
                            unresolvedCount++;
                            if (unresolvedSample.Length == 0) unresolvedSample = filePath;
                            logFn?.Invoke($"Skipping {title} — unresolved path: {filePath}  (add a Path Mapping from this path's folder to where it's mounted in Themearr)");
                            continue;
                        }

                        logFn?.Invoke($"Matched: {title} ({year}) -> {folder} [{mode}]");
                        // source_ref keeps BOTH identifiers: PlexImageUrl needs the server as
                        // well as the rating key, so a rating key alone would break posters
                        // for anyone running more than one Plex server.
                        result.Add(new MovieRecord(folder, "plex", movieId, title, year, filePath));
                    }
                }
            }
        }
        finally
        {
            // Overwritten every sync, so fixing a mapping clears the health warning
            // on the next run. Recorded here even when a sync fails partway, so the
            // numbers always describe the most recent attempt rather than an older
            // successful one.
            try
            {
                db.SetSetting("last_sync_unresolved_count",  unresolvedCount.ToString());
                db.SetSetting("last_sync_unresolved_sample", unresolvedSample);
            }
            catch (Exception)
            {
                /* settings write must not mask the sync error; counters are diagnostics only */
            }
        }

        return result;
    }

    private async Task<List<XElement>> FetchMoviesForSectionAsync(
        List<string> serverUrls, string sectionKey, string serverToken, string clientId)
    {
        var items = new List<XElement>();
        var pageSize = 200;
        var start = 0;
        var activeUrl = serverUrls[0];

        while (true)
        {
            var url = $"{activeUrl.TrimEnd('/')}/library/sections/{sectionKey}/all?" +
                BuildQuery(ClientParams(clientId),
                    ("type", "1"), ("X-Plex-Token", serverToken),
                    ("X-Plex-Container-Start", start.ToString()),
                    ("X-Plex-Container-Size", pageSize.ToString()));

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (k, v) in ClientHeaders(clientId, serverToken)) req.Headers.TryAddWithoutValidation(k, v);
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = xml.Root!;
            items.AddRange(root.Elements("Video"));

            var size = int.Parse(root.Attribute("size")?.Value ?? "0");
            var totalSize = int.Parse(root.Attribute("totalSize")?.Value ?? size.ToString());
            if (size <= 0 || start + size >= totalSize) break;
            start += size;
        }
        return items;
    }

    // ── Show fetch ────────────────────────────────────────────────────────────

    public async Task<List<ShowRecord>> FetchShowsAsync(Action<string>? logFn = null)
    {
        var accessToken = db.GetSetting("plex_access_token").Trim();
        var clientId    = db.GetSetting("plex_client_identifier").Trim();
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException("Plex sign-in has not been completed");

        var servers  = db.GetPlexServers();
        var libMap   = db.GetSelectedShowLibraries();
        var result   = new List<ShowRecord>();
        var seen     = new HashSet<string>();
        var unresolvedCount = 0;
        var unresolvedSample = "";

        foreach (var srv in servers)
        {
            var serverId    = srv.GetValueOrDefault("id", "")?.ToString()?.Trim() ?? "";
            var serverName  = srv.GetValueOrDefault("name", "")?.ToString()?.Trim() ?? "";
            var primaryUrl  = srv.GetValueOrDefault("url", "")?.ToString()?.Trim() ?? "";
            var urlList     = srv.GetValueOrDefault("urls") is JsonElement je && je.ValueKind == JsonValueKind.Array
                ? je.EnumerateArray().Select(u => u.GetString() ?? "").Where(u => !string.IsNullOrEmpty(u)).ToList()
                : new List<string> { primaryUrl };
            var serverToken = srv.GetValueOrDefault("token", "")?.ToString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(serverId) || string.IsNullOrEmpty(serverToken)) continue;

            var selectedKeys = libMap.GetValueOrDefault(serverId, []);
            if (selectedKeys.Count == 0) continue;   // opt-in: nothing selected → skip this server

            var libs = await ListLibrariesAsync(urlList, serverToken, "show");
            libs = libs.Where(l => selectedKeys.Contains(l["key"]?.ToString() ?? "")).ToList();
            logFn?.Invoke($"Scanning {libs.Count} show libraries on {serverName}");

            foreach (var lib in libs)
            {
                var sectionKey = lib["key"]?.ToString() ?? "";
                foreach (var show in await FetchShowsForSectionAsync(urlList, sectionKey, serverToken, clientId))
                {
                    if (string.IsNullOrEmpty(show.RatingKey)) continue;
                    var showId = $"{serverId}:{show.RatingKey}";
                    if (!seen.Add(showId)) continue;
                    if (string.IsNullOrEmpty(show.RootFolder))
                    {
                        logFn?.Invoke($"Skipping {show.Title} — no folder reported by Plex");
                        continue;
                    }
                    // Reuse the file-path resolver by appending a dummy filename, so the show's
                    // ROOT folder is resolved through path-mappings (same trick as RadarrLibrarySource).
                    var (folder, _) = folders.Resolve(show.RootFolder.TrimEnd('/', '\\') + "/placeholder.mkv");
                    if (string.IsNullOrEmpty(folder))
                    {
                        unresolvedCount++;
                        if (unresolvedSample.Length == 0) unresolvedSample = show.RootFolder;
                        logFn?.Invoke($"Skipping {show.Title} — unresolved path: {show.RootFolder}  (add a Path Mapping)");
                        continue;
                    }
                    result.Add(new ShowRecord(folder, "plex", showId, show.Title, show.Year, show.RootFolder, show.HasTheme));
                }
            }
        }

        db.SetSetting("last_show_sync_unresolved_count", unresolvedCount.ToString());
        db.SetSetting("last_show_sync_unresolved_sample", unresolvedSample);
        return result;
    }

    private async Task<List<PlexShow>> FetchShowsForSectionAsync(
        List<string> serverUrls, string sectionKey, string serverToken, string clientId)
    {
        var shows = new List<PlexShow>();
        var pageSize = 200; var start = 0; var activeUrl = serverUrls[0];
        while (true)
        {
            var url = $"{activeUrl.TrimEnd('/')}/library/sections/{sectionKey}/all?" +
                BuildQuery(ClientParams(clientId),
                    ("type", "2"), ("X-Plex-Token", serverToken),
                    ("X-Plex-Container-Start", start.ToString()), ("X-Plex-Container-Size", pageSize.ToString()));
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (k, v) in ClientHeaders(clientId, serverToken)) req.Headers.TryAddWithoutValidation(k, v);
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            shows.AddRange(PlexShowThemes.Parse(body));
            var root = XDocument.Parse(body).Root!;
            var size = int.Parse(root.Attribute("size")?.Value ?? "0");
            var totalSize = int.Parse(root.Attribute("totalSize")?.Value ?? size.ToString());
            if (size <= 0 || start + size >= totalSize) break;
            start += size;
        }
        return shows;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildAuthUrl(string code, string clientId, string forwardUrl)
    {
        var p = $"clientID={Uri.EscapeDataString(clientId)}&code={Uri.EscapeDataString(code)}&context[device][product]={Uri.EscapeDataString(Product)}";
        if (!string.IsNullOrEmpty(forwardUrl)) p += $"&forwardUrl={Uri.EscapeDataString(forwardUrl)}";
        return $"https://app.plex.tv/auth#?{p}";
    }

    private static string AugmentForwardUrl(string forwardUrl, int pinId, string code)
    {
        if (string.IsNullOrEmpty(forwardUrl)) return "";
        var ub = new UriBuilder(forwardUrl);
        var q = HttpUtility.ParseQueryString(ub.Query);
        q["plexPinId"] = pinId.ToString();
        q["plexCode"]  = code;
        ub.Query = q.ToString();
        return ub.ToString();
    }

    private static string BuildQuery(Dictionary<string, string> baseParams, params (string k, string v)[] extras)
    {
        var all = new Dictionary<string, string>(baseParams);
        foreach (var (k, v) in extras) all[k] = v;
        return string.Join("&", all.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static async Task<Dictionary<string, object?>> CoercePayloadAsync(HttpResponseMessage resp)
    {
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var text = await resp.Content.ReadAsStringAsync();
        if (contentType.Contains("json"))
        {
            var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
            return obj?.ToDictionary(kv => kv.Key, kv => (object?)(
                kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.ToString()
            )) ?? [];
        }
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            var xml = XDocument.Parse(text);
            return xml.Root!.Attributes().ToDictionary(a => a.Name.LocalName, a => (object?)a.Value);
        }
        catch { return []; }
    }

    private static List<Dictionary<string, object?>> ParseResources(string xml)
    {
        var doc = XDocument.Parse(xml);
        return doc.Descendants("Device").Select(d => {
            var r = d.Attributes().ToDictionary(a => a.Name.LocalName, a => (object?)a.Value);
            r["connections"] = d.Elements("Connection")
                .Select(c => c.Attributes().ToDictionary(a => a.Name.LocalName, a => (object?)a.Value))
                .ToList();
            return r;
        }).ToList();
    }

    private static List<string> RankConnections(Dictionary<string, object?> resource)
    {
        var connections = resource.GetValueOrDefault("connections") as List<Dictionary<string, object?>> ?? [];
        var ranked = connections
            .OrderBy(c => c.GetValueOrDefault("local", "")?.ToString() is not ("1" or "true"))
            .ThenBy(c => c.GetValueOrDefault("protocol", "")?.ToString() != "https")
            .Select(c => c.GetValueOrDefault("uri", "")?.ToString()?.TrimEnd('/') ?? "")
            .Where(u => !string.IsNullOrEmpty(u))
            .Distinct()
            .ToList();

        var uri = resource.GetValueOrDefault("uri", "")?.ToString()?.TrimEnd('/') ?? "";
        if (!string.IsNullOrEmpty(uri) && !ranked.Contains(uri)) ranked.Add(uri);
        return ranked;
    }

    private static bool CoerceBool(string? value) =>
        value?.ToLower() is "1" or "true" or "yes" or "on";
}
