using Themearr.API.Data;

namespace Themearr.API.Tests;

public class DeadSettingsTests
{
    [Fact]
    public void Init_removes_the_write_only_plex_server_settings_rows()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();

        // A legacy install carries these rows -- historically written on every server
        // save but read by nothing (the live token lives in plex_servers). Notably
        // plex_server_token is a redundant copy of a Plex credential, so clearing it
        // is a small hygiene win, not just tidiness.
        db.SetSetting("plex_server_url",   "http://old:32400");
        db.SetSetting("plex_server_token", "legacy-secret");
        db.SetSetting("plex_server_name",  "OldServer");

        // The next startup runs Init again and must clean them.
        db.Init();

        Assert.Equal("", db.GetSetting("plex_server_url"));
        Assert.Equal("", db.GetSetting("plex_server_token"));
        Assert.Equal("", db.GetSetting("plex_server_name"));
    }
}
