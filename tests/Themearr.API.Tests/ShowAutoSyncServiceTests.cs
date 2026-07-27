using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the scheduler's decision logic through the RunScheduledAsync seam. The 30-minute
/// timer loop itself is deliberately not tested — same level as AutoSyncService.
/// </summary>
public class ShowAutoSyncServiceTests
{
    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(r));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private static (ShowAutoSyncService sut, TaskRegistry registry) Build(Database db, HttpMessageHandler handler)
    {
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(plex);
        services.AddScoped<ShowSyncService>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ShowSyncService>>(
            NullLogger<ShowSyncService>.Instance);
        var sp = services.BuildServiceProvider();

        var registry    = new TaskRegistry();
        var plexSource  = new PlexLibrarySource(plex, db, new StubHttpClientFactory());
        var sut = new ShowAutoSyncService(sp, registry, plexSource, NullLogger<ShowAutoSyncService>.Instance);
        registry.Register(ShowAutoSyncService.SyncTaskId, "Sync Shows", TimeSpan.FromHours(24));
        return (sut, registry);
    }

    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetSetting("plex_access_token", "tok");
        db.SetSetting("plex_client_identifier", "c1");
        db.MarkSetupComplete();
        return db;
    }

    /// <summary>Selects a show library and serves one show from it.</summary>
    private static CountingHandler PlexServing(Database db, string showRoot)
    {
        db.SetPlexServers([new Dictionary<string, object?> {
            ["id"] = "srv1", ["name"] = "Tower", ["url"] = "http://plex.local:32400",
            ["urls"] = new List<string> { "http://plex.local:32400" }, ["token"] = "tok" }]);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        return new CountingHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/library/sections" => Xml("""<MediaContainer size="1"><Directory key="3" type="show" title="TV"/></MediaContainer>"""),
            "/library/sections/3/all" => Xml($"""
                <MediaContainer size="1" totalSize="1">
                  <Directory ratingKey="46" type="show" title="The Wire" year="2002">
                    <Location id="1" path="{showRoot}"/>
                  </Directory>
                </MediaContainer>
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    [Fact]
    public async Task Forced_run_with_no_show_libraries_selected_is_a_safe_no_op()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        // No show libraries selected → ShowSyncService's opt-in guard returns 0 without
        // ever reaching Plex, so a handler that throws proves no call was made.
        var handler = new CountingHandler(_ => throw new InvalidOperationException("should not call Plex"));
        var (sut, registry) = Build(db, handler);

        await sut.RunScheduledAsync(forced: true, CancellationToken.None);

        Assert.Empty(db.GetAllShows());
        Assert.Equal(0, handler.Calls);
        var task = registry.Snapshot().Single(t => t.Id == ShowAutoSyncService.SyncTaskId);
        Assert.Equal("synced 0 shows", task.LastResult);
    }

    [Fact]
    public async Task Unforced_run_does_nothing_when_auto_sync_is_off()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var showRoot = Path.Combine(dir.Path, "The Wire"); Directory.CreateDirectory(showRoot);
        var handler = PlexServing(db, showRoot);
        // auto_sync deliberately left off
        var (sut, _) = Build(db, handler);

        await sut.RunScheduledAsync(forced: false, CancellationToken.None);

        Assert.Equal(0, handler.Calls);
        Assert.Empty(db.GetAllShows());
    }

    [Fact]
    public async Task Forced_run_syncs_shows_and_records_the_task()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var showRoot = Path.Combine(dir.Path, "The Wire"); Directory.CreateDirectory(showRoot);
        var handler = PlexServing(db, showRoot);
        var (sut, registry) = Build(db, handler);

        await sut.RunScheduledAsync(forced: true, CancellationToken.None);

        Assert.Contains(db.GetAllShows(), s => (string)s["title"]! == "The Wire");
        var task = registry.Snapshot().Single(t => t.Id == ShowAutoSyncService.SyncTaskId);
        Assert.Equal("synced 1 shows", task.LastResult);
    }

    /// <summary>
    /// The show sync must keep its own schedule clock. Sharing the movie sync's
    /// last_auto_sync_at would make either sync suppress the other for a full day.
    /// </summary>
    [Fact]
    public async Task Uses_its_own_timestamp_key_and_leaves_the_movie_sync_clock_alone()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var showRoot = Path.Combine(dir.Path, "The Wire"); Directory.CreateDirectory(showRoot);
        var (sut, _) = Build(db, PlexServing(db, showRoot));

        await sut.RunScheduledAsync(forced: true, CancellationToken.None);

        Assert.NotEqual("", db.GetSetting("last_show_auto_sync_at", ""));
        Assert.Equal("", db.GetSetting("last_auto_sync_at", ""));
    }

    /// <summary>
    /// ShowSyncService has no internal try/catch, so an unreachable Plex would otherwise
    /// take down the scheduler loop. It must be recorded as a failure instead.
    /// </summary>
    [Fact]
    public async Task A_failing_sync_is_caught_and_recorded_rather_than_thrown()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetPlexServers([new Dictionary<string, object?> {
            ["id"] = "srv1", ["name"] = "Tower", ["url"] = "http://plex.local:32400",
            ["urls"] = new List<string> { "http://plex.local:32400" }, ["token"] = "tok" }]);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });
        var handler = new CountingHandler(_ => throw new HttpRequestException("Plex is down"));
        var (sut, registry) = Build(db, handler);

        await sut.RunScheduledAsync(forced: true, CancellationToken.None);   // must not throw

        var task = registry.Snapshot().Single(t => t.Id == ShowAutoSyncService.SyncTaskId);
        Assert.Contains("failed", task.LastResult!);
        // A run that failed must not advance the schedule clock.
        Assert.Equal("", db.GetSetting("last_show_auto_sync_at", ""));
    }

    /// <summary>An unforced run inside the interval must not re-sync.</summary>
    [Fact]
    public async Task Unforced_run_respects_the_sync_interval()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("auto_sync", "true");
        db.SetSetting("last_show_auto_sync_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        var showRoot = Path.Combine(dir.Path, "The Wire"); Directory.CreateDirectory(showRoot);
        var handler = PlexServing(db, showRoot);
        var (sut, _) = Build(db, handler);

        await sut.RunScheduledAsync(forced: false, CancellationToken.None);

        Assert.Equal(0, handler.Calls);   // synced seconds ago — 24h has not elapsed
    }
}
