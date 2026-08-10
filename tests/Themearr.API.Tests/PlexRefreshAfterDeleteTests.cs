using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// Sequel to issue #45 (PlexRefreshAfterDownloadTests): deleting a theme leaves Plex
/// playing its cached copy until the item is refreshed — the same staleness the
/// refresh-after-download fixed, in the opposite direction. Both delete endpoints must
/// ask Plex to refresh the item; a movie-side-only hook would rebuild the classic
/// movie/show parity miss.
///
/// The delete actions are synchronous (existing tests call them without await), so the
/// refresh is fire-and-forget: these tests poll for the recorded request rather than
/// awaiting a task the controller deliberately discards.
/// </summary>
public class PlexRefreshAfterDeleteTests
{
    private const string ServerUrl = "http://plex.local:32400";

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class NullProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// Records every request Plex would have seen. Thread-safe (ConcurrentQueue) because
    /// the delete-side refresh runs on a background task while the test thread polls.
    /// </summary>
    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Method, string Path, string? Token)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            r.Headers.TryGetValues("X-Plex-Token", out var tokens);
            Requests.Enqueue((r.Method.Method, r.RequestUri!.AbsolutePath, tokens?.FirstOrDefault()));
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    /// <summary>Models a Plex server that is down entirely (connection refused).</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
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

    private static MoviesController NewMovies(Database db, HttpMessageHandler plexHandler)
    {
        var config = new ConfigurationBuilder().Build();
        var download = new DownloadService(new NullProvider(), db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);
        var plex = new PlexService(new HttpClient(plexHandler), db, new LocalFolderResolver(db));
        return new MoviesController(
            db, new YoutubeService(), download, new PosterUrlSigner(new byte[32]),
            new LibrarySourceResolver(db, Array.Empty<ILibrarySource>()),
            NullLogger<MoviesController>.Instance, plex);
    }

    private static ShowsController NewShows(Database db, HttpMessageHandler plexHandler)
    {
        var config = new ConfigurationBuilder().Build();
        var download = new DownloadService(new NullProvider(), db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);
        var plex = new PlexService(new HttpClient(plexHandler), db, new LocalFolderResolver(db));
        return new ShowsController(db, new YoutubeService(), download, new PosterUrlSigner(new byte[32]),
            NullLogger<ShowsController>.Instance, plex)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static string AddMovie(Database db, TempDir dir, string source, string sourceRef, bool withTheme = true)
    {
        var folder = Path.Combine(dir.Path, "Test Movie");
        Directory.CreateDirectory(folder);
        if (withTheme) File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9, 9, 9]);
        db.UpsertMovies([new MovieRecord(folder, source, sourceRef, "Test Movie", 2020, "/plex/Test Movie/m.mkv")]);
        var id = MediaFolderId.For(folder);
        db.SetMovieStatus(id, "downloaded");
        return id;
    }

    private static string AddShow(Database db, TempDir dir, string sourceRef)
    {
        var folder = Path.Combine(dir.Path, "Test Show");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9, 9, 9]);
        db.UpsertShows([new ShowRecord(folder, "plex", sourceRef, "Test Show", 2019, "/plex/Test Show", false)]);
        var id = MediaFolderId.For(folder);
        db.SetShowStatus(id, "downloaded");
        return id;
    }

    /// <summary>Polls for the fire-and-forget refresh request; the deadline mirrors WaitForFinish.</summary>
    private static async Task<(string Method, string Path, string? Token)> WaitForPlexRequest(RecordingHandler handler)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (handler.Requests.TryPeek(out var req)) return req;
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("Plex never received the metadata-refresh request.");
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Movie_delete_asks_plex_to_refresh_the_item()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = AddMovie(db, dir, "plex", "srv1:45");

        var handler = new RecordingHandler();
        var result = Assert.IsType<OkObjectResult>(NewMovies(db, handler).DeleteTheme(id));

        Assert.True((bool)Prop(result.Value!, "deleted")!);
        Assert.False(ThemeFiles.HasUsableTheme(Path.Combine(dir.Path, "Test Movie")));
        Assert.Equal("pending", db.GetMovie(id)!["status"]);

        // The same item-scoped PUT the refresh-after-download sends, with the server's
        // token in the header (never the URI).
        var refresh = await WaitForPlexRequest(handler);
        Assert.Equal("PUT", refresh.Method);
        Assert.Equal("/library/metadata/45/refresh", refresh.Path);
        Assert.Equal("tok", refresh.Token);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Show_delete_asks_plex_to_refresh_the_item()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = AddShow(db, dir, "srv1:78");

        var handler = new RecordingHandler();
        var result = Assert.IsType<OkObjectResult>(NewShows(db, handler).DeleteTheme(id));

        Assert.True((bool)Prop(result.Value!, "deleted")!);
        Assert.False(ThemeFiles.HasUsableTheme(Path.Combine(dir.Path, "Test Show")));
        Assert.Equal("pending", db.GetShow(id)!["status"]);

        var refresh = await WaitForPlexRequest(handler);
        Assert.Equal("PUT", refresh.Method);
        Assert.Equal("/library/metadata/78/refresh", refresh.Path);
        Assert.Equal("tok", refresh.Token);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Radarr_movie_delete_skips_the_refresh()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        // Radarr's source_ref is Radarr's own movie id — no Plex ratingKey to refresh
        // with, so the honest behaviour is no Plex traffic at all.
        var id = AddMovie(db, dir, "radarr", "7");

        var handler = new RecordingHandler();
        var result = Assert.IsType<OkObjectResult>(NewMovies(db, handler).DeleteTheme(id));

        Assert.True((bool)Prop(result.Value!, "deleted")!);
        Assert.False(ThemeFiles.HasUsableTheme(Path.Combine(dir.Path, "Test Movie")));

        // The source gate runs before any HTTP is composed; the grace period only exists
        // to catch a regression where a request fires anyway.
        await Task.Delay(250);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true)]   // Plex answers 500
    [InlineData(false)]  // Plex is unreachable (handler throws)
    public async Task Refresh_failure_never_fails_the_delete(bool respondsWithError)
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = AddMovie(db, dir, "plex", "srv1:45");

        HttpMessageHandler handler = respondsWithError
            ? new RecordingHandler(HttpStatusCode.InternalServerError)
            : new ThrowingHandler();

        // The theme is already gone from disk by the time the refresh runs, so a Plex
        // failure must stay invisible to the caller: the delete succeeds regardless.
        var result = Assert.IsType<OkObjectResult>(NewMovies(db, handler).DeleteTheme(id));

        Assert.True((bool)Prop(result.Value!, "deleted")!);
        Assert.False(ThemeFiles.HasUsableTheme(Path.Combine(dir.Path, "Test Movie")));
        Assert.Equal("pending", db.GetMovie(id)!["status"]);

        // Drain the fire-and-forget refresh before TempDir teardown. A faulted discarded
        // task would be invisible to xUnit here, so the wrapper's never-faults contract
        // is pinned directly in PlexRefreshWrapperTests instead.
        await Task.Delay(250);
    }

    [Fact]
    public async Task Delete_with_no_theme_on_disk_does_not_ping_plex()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = AddMovie(db, dir, "plex", "srv1:45", withTheme: false);

        var handler = new RecordingHandler();
        var result = Assert.IsType<OkObjectResult>(NewMovies(db, handler).DeleteTheme(id));

        // Nothing was deleted, so Plex has nothing stale — no refresh traffic.
        Assert.False((bool)Prop(result.Value!, "deleted")!);
        await Task.Delay(250);
        Assert.Empty(handler.Requests);
    }
}
