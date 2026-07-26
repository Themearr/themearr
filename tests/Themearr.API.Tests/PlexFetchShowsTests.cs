using System.Net;
using System.Text;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexFetchShowsTests
{
    private const string ServerUrl = "http://plex.local:32400";

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }
    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    [Fact]
    public async Task Fetches_shows_from_selected_libraries_with_resolved_root_and_theme_flag()
    {
        using var dir = new TempDir();
        // A real local show root so LocalFolderResolver resolves it "direct".
        var showRoot = Path.Combine(dir.Path, "Breaking Bad");
        Directory.CreateDirectory(showRoot);

        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetSetting("plex_access_token", "tok");
        db.SetSetting("plex_client_identifier", "client-1");
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"] = "srv1", ["name"] = "Tower", ["url"] = ServerUrl,
            ["urls"] = new List<string> { ServerUrl }, ["token"] = "tok",
        }]);
        db.SetSelectedShowLibraries(new() { ["srv1"] = ["3"] });

        var sections = """
            <MediaContainer size="1"><Directory key="3" type="show" title="TV" /></MediaContainer>
            """;
        var items = $"""
            <MediaContainer size="1" totalSize="1">
              <Directory ratingKey="45" type="show" title="Breaking Bad" year="2008"
                         theme="/library/metadata/45/theme/1">
                <Location id="1" path="{showRoot}" />
              </Directory>
            </MediaContainer>
            """;
        var handler = new RoutingHandler(req =>
        {
            var p = req.RequestUri!.AbsolutePath;
            if (p == "/library/sections") return Xml(sections);
            if (p == "/library/sections/3/all") return Xml(items);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var shows = await plex.FetchShowsAsync();

        var s = Assert.Single(shows);
        Assert.Equal("Breaking Bad", s.Title);
        Assert.Equal(showRoot, s.Folder);   // resolved local root
        Assert.True(s.HasPlexTheme);
    }
}
