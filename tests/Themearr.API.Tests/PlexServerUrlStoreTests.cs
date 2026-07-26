using Themearr.API.Data;

namespace Themearr.API.Tests;

public class PlexServerUrlStoreTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"] = "srv1", ["name"] = "Tower",
            ["url"] = "https://old.plex.direct:32400",
            ["urls"] = new List<string> { "https://old.plex.direct:32400" },
            ["token"] = "tok-123",
        }]);
        return db;
    }

    [Fact]
    public void UpdatePlexServerUrl_sets_the_url_and_collapses_urls_and_keeps_the_token()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);

        var ok = db.UpdatePlexServerUrl("srv1", "http://192.168.1.50:32400");

        Assert.True(ok);
        var srv = db.GetPlexServersDict()["srv1"];
        Assert.Equal("http://192.168.1.50:32400", srv.Url);
        Assert.Equal("tok-123", srv.Token);   // token stayed bound to the new url
    }

    [Fact]
    public void UpdatePlexServerUrl_returns_false_for_an_unknown_server()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);

        Assert.False(db.UpdatePlexServerUrl("nope", "http://192.168.1.50:32400"));
    }
}
