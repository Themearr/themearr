using System.Net;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
namespace Themearr.API.Tests;

public class PlexReloginCompatibilityTests
{
    private static Dictionary<string, object?> Server(string token) => new()
    {
        ["id"] = "server", ["url"] = "http://plex.example:32400", ["token"] = token,
        ["urls"] = new List<string> { "http://plex.example:32400" },
    };
    private static Database Seed(TempDir dir)
    {
        var db = new Database(dir.File("relogin.db")); db.Init();
        db.SetPlexServers([Server("expired")]);
        db.SetDiscoveredPlexServers([Server("fresh")]);
        return db;
    }
    [Fact]
    public async Task Redacted_wizard_library_lookup_uses_newly_discovered_token_after_relogin()
    {
        using var dir = new TempDir(); var db = Seed(dir);
        var plex = new PlexService(new HttpClient(new Handler()), db, new LocalFolderResolver(db));
        var result = await new SetupController(db, plex).PlexLibraries(new() { Servers = [Server("")] });
        Assert.IsType<OkObjectResult>(result);
    }
    [Fact]
    public void Redacted_wizard_selection_replaces_expired_selected_token_after_relogin()
    {
        using var dir = new TempDir(); var db = Seed(dir);
        var plex = new PlexService(new HttpClient(new Handler()), db, new LocalFolderResolver(db));
        var result = new SetupController(db, plex).SaveSelection(new()
        { Servers = [Server("")], SelectedLibraries = new() { ["server"] = ["1"] } });
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("fresh", db.GetPlexServersDict()["server"].Token);
    }
    [Fact]
    public void Ordinary_redacted_settings_save_does_not_restore_a_stale_discovery_token()
    {
        using var dir = new TempDir(); var db = Seed(dir);
        db.SetPlexServers([Server("operator-current")]);
        db.SetPlexServersMergingTokens([Server("")]);
        Assert.Equal("operator-current", db.GetPlexServersDict()["server"].Token);
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(request.Headers.GetValues("X-Plex-Token").Single() == "fresh" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            { Content = new StringContent("<MediaContainer><Directory key='1' type='movie' title='Movies'/></MediaContainer>") });
    }
}
