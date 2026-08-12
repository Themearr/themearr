using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the per-folder landing gate (#48 concurrency review, F1/F2). Job single-flight
/// is keyed {mediaType}:{id}, so a movie and a show sharing one folder — a real,
/// supported configuration — are two jobs mutating the same theme.* files. Unserialized,
/// the two landings share the in-flight part name and each one's name-dependent cleanup
/// can delete the other's freshly landed theme; the measured worst interleaving ends
/// with ZERO themes while both jobs record "downloaded". The gate serializes
/// write → promote → cleanup per canonical folder, making the outcome the sequential
/// one: the last landing wins, and exactly one theme file survives.
///
/// Interleavings are held open with TaskCompletionSource stubs, never timed sleeps: job
/// A parks inside its provider (holding the gate) until the test releases it, so every
/// ordering below is forced, not raced.
/// </summary>
public class DownloadServiceFolderGateTests
{
    private const string UrlA = "https://www.youtube.com/watch?v=aaaaaaaaaaa";
    private const string UrlB = "https://www.youtube.com/watch?v=bbbbbbbbbbb";

    private static readonly byte[] Mp3Bytes = [0x49, 0x44, 0x33, 9, 9, 9];
    private static readonly byte[] Mp4Bytes =
        [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
         (byte)'M', (byte)'4', (byte)'A', (byte)' '];

    /// <summary>
    /// Routes by videoId: job A ("a…") writes MP3 bytes then parks until released; job B
    /// ("b…") writes MP4 bytes and returns at once. The test owns every release point.
    /// </summary>
    private sealed class RoutingProvider : IThemeAudioProvider
    {
        public readonly TaskCompletionSource AStarted  = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource BStarted  = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? CheckConfiguration() => null;

        public async Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            if (videoId.StartsWith('a'))
            {
                File.WriteAllBytes(outputPath, Mp3Bytes);
                AStarted.TrySetResult();
                await AReleased.Task.WaitAsync(ct);
            }
            else
            {
                BStarted.TrySetResult();
                File.WriteAllBytes(outputPath, Mp4Bytes);
            }
            return "Routed Theme";
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static object? Prop(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o);

    private static async Task<object> WaitForFinish(DownloadService svc, string id, string mediaType)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        var status = svc.GetStatus(id, mediaType);
        while (!(bool)Prop(status, "finished")! && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            status = svc.GetStatus(id, mediaType);
        }
        return status;
    }

    [Fact]
    public async Task MovieAndShowSharingAFolder_landingsAreSerialized_neverZeroThemes()
    {
        using var dir = new TempDir();
        var dbDir = Path.Combine(Path.GetTempPath(), "themearr-test-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        var db = new Database(Path.Combine(dbDir, "themearr.db"));
        db.Init();

        // The same folder under both media types → the same MediaFolderId, two job keys.
        var id = MediaFolderId.For(dir.Path);
        db.UpsertMovies([new MovieRecord(dir.Path, "srv1", "rk1", "Shared", 2020, "/plex/Shared/x.mkv")]);
        db.UpsertShows([new ShowRecord(dir.Path, "plex", "srv1:45", "Shared", 2020, "/plex/Shared", false)]);

        var provider = new RoutingProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Themearr:DownloadTimeoutSeconds"] = "900" }).Build();
        var svc = new DownloadService(provider, db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);

        Assert.True(svc.Start(id, UrlA));              // movie job A
        await provider.AStarted.Task;                  // A holds the folder gate, mid-download

        Assert.True(svc.Start(id, UrlB, "show"));      // show job B, same folder

        // With the gate, B parks BEFORE its provider runs and nothing can release it
        // until A finishes, so BStarted CANNOT complete yet — the bounded wait expires
        // as a matter of proof, not timing. Without the gate (the reverted world the
        // teeth-check exercises), B's provider runs promptly; we then let B finish
        // before releasing A, which deterministically reproduces the cross-landing
        // corruption: B consumes the shared part, and A's promote finds nothing.
        var bRanEarly = await Task.WhenAny(provider.BStarted.Task, Task.Delay(500)) == provider.BStarted.Task;
        if (bRanEarly)
            await WaitForFinish(svc, id, "show");

        provider.AReleased.TrySetResult();
        var statusA = await WaitForFinish(svc, id, "movie");
        var statusB = await WaitForFinish(svc, id, "show");

        // Serialized landings behave like sequential downloads: A lands theme.mp3 and
        // finishes; B then lands theme.m4a and its gated cleanup replaces A's file.
        // Exactly one theme survives — never zero, which is what the unserialized
        // cleanups produced — and both jobs are honest successes.
        Assert.Null((string?)Prop(statusA, "error"));
        Assert.Null((string?)Prop(statusB, "error"));
        Assert.Equal([dir.File("theme.m4a")], Directory.GetFiles(dir.Path, "theme.*"));
        Assert.Equal(Mp4Bytes, File.ReadAllBytes(dir.File("theme.m4a")));
        Assert.Equal("downloaded", db.GetMovie(id)!["status"]);
        Assert.Equal("downloaded", db.GetShow(id)!["status"]);
        Assert.Equal(2, db.GetThemeHistory().Count(h => (string)h["movieId"]! == id));
    }

    [Fact]
    public async Task MovieThenShowSequentially_sameFolder_containerChangeReplacesCleanly()
    {
        // The benign ordering of the same configuration: no concurrency, two media
        // types, one folder, container change between them. The show's landing must
        // replace the movie's m4a with its mp3 — one theme, both recorded.
        using var dir = new TempDir();
        var dbDir = Path.Combine(Path.GetTempPath(), "themearr-test-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        var db = new Database(Path.Combine(dbDir, "themearr.db"));
        db.Init();

        var id = MediaFolderId.For(dir.Path);
        db.UpsertMovies([new MovieRecord(dir.Path, "srv1", "rk1", "Shared", 2020, "/plex/Shared/x.mkv")]);
        db.UpsertShows([new ShowRecord(dir.Path, "plex", "srv1:45", "Shared", 2020, "/plex/Shared", false)]);

        var provider = new RoutingProvider();
        provider.AReleased.TrySetResult();   // A never parks: both jobs run straight through
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Themearr:DownloadTimeoutSeconds"] = "900" }).Build();
        var svc = new DownloadService(provider, db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);

        Assert.True(svc.Start(id, UrlB, "show"));      // show lands theme.m4a
        Assert.Null((string?)Prop(await WaitForFinish(svc, id, "show"), "error"));
        Assert.True(File.Exists(dir.File("theme.m4a")));

        Assert.True(svc.Start(id, UrlA));              // movie lands theme.mp3 over it
        Assert.Null((string?)Prop(await WaitForFinish(svc, id, "movie"), "error"));

        Assert.Equal([dir.File("theme.mp3")], Directory.GetFiles(dir.Path, "theme.*"));
        Assert.Equal(Mp3Bytes, File.ReadAllBytes(dir.File("theme.mp3")));
        Assert.Equal(2, db.GetThemeHistory().Count(h => (string)h["movieId"]! == id));
    }
}
