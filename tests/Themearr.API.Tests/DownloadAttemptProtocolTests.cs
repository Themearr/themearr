using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class DownloadAttemptProtocolTests
{
    internal const string Url = "https://www.youtube.com/watch?v=abcdefghijk";
    internal sealed class Factory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    internal sealed class HeldProvider : IThemeAudioProvider
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail;
        public string? CheckConfiguration() => null;
        public async Task<string?> DownloadAsync(string id, string path, Action<string> progress, CancellationToken ct)
        {
            progress("this attempt's progress"); Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            if (Fail) throw new IOException("provider rejected download");
            File.WriteAllBytes(path, [0x49, 0x44, 0x33, 1]); return "Theme";
        }
    }
    internal sealed class Fixture : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Database Db;
        public readonly HeldProvider Provider = new();
        public readonly DownloadService Service;
        public readonly string Id;
        public Fixture()
        {
            Db = new(Dir.File("test.db")); Db.Init(); var folder = Dir.File("Media"); Directory.CreateDirectory(folder);
            Id = MediaFolderId.For(folder); Db.SetLibraryPaths([Dir.Path]);
            Db.UpsertMovies([new(folder, "plex", "srv:1", "Item", 2020, folder)]);
            Db.UpsertShows([new(folder, "plex", "srv:1", "Item", 2020, folder, false)]);
            Service = new(Provider, Db, new Factory(), new ConfigurationBuilder().Build(), NullLogger<DownloadService>.Instance);
        }
        public async Task<JsonElement> Finish(string media)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var s = Snapshot(Service.GetStatus(Id, media));
                if (s.GetProperty("finished").GetBoolean()) return s;
                await Task.Delay(10, cts.Token);
            }
        }
        public void Dispose() { Provider.Release.TrySetResult(); Dir.Dispose(); }
    }
    internal static JsonElement Snapshot(object status) => JsonSerializer.SerializeToElement(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Untouched_key_explicitly_reports_unknown_without_claiming_completion()
    {
        using var f = new Fixture(); var status = Snapshot(f.Service.GetStatus(f.Id));
        Assert.True(status.TryGetProperty("known", out var known), "status must expose known");
        Assert.False(known.GetBoolean()); Assert.Equal("unknown", status.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("attemptId").ValueKind);
        Assert.False(status.GetProperty("inProgress").GetBoolean()); Assert.False(status.GetProperty("finished").GetBoolean());
    }

    [Theory]
    [InlineData("movie", false)]
    [InlineData("show", false)]
    [InlineData("movie", true)]
    [InlineData("show", true)]
    public async Task Running_and_terminal_snapshots_keep_the_same_nonempty_identity(string media, bool fail)
    {
        using var f = new Fixture(); f.Provider.Fail = fail;
        Assert.True(f.Service.Start(f.Id, Url, media, "manual"));
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = Snapshot(f.Service.GetStatus(f.Id, media));
        try
        {
            Assert.True(running.TryGetProperty("attemptId", out var id), "accepted job must expose its identity");
            Assert.NotEqual(Guid.Empty, id.GetGuid()); Assert.Equal("running", running.GetProperty("state").GetString());
        }
        finally { f.Provider.Release.TrySetResult(); await f.Finish(media); }
        var done = await f.Finish(media);
        Assert.Equal(running.GetProperty("attemptId").GetGuid(), done.GetProperty("attemptId").GetGuid());
        Assert.Equal(fail ? "failed" : "succeeded", done.GetProperty("state").GetString());
    }
    [Fact]
    public async Task Shared_folder_pending_and_running_attempts_have_distinct_media_identities()
    {
        using var f = new Fixture();
        var movie = f.Service.StartAttempt(f.Id, Url, "movie", "manual");
        await f.Provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var show = f.Service.StartAttempt(f.Id, Url, "show", "manual");
            Assert.True(show.Started); Assert.NotEqual(movie.AttemptId, show.AttemptId);
            var pending = f.Service.GetAttemptStatus(f.Id, "show", show.AttemptId);
            Assert.Equal("pending", pending.State); Assert.True(pending.InProgress);
            Assert.True(f.Service.IsAnyInProgress());
            Assert.Equal("running", f.Service.GetAttemptStatus(f.Id, "movie", movie.AttemptId).State);
        }
        finally { f.Provider.Release.TrySetResult(); await f.Finish("movie"); await f.Finish("show"); }
    }

    [Fact]
    public async Task Concurrent_admission_returns_one_reserved_identity_and_no_identity_on_conflict()
    {
        using var f = new Fixture();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => f.Service.StartAttempt(f.Id, Url, "movie", "manual"))));
        try
        {
            var accepted = Assert.Single(results.Where(r => r.Started));
            Assert.NotNull(accepted.AttemptId); Assert.NotEqual(Guid.Empty, accepted.AttemptId);
            Assert.All(results.Where(r => !r.Started), r => Assert.Null(r.AttemptId));
            Assert.Equal(accepted.AttemptId, f.Service.GetAttemptStatus(f.Id, "movie", null).AttemptId);
        }
        finally { f.Provider.Release.TrySetResult(); await f.Finish("movie"); }
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task Retry_returns_new_identity_and_old_attempt_reads_cannot_consume_its_logs_or_result(string media)
    {
        using var f = new Fixture(); f.Provider.Release.TrySetResult();
        var first = f.Service.StartAttempt(f.Id, Url, media, "manual"); await f.Finish(media);
        var second = f.Service.StartAttempt(f.Id, Url, media, "manual"); await f.Finish(media);
        Assert.True(second.Started); Assert.NotEqual(first.AttemptId, second.AttemptId);
        var old = f.Service.GetAttemptStatus(f.Id, media, first.AttemptId);
        Assert.Equal("superseded", old.State); Assert.Equal(first.AttemptId, old.AttemptId);
        Assert.Equal(second.AttemptId, old.CurrentAttemptId); Assert.False(old.Finished); Assert.False(old.InProgress);
        Assert.Null(old.Error); Assert.Empty(old.Logs);
        var current = f.Service.GetAttemptStatus(f.Id, media, second.AttemptId);
        Assert.Equal("succeeded", current.State); Assert.True(current.Finished); Assert.NotEmpty(current.Logs);
        using var restarted = new Fixture();
        var lost = restarted.Service.GetAttemptStatus(f.Id, media, second.AttemptId);
        Assert.False(lost.Known); Assert.Equal("unknown", lost.State); Assert.Null(lost.AttemptId);
    }

}
