using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class WorkerDeleteReconciliationRaceTests
{
    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    private sealed class Provider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string id, string path, Action<string> progress, CancellationToken ct) => Task.FromResult<string?>(null);
    }
    private sealed class EmptySearch : YoutubeService
    {
        public override Task<List<Dictionary<string, object?>>> SearchAsync(string query, int maxResults = 8, string? title = null, int? year = null) => Task.FromResult(new List<Dictionary<string, object?>>());
    }
    private sealed class PausingLog<T> : ILogger<T>
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (level != LogLevel.Debug || !format(state, error).StartsWith("Existing theme found for ")) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release adoption");
        }
    }
    private static object? Scalar(TempDir dir, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dir.File("test.db")}"); conn.Open();
        using var cmd = conn.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar();
    }
    private static (Database db, string folder, string id) Seed(TempDir dir, string media)
    {
        var db = new Database(dir.File("test.db")); db.Init(); db.MarkSetupComplete(); db.SetSetting("auto_download", "true"); db.SetLibraryPaths([dir.Path]);
        var folder = dir.File("Item"); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [1, 2, 3]);
        var id = MediaFolderId.For(folder);
        if (media == "movie") { db.UpsertMovies([new(folder, "plex", id, "Item", 2020, folder)]); db.SetMovieStatus(id, "pending"); }
        else { db.UpsertShows([new(folder, "plex", id, "Item", 2020, folder, false)]); db.SetShowStatus(id, "pending"); }
        return (db, folder, id);
    }
    private static IActionResult Delete(Database db, DownloadService download, string id, string media) => media == "movie"
        ? new MoviesController(db, new EmptySearch(), download, new PosterUrlSigner(new byte[32]), null!, NullLogger<MoviesController>.Instance).DeleteTheme(id)
        : new ShowsController(db, new EmptySearch(), download, new PosterUrlSigner(new byte[32]), NullLogger<ShowsController>.Instance).DeleteTheme(id);
    private static Task Tick(AutoDownloadService worker) => (Task)typeof(AutoDownloadService)
        .GetMethod("TryAutoDownloadOne", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(worker, null)!;

    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task Delete_during_worker_adoption_cannot_leave_a_downloaded_row_without_a_file(string media)
    {
        using var dir = new TempDir(); var (db, folder, id) = Seed(dir, media); var provider = new Provider();
        var download = new DownloadService(provider, db, new Factory(), new ConfigurationBuilder().Build(), NullLogger<DownloadService>.Instance);
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton<YoutubeService>(new EmptySearch()).BuildServiceProvider();
        var movieLog = new PausingLog<AutoDownloadService>(); var showLog = new PausingLog<ShowAutoDownloadService>();
        var entered = media == "movie" ? movieLog.Entered : showLog.Entered;
        var release = media == "movie" ? movieLog.Release : showLog.Release;
        var tick = Task.Run(() => media == "movie"
            ? Tick(new AutoDownloadService(services, download, provider, movieLog))
            : new ShowAutoDownloadService(services, download, provider, showLog).TryDownloadOnceAsync(CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<OkObjectResult>(Delete(db, download, id, media));
        }
        finally { release.Set(); await tick.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.False(ThemeFiles.HasUsableTheme(folder));
        Assert.Equal("pending", Scalar(dir, $"SELECT status FROM {(media == "movie" ? "movies" : "shows")} LIMIT 1"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task A_worker_reconciles_a_dirty_folder_even_when_the_stored_prefilter_says_downloaded(string media)
    {
        using var dir = new TempDir(); var (db, folder, id) = Seed(dir, media); var provider = new Provider();
        var table = media == "movie" ? "movies" : "shows";
        Scalar(dir, $"UPDATE {table} SET status = 'downloaded';");
        var download = new DownloadService(provider, db, new Factory(), new ConfigurationBuilder().Build(), NullLogger<DownloadService>.Instance);
        Scalar(dir, $"CREATE TRIGGER fail_reset BEFORE UPDATE OF status ON {table} BEGIN SELECT RAISE(FAIL, 'reset rejected'); END;");
        Assert.Throws<SqliteException>(() => Delete(db, download, id, media));
        Scalar(dir, "DROP TRIGGER fail_reset;");
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton<YoutubeService>(new EmptySearch()).BuildServiceProvider();
        if (media == "movie") await Tick(new AutoDownloadService(services, download, provider, NullLogger<AutoDownloadService>.Instance));
        else await new ShowAutoDownloadService(services, download, provider, NullLogger<ShowAutoDownloadService>.Instance).TryDownloadOnceAsync(CancellationToken.None);
        Assert.False(ThemeFiles.HasUsableTheme(folder));
        Assert.Equal("pending", Scalar(dir, $"SELECT status FROM {table} LIMIT 1"));
    }
}
