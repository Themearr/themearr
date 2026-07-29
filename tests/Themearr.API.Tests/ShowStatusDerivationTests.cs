using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowStatusDerivationTests
{
    private static (Database db, string id, string folder) NewShow(
        TempDir dir, string title, bool plexHasTheme)
    {
        var folder = Path.Combine(dir.Path, title);
        Directory.CreateDirectory(folder);
        var db = new Database(Path.Combine(dir.Path, $"{title}.db"));
        db.Init();
        db.UpsertShows([new ShowRecord(folder, "plex", "srv1:1", title, 2010, folder, plexHasTheme)]);
        return (db, MediaFolderId.For(folder), folder);
    }

    [Fact]
    public void No_theme_anywhere_is_pending()
    {
        using var dir = new TempDir();
        var (db, id, _) = NewShow(dir, "Plain", plexHasTheme: false);
        Assert.Equal("pending", db.GetShow(id)!["status"]);
        Assert.Equal(false, db.GetShow(id)!["plexHasTheme"]);
    }

    [Fact]
    public void Plex_theme_without_a_local_file_is_plexTheme()
    {
        using var dir = new TempDir();
        var (db, id, _) = NewShow(dir, "PlexThemed", plexHasTheme: true);
        Assert.Equal("plexTheme", db.GetShow(id)!["status"]);
        Assert.Equal(true, db.GetShow(id)!["plexHasTheme"]);
    }

    /// <summary>
    /// Rule 2 before rule 3 — the load-bearing ordering. Without it, "Download anyway" on
    /// a Plex-themed show would write a real theme.mp3 and the row would still claim
    /// plexTheme, so the UI could never show the download succeeded.
    /// </summary>
    [Fact]
    public void A_local_theme_beats_a_plex_theme()
    {
        using var dir = new TempDir();
        var (db, id, folder) = NewShow(dir, "Both", plexHasTheme: true);
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);

        Assert.Equal("downloaded", db.GetShow(id)!["status"]);
        Assert.Equal(true, db.GetShow(id)!["plexHasTheme"]);   // flag still reported
    }

    [Fact]
    public void An_empty_local_theme_does_not_count_as_downloaded()
    {
        using var dir = new TempDir();
        var (db, id, folder) = NewShow(dir, "Truncated", plexHasTheme: false);
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), []);
        Assert.Equal("pending", db.GetShow(id)!["status"]);
    }

    [Fact]
    public void Ignored_beats_everything()
    {
        using var dir = new TempDir();
        var (db, id, folder) = NewShow(dir, "Ignored", plexHasTheme: true);
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [0x49, 0x44, 0x33, 9]);
        db.SetShowIgnored(id, true);

        Assert.Equal("ignored", db.GetShow(id)!["status"]);
    }

    [Fact]
    public void GetAllShows_uses_the_same_derivation()
    {
        using var dir = new TempDir();
        var (db, id, _) = NewShow(dir, "Listed", plexHasTheme: true);
        var row = db.GetAllShows().Single(s => (string)s["id"]! == id);
        Assert.Equal("plexTheme", row["status"]);
        Assert.Equal(true, row["plexHasTheme"]);
    }
}
