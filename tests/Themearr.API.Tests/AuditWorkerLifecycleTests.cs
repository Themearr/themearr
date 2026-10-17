using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class AuditWorkerLifecycleTests
{
    private static Database NewDb(TempDir dir) { var db = new Database(dir.File("test.db")); db.Init(); return db; }
    private static string Seed(Database db, TempDir dir, string title, string pickedBy, bool kept = false)
    {
        var folder = dir.File(title); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [1, 2, 3]);
        var id = MediaFolderId.For(folder); db.UpsertMovies([new(folder, "plex", id, title, 2021, folder)]); db.SetMovieStatus(id, "downloaded");
        db.AddThemeHistory(id, title, 2021, title + " Official Trailer", "https://youtu.be/abcdefghijk", "movie", pickedBy, 45);
        if (kept) db.SetRecheck(id, "movie", "kept", null);
        return id;
    }
    [Fact]
    public async Task Rules_upgrade_waits_for_setup_and_preserves_kept_manual_and_media()
    {
        using var dir = new TempDir(); var db = NewDb(dir);
        var kept = Seed(db, dir, "A", "auto", true); var manual = Seed(db, dir, "B", "manual"); var auto = Seed(db, dir, "C", "auto");
        var scan = new ThemeRecheckService(db, NullLogger<ThemeRecheckService>.Instance);
        var worker = new AutoRecheckService(db, scan, NullLogger<AutoRecheckService>.Instance);
        await worker.RunScheduledAsync(CancellationToken.None);
        Assert.Equal("", db.GetSetting("match_rules_version", "")); Assert.Empty(db.GetFlaggedItems());
        db.MarkSetupComplete(); await worker.RunScheduledAsync(CancellationToken.None);
        Assert.Equal(ThemeMatch.RulesVersion.ToString(), db.GetSetting("match_rules_version", ""));
        Assert.Equal("kept", db.GetRecheckCandidates("movie").Single(row => row["id"]?.ToString() == kept)["recheckState"]);
        Assert.Equal("review", db.GetFlaggedItems().Single(row => row.Id == manual).State);
        Assert.Equal("queued", db.GetFlaggedItems().Single(row => row.Id == auto).State);
        foreach (var title in new[] { "A", "B", "C" }) Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dir.File(title + "/theme.mp3")));
        Assert.Equal("false", db.GetSetting("auto_download", "false"));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Current_or_newer_rules_marker_does_not_scan_again(int delta)
    {
        using var dir = new TempDir(); var db = NewDb(dir); db.MarkSetupComplete(); Seed(db, dir, "Dune", "auto");
        db.SetSetting("match_rules_version", (ThemeMatch.RulesVersion + delta).ToString());
        var scan = new ThemeRecheckService(db, NullLogger<ThemeRecheckService>.Instance);
        var worker = new AutoRecheckService(db, scan, NullLogger<AutoRecheckService>.Instance);
        await worker.RunScheduledAsync(CancellationToken.None);
        Assert.Empty(db.GetFlaggedItems()); Assert.Equal((ThemeMatch.RulesVersion + delta).ToString(), db.GetSetting("match_rules_version", ""));
    }
    private sealed class FailOnceLog : ILogger<ThemeRecheckService>
    {
        private bool _failed;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Information && !_failed) { _failed = true; throw new IOException("injected interrupted scan"); } }
    }
    [Fact]
    public async Task Failed_full_scan_retains_version_and_is_retried()
    {
        using var dir = new TempDir(); var db = NewDb(dir); db.MarkSetupComplete(); Seed(db, dir, "A", "auto"); Seed(db, dir, "B", "auto");
        var scan = new ThemeRecheckService(db, new FailOnceLog());
        var worker = new AutoRecheckService(db, scan, NullLogger<AutoRecheckService>.Instance);
        await worker.RunScheduledAsync(CancellationToken.None);
        Assert.False(scan.Succeeded); Assert.Equal("", db.GetSetting("match_rules_version", ""));
        await worker.RunScheduledAsync(CancellationToken.None);
        Assert.True(scan.Succeeded); Assert.Equal(ThemeMatch.RulesVersion.ToString(), db.GetSetting("match_rules_version", "")); Assert.Equal(2, db.GetFlaggedItems().Count);
    }
    private sealed class PausingLog : ILogger<ThemeRecheckService>
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release = new(); private int _calls;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (Interlocked.Increment(ref _calls) == 1) { Entered.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(10)); } }
    }
    [Fact]
    public async Task Cancellation_stops_scan_before_remaining_rows_without_advancing_version()
    {
        using var dir = new TempDir(); var db = NewDb(dir); db.MarkSetupComplete(); Seed(db, dir, "A", "auto"); var b = Seed(db, dir, "B", "auto");
        var logger = new PausingLog(); var scan = new ThemeRecheckService(db, logger);
        var worker = new AutoRecheckService(db, scan, NullLogger<AutoRecheckService>.Instance);
        using var cts = new CancellationTokenSource(); var run = worker.RunScheduledAsync(cts.Token);
        try { await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        finally { logger.Release.Set(); await scan.Current; }
        Assert.False(scan.Succeeded); Assert.DoesNotContain(db.GetFlaggedItems(), row => row.Id == b); Assert.Equal("", db.GetSetting("match_rules_version", ""));
    }
    [Fact]
    public async Task Completing_one_run_preserves_an_additional_queued_trigger()
    {
        var registry = new TaskRegistry(); registry.Register("task", "Task", TimeSpan.FromHours(1));
        Assert.True(registry.Trigger("task")); await registry.WaitForTriggerAsync("task", CancellationToken.None);
        Assert.True(registry.Trigger("task")); registry.RecordRun("task", DateTime.UtcNow, TimeSpan.Zero, "first");
        Assert.True(registry.Snapshot().Single().IsPending);
        await registry.WaitForTriggerAsync("task", CancellationToken.None); registry.RecordFailure("task", "failed");
        Assert.False(registry.Snapshot().Single().IsPending);
    }
    [Theory]
    [InlineData("syncShows")]
    [InlineData("syncLibrary")]
    public async Task Manual_run_during_application_warmup_publishes_its_outcome(string taskId)
    {
        using var dir = new TempDir(); var db = NewDb(dir); db.MarkSetupComplete();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Themearr.API"))) root = root.Parent;
        Assert.NotNull(root);
        var assembly = Path.Combine(root.FullName, "src", "Themearr.API", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0", "Themearr.API.dll");
        // A reserved loopback port is released just before Kestrel binds it.
        var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); socket.Start(); var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var info = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(assembly); info.Environment["DB_PATH"] = dir.File("test.db"); info.Environment["THEMEARR_AUTH_TOKEN"] = "audit-test-token-0123456789"; info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        using var process = System.Diagnostics.Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(1) }; client.DefaultRequestHeaders.Authorization = new("Bearer", "audit-test-token-0123456789");
        try
        {
            TaskState? state = null; var readyDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < readyDeadline && state == null)
            {
                try { state = (await client.GetFromJsonAsync<TaskState[]>("/api/system/tasks"))?.Single(row => row.Id == taskId); }
                catch (HttpRequestException) { await Task.Delay(25); }
            }
            Assert.NotNull(state); using var response = await client.PostAsync($"/api/system/tasks/{taskId}/run", null); Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            do { state = (await client.GetFromJsonAsync<TaskState[]>("/api/system/tasks"))!.Single(row => row.Id == taskId); if (!state.IsPending && !state.IsRunning && state.LastResult != null) break; await Task.Delay(25); } while (DateTime.UtcNow < deadline);
            Assert.False(state.IsPending); Assert.False(state.IsRunning); Assert.NotNull(state.LastResult);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
    }
}
