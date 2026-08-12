using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the "never faults" contract of BOTH refresh wrappers — the property the
/// call sites lean on: the delete endpoints discard the wrapper's task (a fault there
/// would be an unobserved exception), and DownloadService's success tail awaits its
/// wrapper inside RunAsync's try (a fault there would report a successfully landed
/// theme as a failed download).
///
/// The 500/unreachable cases in the sibling test files never reach the wrappers'
/// catch-alls: RefreshItemMetadataAsync absorbs HttpRequestException and
/// OperationCanceledException itself (PlexService.cs:543). These tests use a failure
/// that ESCAPES that narrow filter — a malformed stored server URL, which throws
/// UriFormatException when the HttpRequestMessage is constructed — so each test goes
/// red the moment its wrapper's catch-all disappears.
/// </summary>
public class PlexRefreshWrapperTests
{
    private const string YtUrl = "https://www.youtube.com/watch?v=abc12345678";
    // A space in the authority makes URI construction itself throw — a failure class
    // the inner catch filter does not absorb.
    private const string MalformedServerUrl = "http://plex local:32400";

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
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

    private static Database NewDbWithMalformedServerUrl(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetSetting("plex_client_identifier", "client-1");
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"] = "srv1", ["name"] = "Tower", ["url"] = MalformedServerUrl,
            ["urls"] = new List<string> { MalformedServerUrl }, ["token"] = "tok",
        }]);
        return db;
    }

    [Fact]
    public async Task Delete_side_wrapper_never_faults_when_the_failure_escapes_the_inner_filter()
    {
        using var dir = new TempDir();
        var db = NewDbWithMalformedServerUrl(dir);
        var plex = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));

        // Must complete rather than fault: the delete endpoints fire-and-forget this
        // task, so a fault here is invisible in a response and unobservable in xUnit —
        // this direct await is the only place the contract can be pinned.
        await plex.TryRefreshItemMetadataAsync("plex", "srv1:45", NullLogger.Instance, "movie-1");
    }

    [Fact]
    public async Task Download_side_wrapper_contains_a_failure_that_escapes_the_inner_filter()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Test Movie");
        Directory.CreateDirectory(movieDir);
        var db = NewDbWithMalformedServerUrl(dir);
        db.UpsertMovies([new MovieRecord(movieDir, "plex", "srv1:45", "Test Movie", 2020, "/plex/Test Movie/m.mkv")]);
        var movieId = MediaFolderId.For(movieDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Themearr:DownloadTimeoutSeconds"] = "900" })
            .Build();
        var plex = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));
        var svc = new DownloadService(new WritingProvider(), db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance, plex);

        Assert.True(svc.Start(movieId, YtUrl));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        object status = svc.GetStatus(movieId, "movie");
        while (DateTime.UtcNow < deadline && !(bool)Prop(status, "finished")!)
        {
            await Task.Delay(50);
            status = svc.GetStatus(movieId, "movie");
        }

        // The theme landed; if the wrapper's catch-all vanished, the UriFormatException
        // would fall into RunAsync's outer catch and report this success as a failure.
        Assert.True((bool)Prop(status, "finished")!);
        Assert.Null((string?)Prop(status, "error"));
        Assert.Equal("downloaded", db.GetMovie(movieId)!["status"]);
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);
}
