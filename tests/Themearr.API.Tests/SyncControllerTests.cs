using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// Exercises <see cref="SyncController"/>'s start action against the real
/// <see cref="PlexLibrarySource"/> and <see cref="RadarrLibrarySource"/> — the bug this
/// guards against was the controller validating Plex configuration unconditionally, which
/// made the Movies page's sync button silently do nothing on a Radarr install.
/// </summary>
public class SyncControllerTests
{
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static (SyncController Controller, Database Db, SyncService Sync) New(TempDir dir)
    {
        var db      = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var factory = new StubHttpClientFactory();
        var plex    = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));
        var sources = new LibrarySourceResolver(db,
        [
            new PlexLibrarySource(plex, db, factory),
            new RadarrLibrarySource(db, new LocalFolderResolver(db), factory),
        ]);
        var sync = new SyncService(db, sources, NullLogger<SyncService>.Instance);
        return (new SyncController(sync, sources), db, sync);
    }

    [Fact]
    public async Task Plex_source_with_nothing_configured_is_rejected()
    {
        using var dir = new TempDir();
        var (controller, _, _) = New(dir);
        // library_source defaults to "plex"; no servers or libraries are selected.

        var result = Assert.IsType<BadRequestObjectResult>(await controller.StartSync());

        var detail = (string)result.Value!.GetType().GetProperty("detail")!.GetValue(result.Value)!;
        Assert.Equal("Plex sign-in is not complete", detail);
    }

    [Fact]
    public async Task Radarr_source_with_no_url_or_key_is_rejected()
    {
        using var dir = new TempDir();
        var (controller, db, _) = New(dir);
        db.SetSetting("library_source", "radarr");

        var result = Assert.IsType<BadRequestObjectResult>(await controller.StartSync());

        var detail = (string)result.Value!.GetType().GetProperty("detail")!.GetValue(result.Value)!;
        Assert.Contains("Radarr is not configured", detail);
    }

    [Fact]
    public async Task Radarr_source_fully_configured_is_accepted()
    {
        using var dir = new TempDir();
        var (controller, db, sync) = New(dir);
        db.SetSetting("library_source", "radarr");
        db.SetSetting("radarr_url", "http://localhost:7878");
        db.SetSetting("radarr_api_key", "a-key");

        var result = Assert.IsType<OkObjectResult>(await controller.StartSync());

        var started = (bool)result.Value!.GetType().GetProperty("started")!.GetValue(result.Value)!;
        Assert.True(started);

        // Let the background sync (which will fail against a nonexistent Radarr) finish
        // before the temp directory is torn down, rather than leaving it racing dispose.
        await sync.Current.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Plex_source_fully_configured_is_accepted()
    {
        using var dir = new TempDir();
        var (controller, db, sync) = New(dir);
        db.SetSetting("plex_selected_servers",
            """[{"id":"srv1","name":"Server","url":"http://localhost:32400","token":"tok"}]""");
        db.SetSetting("plex_selected_libraries", """{"srv1":["1"]}""");

        var result = Assert.IsType<OkObjectResult>(await controller.StartSync());

        var started = (bool)result.Value!.GetType().GetProperty("started")!.GetValue(result.Value)!;
        Assert.True(started);

        await sync.Current.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
