using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class DeleteDownloadRaceTests
{
    private const string Url = "https://www.youtube.com/watch?v=abcdefghijk";
    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    private sealed class HeldProvider : IThemeAudioProvider
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CheckConfiguration() => null;
        public async Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            File.WriteAllBytes(outputPath, [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 9]);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return "New theme";
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Database Db;
        public readonly HeldProvider Provider = new();
        public readonly DownloadService Download;
        public readonly string Folder;
        public readonly string Id;
        public Fixture(string media, bool existing, bool shared = false)
        {
            Db = new Database(Dir.File("test.db")); Db.Init();
            Folder = Dir.File("Library"); Directory.CreateDirectory(Folder); Id = MediaFolderId.For(Folder);
            Db.SetLibraryPaths([Dir.Path]);
            if (media == "movie" || shared) Db.UpsertMovies([new(Folder, "plex", "srv:1", "Item", 2020, Folder)]);
            if (media == "show" || shared) Db.UpsertShows([new(Folder, "plex", "srv:1", "Item", 2020, Folder, false)]);
            if (existing)
            {
                File.WriteAllBytes(Path.Combine(Folder, "theme.mp3"), [0x49, 0x44, 0x33, 1]);
                if (media == "movie" || shared) Db.SetMovieStatus(Id, "downloaded");
                if (media == "show" || shared) Db.SetShowStatus(Id, "downloaded");
            }
            Download = new(Provider, Db, new Factory(), new ConfigurationBuilder().Build(), NullLogger<DownloadService>.Instance);
        }
        public IActionResult Delete(string media) => media == "movie"
            ? new MoviesController(Db, new YoutubeService(), Download, new PosterUrlSigner(new byte[32]), null!, NullLogger<MoviesController>.Instance).DeleteTheme(Id)
            : new ShowsController(Db, new YoutubeService(), Download, new PosterUrlSigner(new byte[32]), NullLogger<ShowsController>.Instance).DeleteTheme(Id);
        public object? Scalar(string sql)
        {
            using var conn = new SqliteConnection($"Data Source={Dir.File("test.db")}"); conn.Open();
            using var cmd = conn.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
        }
        public void Execute(string sql) => Scalar(sql);
        public async Task<object> Finish(string media)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var status = Download.GetStatus(Id, media);
                if (Prop(status, "finished") is true) return status;
                await Task.Delay(10, timeout.Token);
            }
        }
        public void Dispose() { Provider.Release.TrySetResult(); Dir.Dispose(); }
    }
    private static object? Prop(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);

    [Theory]
    [InlineData("movie", true, "manual")]
    [InlineData("show", true, "manual")]
    [InlineData("movie", false, "auto")]
    [InlineData("show", false, "auto")]
    public async Task Delete_invalidates_a_download_already_staged_before_publication(string media, bool existing, string pickedBy)
    {
        using var f = new Fixture(media, existing);
        Assert.True(f.Download.Start(f.Id, Url, media, pickedBy));
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = Assert.IsType<OkObjectResult>(f.Delete(media));
        Assert.Equal(existing, Prop(result.Value!, "deleted"));
        f.Provider.Release.TrySetResult(); var status = await f.Finish(media);
        Assert.False(ThemeFiles.HasUsableTheme(f.Folder));
        Assert.Equal("pending", f.Scalar($"SELECT status FROM {(media == "movie" ? "movies" : "shows")} LIMIT 1"));
        Assert.Equal(0L, f.Scalar("SELECT COUNT(*) FROM theme_history"));
        Assert.NotNull(Prop(status, "error"));
        Assert.Empty(Directory.GetFiles(f.Folder, "*.part"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task Shared_folder_delete_invalidates_active_and_waiting_jobs_and_both_stored_rows(string deletingMedia)
    {
        using var f = new Fixture("movie", true, shared: true);
        Assert.True(f.Download.Start(f.Id, Url, "movie", "manual"));
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Download.Start(f.Id, Url, "show", "manual"));
        Assert.IsType<OkObjectResult>(f.Delete(deletingMedia));
        f.Provider.Release.TrySetResult();
        await f.Finish("movie"); await f.Finish("show");
        Assert.False(ThemeFiles.HasUsableTheme(f.Folder));
        Assert.Equal("pending", f.Scalar("SELECT status FROM movies LIMIT 1"));
        Assert.Equal("pending", f.Scalar("SELECT status FROM shows LIMIT 1"));
        Assert.Equal(0L, f.Scalar("SELECT COUNT(*) FROM theme_history"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task A_fresh_attempt_after_delete_can_publish_and_a_later_delete_resets_it(string media)
    {
        using var f = new Fixture(media, true);
        Assert.True(f.Download.Start(f.Id, Url, media, "manual"));
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Delete(media); f.Provider.Release.TrySetResult(); await f.Finish(media);
        Assert.True(f.Download.Start(f.Id, Url, media, "manual"));
        var status = await f.Finish(media);
        Assert.Null(Prop(status, "error"));
        Assert.True(File.Exists(Path.Combine(f.Folder, "theme.m4a")));
        Assert.Equal(1L, f.Scalar("SELECT COUNT(*) FROM theme_history"));
        Assert.IsType<OkObjectResult>(f.Delete(media));
        Assert.False(ThemeFiles.HasUsableTheme(f.Folder));
        Assert.Equal("pending", f.Scalar($"SELECT status FROM {(media == "movie" ? "movies" : "shows")} LIMIT 1"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task A_failed_database_reset_still_invalidates_the_older_download(string media)
    {
        using var f = new Fixture(media, true);
        Assert.True(f.Download.Start(f.Id, Url, media, "manual"));
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var table = media == "movie" ? "movies" : "shows";
        f.Execute($"CREATE TRIGGER fail_reset BEFORE UPDATE OF status ON {table} BEGIN SELECT RAISE(FAIL, 'reset rejected'); END;");
        Assert.Throws<SqliteException>(() => f.Delete(media));
        f.Execute("DROP TRIGGER fail_reset;");
        f.Provider.Release.TrySetResult(); await f.Finish(media);
        Assert.False(ThemeFiles.HasUsableTheme(f.Folder));
        Assert.Equal(0L, f.Scalar("SELECT COUNT(*) FROM theme_history"));
    }
}
