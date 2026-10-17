using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class AuditCoordinationTests
{
    private const string Url = "https://www.youtube.com/watch?v=abc12345678";
    private sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    private static Database NewDb(TempDir dir) { var db = new Database(dir.File("test.db")); db.Init(); return db; }
    private static string Seed(Database db, TempDir dir, string title, string mediaType = "movie")
    {
        var folder = dir.File(title); Directory.CreateDirectory(folder);
        var id = MediaFolderId.For(folder);
        if (mediaType == "movie") { db.UpsertMovies([new(folder, "plex", id, title, 2021, folder)]); db.SetMovieStatus(id, "downloaded"); }
        else { db.UpsertShows([new(folder, "plex", id, title, 2021, folder, false)]); db.SetShowStatus(id, "downloaded"); }
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [1, 2, 3]);
        db.AddThemeHistory(id, title, 2021, title + " Official Trailer", Url, mediaType, "auto", 45);
        return id;
    }
    private sealed class PausingLog : ILogger<ThemeRecheckService>
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release = new();
        private int _calls;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { if (Interlocked.Increment(ref _calls) == 1) { Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(15)); } }
    }
    [Theory]
    [InlineData("movie", "keep")]
    [InlineData("movie", "ignore")]
    [InlineData("movie", "replacement")]
    [InlineData("movie", "delete")]
    [InlineData("show", "keep")]
    [InlineData("show", "ignore")]
    [InlineData("show", "replacement")]
    [InlineData("show", "delete")]
    public async Task Scan_does_not_overwrite_changes_after_its_snapshot(string mediaType, string action)
    {
        using var dir = new TempDir(); var db = NewDb(dir);
        Seed(db, dir, "A", mediaType); var id = Seed(db, dir, "B", mediaType);
        var logger = new PausingLog(); var scan = new ThemeRecheckService(db, logger);
        Assert.True(await scan.StartAsync());
        try
        {
            await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (action)
            {
                case "keep": db.SetRecheck(id, mediaType, "kept", null); break;
                case "ignore": if (mediaType == "movie") db.SetMovieIgnored(id, true); else db.SetShowIgnored(id, true); break;
                case "replacement": db.AddThemeHistory(id, "B", 2021, "B Official Trailer", Url, mediaType, "auto", 45); db.ClearRecheck(id, mediaType); break;
                case "delete": if (mediaType == "movie") db.SetMovieStatus(id, "pending"); else db.SetShowStatus(id, "pending"); db.ClearRecheck(id, mediaType); break;
            }
        }
        finally { logger.Release.Set(); await scan.Current; }
        Assert.DoesNotContain(db.GetFlaggedItems(), item => item.Id == id);
        if (action == "keep") Assert.Equal("kept", db.GetRecheckCandidates(mediaType).Single(item => item["id"]?.ToString() == id)["recheckState"]);
    }
    private sealed class BlockingProvider : IThemeAudioProvider
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CheckConfiguration() => null;
        public async Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            Entered.TrySetResult(); await Release.Task;
            File.WriteAllBytes(outputPath, [0x49, 0x44, 0x33, 9, 9, 9]); return "new theme";
        }
    }
    [Fact]
    public async Task Same_item_simultaneous_starts_accept_exactly_one()
    {
        using var dir = new TempDir(); var db = NewDb(dir); var id = Seed(db, dir, "Dune");
        var provider = new BlockingProvider();
        var download = new DownloadService(provider, db, new Factory(), new ConfigurationBuilder().Build(), NullLogger<DownloadService>.Instance);
        var url = "https://www.youtube.com/watch?v=" + new string('x', 1_000_000);
        using var barrier = new Barrier(12);
        var starts = Enumerable.Range(0, 12).Select(_ => Task.Factory.StartNew(() => { barrier.SignalAndWait(); return download.Start(id, url, "movie", "manual"); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try { await Task.WhenAll(starts); _ = Assert.Single(starts, task => task.Result); }
        finally { provider.Release.TrySetResult(); await WaitFinished(download, id); }
    }
    [Fact]
    public async Task Expired_provider_completion_cannot_replace_existing_theme_or_history()
    {
        using var dir = new TempDir(); var db = NewDb(dir); var id = Seed(db, dir, "Dune");
        var provider = new BlockingProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Themearr:DownloadTimeoutSeconds"] = "1" }).Build();
        var download = new DownloadService(provider, db, new Factory(), config, NullLogger<DownloadService>.Instance);
        Assert.True(download.Start(id, Url, "movie", "manual"));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(1200); provider.Release.TrySetResult(); await WaitFinished(download, id);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dir.File("Dune/theme.mp3")));
        Assert.Single(db.GetThemeHistory());
        Assert.Contains("timed out", Prop(download.GetStatus(id), "error")?.ToString());
    }
    private sealed class GenerationalProvider : IThemeAudioProvider
    {
        private int _calls;
        public TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CheckConfiguration() => null;
        public async Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            { FirstEntered.TrySetResult(); await FirstRelease.Task; progress("stale first progress"); throw new IOException("stale first error"); }
            SecondEntered.TrySetResult(); await SecondRelease.Task;
            File.WriteAllBytes(outputPath, [0x49, 0x44, 0x33, 8, 8, 8]); return "second theme";
        }
    }
    [Fact]
    public async Task Late_attempt_cannot_finish_or_append_to_a_newer_job()
    {
        using var dir = new TempDir(); var db = NewDb(dir); var id = Seed(db, dir, "Dune");
        var provider = new GenerationalProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Themearr:DownloadTimeoutSeconds"] = "1", ["Themearr:DownloadWatchdogGraceSeconds"] = "1" }).Build();
        var download = new DownloadService(provider, db, new Factory(), config, NullLogger<DownloadService>.Instance);
        Assert.True(download.Start(id, Url, "movie", "auto")); await provider.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(2200); Assert.True(download.Start(id, Url, "movie", "manual"));
            provider.FirstRelease.TrySetResult(); await provider.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 10; i++)
            {
                var status = download.GetStatus(id); Assert.Equal(true, Prop(status, "inProgress")); Assert.Equal(false, Prop(status, "finished")); Assert.Null(Prop(status, "error"));
                Assert.DoesNotContain((string[])Prop(status, "logs")!, line => line.Contains("stale first")); await Task.Delay(10);
            }
        }
        finally { provider.FirstRelease.TrySetResult(); provider.SecondRelease.TrySetResult(); await WaitFinished(download, id); }
        Assert.Equal("second theme", db.GetLatestThemeProvenance(id, "movie")!.ThemeTitle);
    }
    private static object? Prop(object value, string property) => value.GetType().GetProperty(property)?.GetValue(value);
    private static async Task WaitFinished(DownloadService download, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (Prop(download.GetStatus(id), "finished") is not true) await Task.Delay(10, timeout.Token);
    }
    [Fact]
    public async Task Accepted_trigger_remains_pending_until_its_result_is_recorded()
    {
        var registry = new TaskRegistry(); registry.Register("syncShows", "Sync Shows", TimeSpan.FromDays(1));
        registry.RecordRun("syncShows", DateTime.UtcNow.AddDays(-1), TimeSpan.Zero, "old result");
        Assert.True(registry.Trigger("syncShows"));
        Assert.Equal(true, Prop(registry.Snapshot().Single(), "IsPending"));
        await registry.WaitForTriggerAsync("syncShows", CancellationToken.None);
        Assert.Equal(true, Prop(registry.Snapshot().Single(), "IsPending"));
        registry.RecordRun("syncShows", DateTime.UtcNow, TimeSpan.Zero, "new result");
        Assert.Equal(false, Prop(registry.Snapshot().Single(), "IsPending"));
    }
    [Fact]
    public async Task Production_startup_rechecks_current_rules_after_setup()
    {
        using var dir = new TempDir(); var db = NewDb(dir); var id = Seed(db, dir, "Dune"); db.MarkSetupComplete();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Themearr.API"))) root = root.Parent;
        Assert.NotNull(root);
        var assembly = Path.Combine(root.FullName, "src", "Themearr.API", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "Themearr.API.dll");
        var info = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(assembly);
        info.Environment["DB_PATH"] = dir.File("test.db");
        info.Environment["THEMEARR_AUTH_TOKEN"] = "audit-test-token-0123456789";
        info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        using var process = System.Diagnostics.Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && db.GetSetting("match_rules_version", "") == "" && !process.HasExited) await Task.Delay(50);
            Assert.Equal(ThemeMatch.RulesVersion.ToString(), db.GetSetting("match_rules_version", ""));
            Assert.Contains(db.GetFlaggedItems(), row => row.Id == id);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
    }
    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Entered.TrySetResult(); await Release.Task; return new(HttpStatusCode.OK) { Content = new StringContent("<MediaContainer size=\"0\"/>") }; }
    }
    [Fact]
    public async Task Show_task_reports_running_during_real_fetch()
    {
        using var dir = new TempDir(); var db = NewDb(dir); db.MarkSetupComplete(); db.SetSetting("plex_access_token", "tok"); db.SetSetting("plex_client_identifier", "client");
        db.SetPlexServers([new() { ["id"] = "srv", ["url"] = "http://plex.local:32400", ["urls"] = new List<string> { "http://plex.local:32400" }, ["token"] = "tok" }]);
        db.SetSelectedShowLibraries(new() { ["srv"] = ["3"] });
        var handler = new HangingHandler(); var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        using var services = new ServiceCollection().AddSingleton(db).AddSingleton(plex).AddScoped<ShowSyncService>().AddSingleton<ILogger<ShowSyncService>>(NullLogger<ShowSyncService>.Instance).BuildServiceProvider();
        var registry = new TaskRegistry(); registry.Register("syncShows", "Sync Shows", TimeSpan.FromDays(1));
        var worker = new ShowAutoSyncService(services, registry, new PlexLibrarySource(plex, db, new Factory()), NullLogger<ShowAutoSyncService>.Instance);
        var run = worker.RunScheduledAsync(true, CancellationToken.None);
        try { await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(registry.Snapshot().Single().IsRunning); }
        finally { handler.Release.TrySetResult(); await run; }
        Assert.False(registry.Snapshot().Single().IsRunning);
    }
}
