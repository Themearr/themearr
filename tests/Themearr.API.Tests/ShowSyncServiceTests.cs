using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowSyncServiceTests
{
    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }
    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    [Fact]
    public async Task RunOnce_upserts_selected_plex_shows_into_the_shows_table()
    {
        using var dir = new TempDir();
        var showRoot = Path.Combine(dir.Path, "The Wire"); Directory.CreateDirectory(showRoot);
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        db.SetSetting("plex_access_token", "tok"); db.SetSetting("plex_client_identifier", "c1");
        db.SetPlexServers([new Dictionary<string, object?> {
            ["id"] = "srv1", ["name"] = "Tower", ["url"] = "http://plex.local:32400",
            ["urls"] = new List<string> { "http://plex.local:32400" }, ["token"] = "tok" }]);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath switch
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
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var sut = new ShowSyncService(db, plex, NullLogger<ShowSyncService>.Instance);

        var synced = await sut.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, synced);
        Assert.Contains(db.GetAllShows(), s => (string)s["title"]! == "The Wire");
    }

    [Fact]
    public async Task RunOnce_does_nothing_when_no_show_libraries_are_selected()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db")); db.Init();
        db.SetSetting("plex_access_token", "tok"); db.SetSetting("plex_client_identifier", "c1");
        // no SetSelectedShowLibraries → opt-out
        var handler = new RoutingHandler(_ => throw new InvalidOperationException("should not call Plex"));
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var sut = new ShowSyncService(db, plex, NullLogger<ShowSyncService>.Instance);

        var synced = await sut.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, synced);
        Assert.Empty(db.GetAllShows());
    }
}
