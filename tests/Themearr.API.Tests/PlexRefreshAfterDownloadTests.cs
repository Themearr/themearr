using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Issue #45: after a theme lands, Plex must be asked to refresh THAT item so the theme
/// starts playing without a manual "Refresh Metadata". The show-path test is the point:
/// Plex's partial-scan setting already picks up movie folders changing on most installs,
/// but show themes stay invisible until the show itself is refreshed — so a hook that
/// fires on the movie path alone would rebuild the reported bug.
/// </summary>
public class PlexRefreshAfterDownloadTests
{
    private const string YtUrl     = "https://www.youtube.com/watch?v=abc12345678";
    private const string ServerUrl = "http://plex.local:32400";

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>Records every request Plex would have seen; answers with a fixed status.</summary>
    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Token)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            r.Headers.TryGetValues("X-Plex-Token", out var tokens);
            Requests.Add((r.Method.Method, r.RequestUri!.AbsolutePath, tokens?.FirstOrDefault()));
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    /// <summary>Models a Plex server that is down entirely (connection refused).</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    /// <summary>Provider that writes a valid theme file, so the download itself succeeds.</summary>
    private sealed class WritingProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            File.WriteAllBytes(outputPath, new byte[] { 0x49, 0x44, 0x33, 9, 9, 9 });
            return Task.FromResult<string?>("Written Theme");
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetSetting("plex_client_identifier", "client-1");
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"] = "srv1", ["name"] = "Tower", ["url"] = ServerUrl,
            ["urls"] = new List<string> { ServerUrl }, ["token"] = "tok",
        }]);
        return db;
    }

    private static DownloadService Build(Database db, HttpMessageHandler plexHandler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Themearr:DownloadTimeoutSeconds"] = "900",
            })
            .Build();

        var plex = new PlexService(new HttpClient(plexHandler), db, new LocalFolderResolver(db));
        return new DownloadService(
            new WritingProvider(), db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance, plex);
    }

    private static async Task<object> WaitForFinish(DownloadService svc, string id, string mediaType)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var status = svc.GetStatus(id, mediaType);
            if ((bool)Prop(status, "finished")!) return status;
            await Task.Delay(50);
        }
        return svc.GetStatus(id, mediaType);
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Movie_download_asks_plex_to_refresh_the_item()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Test Movie");
        Directory.CreateDirectory(movieDir);
        var db = NewDb(dir);
        db.UpsertMovies([new MovieRecord(movieDir, "plex", "srv1:45", "Test Movie", 2020, "/plex/Test Movie/m.mkv")]);
        var movieId = MediaFolderId.For(movieDir);

        var handler = new RecordingHandler();
        var svc = Build(db, handler);

        Assert.True(svc.Start(movieId, YtUrl));
        var status = await WaitForFinish(svc, movieId, "movie");

        Assert.Null((string?)Prop(status, "error"));
        Assert.Equal("downloaded", db.GetMovie(movieId)!["status"]);

        // The item-scoped refresh Plex's own "Refresh Metadata" action sends, with the
        // server's token in the header (never the URI).
        var refresh = Assert.Single(handler.Requests);
        Assert.Equal("PUT", refresh.Method);
        Assert.Equal("/library/metadata/45/refresh", refresh.Path);
        Assert.Equal("tok", refresh.Token);

        var logs = (string[])Prop(status, "logs")!;
        Assert.Contains(logs, l => l.Contains("refresh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Show_download_asks_plex_to_refresh_the_item()
    {
        using var dir = new TempDir();
        var showDir = Path.Combine(dir.Path, "Test Show");
        Directory.CreateDirectory(showDir);
        var db = NewDb(dir);
        db.UpsertShows([new ShowRecord(showDir, "plex", "srv1:78", "Test Show", 2019, "/plex/Test Show", false)]);
        var showId = MediaFolderId.For(showDir);

        var handler = new RecordingHandler();
        var svc = Build(db, handler);

        Assert.True(svc.Start(showId, YtUrl, "show"));
        var status = await WaitForFinish(svc, showId, "show");

        Assert.Null((string?)Prop(status, "error"));

        var refresh = Assert.Single(handler.Requests);
        Assert.Equal("PUT", refresh.Method);
        Assert.Equal("/library/metadata/78/refresh", refresh.Path);
        Assert.Equal("tok", refresh.Token);
    }

    [Fact]
    public async Task Radarr_movie_skips_the_refresh()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Radarr Movie");
        Directory.CreateDirectory(movieDir);
        var db = NewDb(dir);
        // Radarr's source_ref is Radarr's own movie id — there is no Plex ratingKey to
        // refresh with, so the honest behaviour is no Plex traffic at all.
        db.UpsertMovies([new MovieRecord(movieDir, "radarr", "7", "Radarr Movie", 2021, "/movies/Radarr Movie")]);
        var movieId = MediaFolderId.For(movieDir);

        var handler = new RecordingHandler();
        var svc = Build(db, handler);

        Assert.True(svc.Start(movieId, YtUrl));
        var status = await WaitForFinish(svc, movieId, "movie");

        Assert.Null((string?)Prop(status, "error"));
        Assert.Equal("downloaded", db.GetMovie(movieId)!["status"]);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true)]   // Plex answers 500
    [InlineData(false)]  // Plex is unreachable (handler throws)
    public async Task Refresh_failure_never_fails_the_download(bool respondsWithError)
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Fragile Movie");
        Directory.CreateDirectory(movieDir);
        var db = NewDb(dir);
        db.UpsertMovies([new MovieRecord(movieDir, "plex", "srv1:45", "Fragile Movie", 2020, "/plex/Fragile Movie/m.mkv")]);
        var movieId = MediaFolderId.For(movieDir);

        HttpMessageHandler handler = respondsWithError
            ? new RecordingHandler(HttpStatusCode.InternalServerError)
            : new ThrowingHandler();
        var svc = Build(db, handler);

        Assert.True(svc.Start(movieId, YtUrl));
        var status = await WaitForFinish(svc, movieId, "movie");

        // The theme is already on disk: the refresh outcome must never turn a successful
        // download into a failed job.
        Assert.True((bool)Prop(status, "finished")!);
        Assert.Null((string?)Prop(status, "error"));
        Assert.Equal("downloaded", db.GetMovie(movieId)!["status"]);
    }

    [Fact]
    public async Task RefreshItemMetadataAsync_without_a_resolvable_identity_is_a_no_op()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var handler = new RecordingHandler();
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));

        Assert.False(await plex.RefreshItemMetadataAsync("radarr", "7"));        // not a Plex item
        Assert.False(await plex.RefreshItemMetadataAsync("plex", "no-colon"));   // no serverId:ratingKey shape
        Assert.False(await plex.RefreshItemMetadataAsync("plex", null));         // never synced a ref
        Assert.False(await plex.RefreshItemMetadataAsync("plex", "ghost:9"));    // server since removed

        Assert.Empty(handler.Requests);
    }
}
