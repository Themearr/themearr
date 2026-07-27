using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class DownloadServiceShowTests
{
    private const string YtUrl = "https://www.youtube.com/watch?v=abc12345678";

    /// <summary>Writes a valid theme file and reports a title, like the movie tests' RecordingProvider.</summary>
    private sealed class RecordingProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            File.WriteAllBytes(outputPath, new byte[] { 0x49, 0x44, 0x33, 9, 9, 9 });
            return Task.FromResult<string?>("Show Theme");
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static object? Prop(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o);

    private static Database NewDb()
    {
        var dbDir = Path.Combine(Path.GetTempPath(), "themearr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        var db = new Database(Path.Combine(dbDir, "themearr.db"));
        db.Init();
        return db;
    }

    private static DownloadService NewService(Database db, IThemeAudioProvider provider)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Themearr:DownloadTimeoutSeconds"] = "900" }).Build();
        return new DownloadService(provider, db, new StubHttpClientFactory(), config, NullLogger<DownloadService>.Instance);
    }

    private static async Task<object> WaitForFinishAsync(DownloadService svc, string id, string mediaType)
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
    public async Task Show_download_writes_theme_sets_show_status_and_records_show_history()
    {
        using var showDir = new TempDir();
        var db = NewDb();
        var showId = MediaFolderId.For(showDir.Path);
        db.UpsertShows([new ShowRecord(showDir.Path, "plex", "srv1:45", "Test Show", 2010, "/plex/Test Show", false)]);

        var svc = NewService(db, new RecordingProvider());

        Assert.True(svc.Start(showId, YtUrl, "show"));
        var status = await WaitForFinishAsync(svc, showId, "show");

        Assert.True((bool)Prop(status, "finished")!);
        Assert.Null((string?)Prop(status, "error"));
        Assert.True(File.Exists(Path.Combine(showDir.Path, "theme.mp3")));

        // GetShow derives status from the filesystem, so it would say "downloaded" even
        // if the stored column were never written. GetPendingShows reads the stored
        // column directly — that is what actually pins SetShowStatus having been called.
        Assert.Empty(db.GetPendingShows());
        Assert.Equal("downloaded", db.GetShow(showId)!["status"]);

        var entry = db.GetThemeHistory().Single(h => (string)h["movieId"]! == showId);
        Assert.Equal("show", entry["mediaType"]);
        Assert.Equal("Test Show", entry["movieTitle"]);
    }

    /// <summary>
    /// A show download must not touch the movies table, and vice versa — the two id
    /// spaces share the same MediaFolderId hash, so routing by media type is the only
    /// thing keeping them apart.
    /// </summary>
    [Fact]
    public async Task Show_download_does_not_touch_the_movies_table()
    {
        using var dir = new TempDir();
        var db = NewDb();
        var showId = MediaFolderId.For(dir.Path);
        db.UpsertShows([new ShowRecord(dir.Path, "plex", "srv1:45", "Shared Folder", 2010, "/plex/Shared", false)]);
        // A movie sharing the SAME folder → the SAME id. Only the media type separates them.
        db.UpsertMovies([new MovieRecord(dir.Path, "srv1", "rk1", "Shared Folder", 2010, "/plex/Shared/x.mkv")]);

        var svc = NewService(db, new RecordingProvider());
        Assert.True(svc.Start(showId, YtUrl, "show"));
        await WaitForFinishAsync(svc, showId, "show");

        Assert.Empty(db.GetPendingShows());
        // The movie row's stored status must be untouched, so it is still pending.
        Assert.Contains(db.GetPendingMovies(), m => (string)m["id"]! == showId);
    }

    /// <summary>
    /// Job state is keyed by "{mediaType}:{id}", so a movie download in flight must not
    /// make a same-id show download look finished (or block it).
    /// </summary>
    [Fact]
    public void Job_state_is_namespaced_by_media_type()
    {
        using var dir = new TempDir();
        var db = NewDb();
        var id = MediaFolderId.For(dir.Path);
        db.UpsertShows([new ShowRecord(dir.Path, "plex", "srv1:45", "Shared", 2010, "/plex/Shared", false)]);
        db.UpsertMovies([new MovieRecord(dir.Path, "srv1", "rk1", "Shared", 2010, "/plex/Shared/x.mkv")]);

        var svc = NewService(db, new RecordingProvider());

        Assert.True(svc.Start(id, YtUrl, "show"));
        // Same id, different media type — must be accepted as its own job, not deduped.
        Assert.True(svc.Start(id, YtUrl));
    }
}
