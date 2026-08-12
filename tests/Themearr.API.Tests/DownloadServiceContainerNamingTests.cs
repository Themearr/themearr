using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins that a finished download is named by the container actually received (issue
/// #48): the youtube-mp36 provider demonstrably delivers AAC/MP4 bytes in production
/// (<c>format_name=mov,mp4,m4a</c> probed inside a file called theme.mp3), and the bytes
/// are stored as received — no transcode — so a hardcoded name is simply wrong for those
/// downloads. These tests bind DownloadService's real path, not the sniffing helper, so
/// they go red if the rename is unplugged while every ThemeFiles-bound test stays green —
/// the #39 wiring lesson applied to files instead of scores.
/// </summary>
public class DownloadServiceContainerNamingTests
{
    private const string YtUrl = "https://www.youtube.com/watch?v=abc12345678";

    private static readonly byte[] Mp4Bytes =
        [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
         (byte)'M', (byte)'4', (byte)'A', (byte)' ', 9, 9, 9, 9];

    private static readonly byte[] Mp3Bytes = [0x49, 0x44, 0x33, 9, 9, 9];

    /// <summary>Writes the given bytes to outputPath — what the wire delivered.</summary>
    private sealed class BytesProvider(byte[] bytes) : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            File.WriteAllBytes(outputPath, bytes);
            return Task.FromResult<string?>("Delivered Theme");
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static object? Prop(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o);

    private static (DownloadService svc, Database db, string movieId) Build(string movieFolder, byte[] deliveredBytes)
    {
        var dbDir = Path.Combine(Path.GetTempPath(), "themearr-test-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        var db = new Database(Path.Combine(dbDir, "themearr.db"));
        db.Init();

        var movieId = MediaFolderId.For(movieFolder);
        db.UpsertMovies([new MovieRecord(movieFolder, "srv1", "rk1", "Test Movie", 2020, "/plex/Test Movie/movie.mkv")]);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Themearr:DownloadTimeoutSeconds"] = "900" }).Build();
        var svc = new DownloadService(new BytesProvider(deliveredBytes), db, new StubHttpClientFactory(),
            config, NullLogger<DownloadService>.Instance);
        return (svc, db, movieId);
    }

    private static async Task<object> WaitForFinish(DownloadService svc, string movieId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        var status = svc.GetStatus(movieId);
        while (!(bool)Prop(status, "finished")! && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            status = svc.GetStatus(movieId);
        }
        return status;
    }

    [Fact]
    public async Task Mp4Delivery_landsAsThemeM4a_notThemeMp3()
    {
        using var movieDir = new TempDir();
        var (svc, db, movieId) = Build(movieDir.Path, Mp4Bytes);

        Assert.True(svc.Start(movieId, YtUrl));
        var status = await WaitForFinish(svc, movieId);

        Assert.True((bool)Prop(status, "finished")!);
        Assert.Null((string?)Prop(status, "error"));
        Assert.True(File.Exists(movieDir.File("theme.m4a")));
        Assert.False(File.Exists(movieDir.File("theme.mp3")));

        // The success tail must be untouched by the rename: status stored, history kept.
        Assert.Equal("downloaded", db.GetMovie(movieId)!["status"]);
        Assert.Single(db.GetThemeHistory(), h => (string)h["movieId"]! == movieId);
    }

    [Fact]
    public async Task Mp3Delivery_staysThemeMp3()
    {
        // The control: genuinely-MPEG deliveries keep their name, so nothing changes for
        // the downloads whose extension was already telling the truth.
        using var movieDir = new TempDir();
        var (svc, _, movieId) = Build(movieDir.Path, Mp3Bytes);

        Assert.True(svc.Start(movieId, YtUrl));
        var status = await WaitForFinish(svc, movieId);

        Assert.Null((string?)Prop(status, "error"));
        Assert.True(File.Exists(movieDir.File("theme.mp3")));
        Assert.False(File.Exists(movieDir.File("theme.m4a")));
    }

    [Fact]
    public async Task RedownloadChangingContainer_mp3ToM4a_replacesCleanly()
    {
        // A previously-downloaded theme.mp3 exists; the re-download delivers MP4. The
        // folder must end with exactly the corrected file — no stale sibling for
        // FindThemeFile to pick nondeterministically.
        using var movieDir = new TempDir();
        movieDir.Write("theme.mp3", Mp3Bytes);
        var (svc, _, movieId) = Build(movieDir.Path, Mp4Bytes);

        Assert.True(svc.Start(movieId, YtUrl));
        var status = await WaitForFinish(svc, movieId);

        Assert.Null((string?)Prop(status, "error"));
        var themeFiles = Directory.GetFiles(movieDir.Path, "theme.*");
        Assert.Equal([movieDir.File("theme.m4a")], themeFiles);
        Assert.Equal(Mp4Bytes, File.ReadAllBytes(movieDir.File("theme.m4a")));
    }

    [Fact]
    public async Task RedownloadChangingContainer_m4aToMp3_replacesCleanly()
    {
        // The reverse direction rides the pre-existing sibling cleanup: the new
        // theme.mp3 lands, the stale theme.m4a is removed, never the other way around.
        using var movieDir = new TempDir();
        movieDir.Write("theme.m4a", Mp4Bytes);
        var (svc, _, movieId) = Build(movieDir.Path, Mp3Bytes);

        Assert.True(svc.Start(movieId, YtUrl));
        var status = await WaitForFinish(svc, movieId);

        Assert.Null((string?)Prop(status, "error"));
        var themeFiles = Directory.GetFiles(movieDir.Path, "theme.*");
        Assert.Equal([movieDir.File("theme.mp3")], themeFiles);
        Assert.Equal(Mp3Bytes, File.ReadAllBytes(movieDir.File("theme.mp3")));
    }
}
