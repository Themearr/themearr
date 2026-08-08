using System.Net;
using System.Text;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Fixtures here are copied from a real Plex server's responses, not invented.
///
/// The distinction matters: the original fixture put a &lt;Location&gt; inside the section
/// listing, which real Plex does NOT return. Every show therefore had no root folder,
/// was skipped, and show sync reported "synced 0 shows" on every install — while these
/// tests passed, because they were verifying the assumption rather than Plex's behaviour.
/// The show's folder is only available from /library/metadata/{ratingKey}.
/// </summary>
public class PlexFetchShowsTests
{
    private const string ServerUrl = "http://plex.local:32400";

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Paths.Add(r.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(r));
        }
    }
    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private static Database NewDb(TempDir dir)
    {
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
        return db;
    }

    private const string Sections = """
        <MediaContainer size="1"><Directory key="3" type="show" title="TV Programmes" /></MediaContainer>
        """;

    /// <summary>
    /// A real section listing: attributes and an &lt;Image&gt; child, and crucially NO
    /// &lt;Location&gt;. Verbatim in shape from /library/sections/{key}/all?type=2.
    /// </summary>
    private const string SectionItems = """
        <MediaContainer size="1" totalSize="1" librarySectionID="2" librarySectionTitle="TV Programmes">
          <Directory ratingKey="45" key="/library/metadata/45/children" type="show" title="Breaking Bad"
                     year="2008" theme="/library/metadata/45/theme/1784087688">
            <Image alt="Breaking Bad" type="coverPoster" url="/library/metadata/45/thumb/1" />
          </Directory>
        </MediaContainer>
        """;

    [Fact]
    public async Task Reads_the_show_root_folder_from_per_show_metadata()
    {
        using var dir = new TempDir();
        var showRoot = Path.Combine(dir.Path, "Breaking Bad");
        Directory.CreateDirectory(showRoot);
        var db = NewDb(dir);

        // The metadata endpoint is where Plex actually reports the folder. Note it has no
        // `id` attribute on <Location>, matching a real response.
        var metadata = $"""
            <MediaContainer size="1">
              <Directory ratingKey="45" type="show" title="Breaking Bad">
                <Location path="{showRoot}" />
              </Directory>
            </MediaContainer>
            """;

        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/library/sections"           => Xml(Sections),
            "/library/sections/3/all"     => Xml(SectionItems),
            "/library/metadata/45"        => Xml(metadata),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var shows = await plex.FetchShowsAsync();

        var s = Assert.Single(shows);
        Assert.Equal("Breaking Bad", s.Title);
        Assert.Equal(showRoot, s.Folder);
        Assert.True(s.HasPlexTheme);                       // theme= survives the section listing
        Assert.Contains("/library/metadata/45", handler.Paths);
    }

    /// <summary>
    /// If a Plex build ever does include &lt;Location&gt; in the listing, use it and skip the
    /// extra round trip — one request per show is the expensive part of this fetch.
    /// </summary>
    [Fact]
    public async Task Uses_a_listing_Location_when_present_without_a_metadata_call()
    {
        using var dir = new TempDir();
        var showRoot = Path.Combine(dir.Path, "The Wire");
        Directory.CreateDirectory(showRoot);
        var db = NewDb(dir);

        var itemsWithLocation = $"""
            <MediaContainer size="1" totalSize="1">
              <Directory ratingKey="46" type="show" title="The Wire" year="2002">
                <Location path="{showRoot}" />
              </Directory>
            </MediaContainer>
            """;

        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/library/sections"       => Xml(Sections),
            "/library/sections/3/all" => Xml(itemsWithLocation),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var shows = await plex.FetchShowsAsync();

        Assert.Equal(showRoot, Assert.Single(shows).Folder);
        Assert.DoesNotContain("/library/metadata/46", handler.Paths);
    }

    /// <summary>
    /// The failure that hid this bug for two releases: shows came back from Plex, every one
    /// was dropped for having no folder, and the counter stayed at 0 — so the sync reported
    /// "0 shows, 0 unresolved" and looked like an empty library rather than a fault.
    /// A show Plex returns but Themearr cannot place must always be counted.
    /// </summary>
    [Fact]
    public async Task Counts_shows_whose_folder_cannot_be_determined()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);

        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/library/sections"       => Xml(Sections),
            "/library/sections/3/all" => Xml(SectionItems),
            // Metadata reachable but carrying no Location — folder is undeterminable.
            "/library/metadata/45"    => Xml("""<MediaContainer size="1"><Directory ratingKey="45" /></MediaContainer>"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var shows = await plex.FetchShowsAsync();

        Assert.Empty(shows);
        Assert.Equal("1", db.GetSetting("last_show_sync_unresolved_count", "0"));
    }

    /// <summary>A metadata lookup that fails must not abort the whole sync — the other
    /// shows still import, and the failed one is counted rather than silently dropped.</summary>
    [Fact]
    public async Task A_failed_metadata_lookup_does_not_abort_the_sync()
    {
        using var dir = new TempDir();
        var wire = Path.Combine(dir.Path, "The Wire");
        Directory.CreateDirectory(wire);
        var db = NewDb(dir);

        var twoShows = """
            <MediaContainer size="2" totalSize="2">
              <Directory ratingKey="45" type="show" title="Breaking Bad" year="2008" />
              <Directory ratingKey="46" type="show" title="The Wire" year="2002" />
            </MediaContainer>
            """;
        var wireMeta = $"""
            <MediaContainer size="1"><Directory ratingKey="46"><Location path="{wire}" /></Directory></MediaContainer>
            """;

        var handler = new RoutingHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/library/sections"       => Xml(Sections),
            "/library/sections/3/all" => Xml(twoShows),
            "/library/metadata/45"    => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "/library/metadata/46"    => Xml(wireMeta),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var shows = await plex.FetchShowsAsync();

        Assert.Equal("The Wire", Assert.Single(shows).Title);
        Assert.Equal("1", db.GetSetting("last_show_sync_unresolved_count", "0"));
    }
}
