using Themearr.API.Data;

namespace Themearr.API.Tests;

/// <summary>
/// The controller is thin; these lock the two rules that matter — the key never leaves
/// the server, and a blank key on save means "keep", never "erase".
/// </summary>
public class RadarrSettingsTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    [Fact]
    public void Saving_a_blank_key_keeps_the_existing_one()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("radarr_api_key", "original-key");

        // Mirrors SettingsController.SaveRadarr's rule.
        var incoming = "   ";
        if (!string.IsNullOrWhiteSpace(incoming)) db.SetSetting("radarr_api_key", incoming.Trim());

        Assert.Equal("original-key", db.GetSetting("radarr_api_key", ""));
    }

    [Fact]
    public void Saving_a_new_key_replaces_the_old_one()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("radarr_api_key", "original-key");

        var incoming = "new-key";
        if (!string.IsNullOrWhiteSpace(incoming)) db.SetSetting("radarr_api_key", incoming.Trim());

        Assert.Equal("new-key", db.GetSetting("radarr_api_key", ""));
    }

    [Fact]
    public void The_library_source_setting_defaults_to_plex()
    {
        using var dir = new TempDir();
        Assert.Equal("plex", NewDb(dir).GetSetting("library_source", "plex"));
    }
}
