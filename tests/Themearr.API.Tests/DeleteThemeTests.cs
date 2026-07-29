using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// Deleting a theme in-app must reset the movie's stored status to 'pending'. The
/// auto-download worker's cheap pre-filter (<see cref="Database.GetPendingMovies"/>) keys
/// off stored status, so without this reset an in-app delete on an auto-download install
/// would no longer be re-downloaded — and the stored column would also lie ('downloaded'
/// for a movie with no theme on disk).
/// </summary>
public class DeleteThemeTests
{
    private sealed class NullProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => "not configured (unused in this test)";
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static Database NewDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "themearr-test-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new Database(Path.Combine(dir, "themearr.db"));
        db.Init();
        return db;
    }

    private static MoviesController New(Database db)
    {
        var config = new ConfigurationBuilder().Build();
        // DeleteTheme uses only db + ThemeFiles; the rest are constructed but never touched.
        var download = new DownloadService(new NullProvider(), db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);
        return new MoviesController(
            db, new YoutubeService(), download, new PosterUrlSigner(new byte[32]),
            new LibrarySourceResolver(db, Array.Empty<ILibrarySource>()), NullLogger<MoviesController>.Instance);
    }

    [Fact]
    public void Deleting_a_theme_resets_status_so_the_worker_pre_filter_re_adopts_it()
    {
        using var folder = new TempDir();
        File.WriteAllBytes(Path.Combine(folder.Path, "theme.mp3"), new byte[] { 1, 2, 3 });

        var db = NewDb();
        db.UpsertMovies([new MovieRecord(folder.Path, "test", "1", "Movie", 2020, "")]);
        var id = MediaFolderId.For(folder.Path);
        db.SetMovieStatus(id, "downloaded");

        // Precondition: a downloaded movie is not in the worker's cheap pending set.
        Assert.DoesNotContain(db.GetPendingMovies(), m => m["id"]?.ToString() == id);

        var result = Assert.IsType<OkObjectResult>(New(db).DeleteTheme(id));

        Assert.False(ThemeFiles.HasUsableTheme(folder.Path));               // file removed
        Assert.Contains(db.GetPendingMovies(), m => m["id"]?.ToString() == id); // status reset → re-adopted
    }
}
