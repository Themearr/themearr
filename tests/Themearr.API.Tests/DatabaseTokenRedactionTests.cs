using Themearr.API.Data;

namespace Themearr.API.Tests;

public class DatabaseTokenRedactionTests
{
    private static Database NewDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "themearr-test-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = new Database(Path.Combine(dir, "themearr.db"));
        db.Init();
        return db;
    }

    private static Dictionary<string, object?> Server(string id, string token) => new()
    {
        ["id"] = id,
        ["name"] = "Home " + id,
        ["url"] = $"http://plex-{id}:32400",
        ["token"] = token,
    };

    [Fact]
    public void GetPlexServersRedacted_blanksToken_keepsOtherFields()
    {
        var db = NewDb();
        db.SetPlexServers([Server("s1", "PLEX-SECRET-1")]);

        var redacted = db.GetPlexServersRedacted();

        var srv = Assert.Single(redacted);
        Assert.Equal("s1", srv["id"]?.ToString());
        Assert.Equal("http://plex-s1:32400", srv["url"]?.ToString());
        Assert.True(string.IsNullOrEmpty(srv.GetValueOrDefault("token")?.ToString()));
    }

    [Fact]
    public void SetPlexServersMergingTokens_blankIncomingToken_preservesStoredToken()
    {
        var db = NewDb();
        db.SetPlexServers([Server("s1", "PLEX-SECRET-1")]);

        // Simulate the settings page saving back a redacted (token-less) server list.
        db.SetPlexServersMergingTokens([Server("s1", "")]);

        Assert.Equal("PLEX-SECRET-1", db.GetPlexServersDict()["s1"].Token);
    }

    [Fact]
    public void SetPlexServersMergingTokens_nonEmptyIncomingToken_updatesIt()
    {
        var db = NewDb();
        db.SetPlexServers([Server("s1", "OLD")]);

        db.SetPlexServersMergingTokens([Server("s1", "NEW")]);

        Assert.Equal("NEW", db.GetPlexServersDict()["s1"].Token);
    }

    [Fact]
    public void SetPlexServersMergingTokens_newServerWithoutStoredToken_staysBlank()
    {
        var db = NewDb();
        db.SetPlexServersMergingTokens([Server("s2", "")]);

        Assert.True(string.IsNullOrEmpty(db.GetPlexServersDict().GetValueOrDefault("s2").Token));
    }

    private static Dictionary<string, object?> ServerAt(string id, string url, string token) => new()
    {
        ["id"] = id,
        ["name"] = "Home " + id,
        ["url"] = url,
        ["token"] = token,
    };

    [Fact]
    public void SetPlexServersMergingTokens_sameIdSameUrl_blankToken_preservesStoredToken()
    {
        var db = NewDb();
        db.SetPlexServers([ServerAt("s1", "http://real-plex:32400", "PLEX-SECRET-1")]);

        db.SetPlexServersMergingTokens([ServerAt("s1", "http://real-plex:32400", "")]);

        Assert.Equal("PLEX-SECRET-1", db.GetPlexServersDict()["s1"].Token);
    }

    // The actual vulnerability this guards against: an attacker who can reach the
    // settings-save endpoint (e.g. with only the API key, not the master token) posts
    // the victim's real server id with a URL they control and no token, hoping the
    // stored token gets re-attached to their host. It must not.
    [Fact]
    public void SetPlexServersMergingTokens_sameIdDifferentUrl_blankToken_doesNotAttachStoredToken()
    {
        var db = NewDb();
        db.SetPlexServers([ServerAt("s1", "http://real-plex:32400", "PLEX-SECRET-1")]);

        db.SetPlexServersMergingTokens([ServerAt("s1", "http://attacker.example/", "")]);

        Assert.True(string.IsNullOrEmpty(db.GetPlexServersDict()["s1"].Token));
    }

    [Fact]
    public void SetPlexServersMergingTokens_explicitToken_isHonoured_regardlessOfUrl()
    {
        var db = NewDb();
        db.SetPlexServers([ServerAt("s1", "http://real-plex:32400", "OLD-SECRET")]);

        db.SetPlexServersMergingTokens([ServerAt("s1", "http://attacker.example/", "NEW-EXPLICIT-TOKEN")]);

        Assert.Equal("NEW-EXPLICIT-TOKEN", db.GetPlexServersDict()["s1"].Token);
    }

    [Fact]
    public void SetPlexServersMergingTokens_trailingSlashDifferenceOnly_stillPreservesStoredToken()
    {
        var db = NewDb();
        db.SetPlexServers([ServerAt("s1", "http://real-plex:32400", "PLEX-SECRET-1")]);

        db.SetPlexServersMergingTokens([ServerAt("s1", "http://real-plex:32400/", "")]);

        Assert.Equal("PLEX-SECRET-1", db.GetPlexServersDict()["s1"].Token);
    }
}
