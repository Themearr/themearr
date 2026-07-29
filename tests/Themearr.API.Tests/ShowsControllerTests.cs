using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowsControllerTests
{
    private sealed class NullProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    internal static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    internal static string AddShow(Database db, TempDir dir, string title, bool plexHasTheme = false)
    {
        var folder = Path.Combine(dir.Path, title);
        Directory.CreateDirectory(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", $"srv1:{title}", title, 2010, folder, plexHasTheme)]);
        return MediaFolderId.For(folder);
    }

    internal static ShowsController New(Database db)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var download = new DownloadService(new NullProvider(), db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);
        return new ShowsController(db, new YoutubeService(), download, new PosterUrlSigner([1, 2, 3]),
            NullLogger<ShowsController>.Instance)
        {
            // GetThemeAudio sets response cache headers, and `Response` throws without an
            // HttpContext. A real request always has one; a directly-constructed controller
            // does not, so supply a stand-in.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public void ListShows_returns_status_plexHasTheme_and_a_show_poster_url()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = AddShow(db, dir, "PlexThemed", plexHasTheme: true);

        var result = Assert.IsType<OkObjectResult>(New(db).ListShows());
        var shows = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(result.Value);
        var row = shows.Single(s => (string)s["id"]! == id);

        Assert.Equal("plexTheme", row["status"]);
        Assert.Equal(true, row["plexHasTheme"]);
        Assert.StartsWith("/api/poster/show?", (string)row["posterUrl"]!);
    }

    [Fact]
    public void ListShows_gives_no_poster_url_when_the_show_has_no_source_ref()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var folder = Path.Combine(dir.Path, "NoRef"); Directory.CreateDirectory(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", "", "NoRef", 2010, folder, false)]);

        var result = Assert.IsType<OkObjectResult>(New(db).ListShows());
        var shows = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(result.Value);

        Assert.Null(shows.Single(s => (string)s["title"]! == "NoRef")["posterUrl"]);
    }

    [Fact]
    public async Task Search_404s_for_an_unknown_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);

        var result = await New(db).SearchYoutube("nope");

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
