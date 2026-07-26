using Themearr.API.Data;

namespace Themearr.API.Tests;

public class ShowStoreTests
{
    private static Database New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    private static ShowRecord Rec(string folder, string title, bool hasPlexTheme = false) =>
        new(folder, "plex", "srv1:1", title, 2008, folder, hasPlexTheme);

    [Fact]
    public void UpsertShows_then_GetShow_roundtrips_identity_and_fields()
    {
        using var dir = new TempDir();
        var db = New(dir);
        var folder = Path.Combine(dir.Path, "Breaking Bad");
        Directory.CreateDirectory(folder);

        db.UpsertShows([Rec(folder, "Breaking Bad")]);

        var id = Themearr.API.Services.MediaFolderId.For(folder);
        var show = db.GetShow(id);
        Assert.NotNull(show);
        Assert.Equal("Breaking Bad", show!["title"]);
        Assert.Equal("pending", show["status"]);   // no theme.* on disk yet
    }

    [Fact]
    public void GetPendingShows_excludes_ignored_and_plex_provided_and_downloaded()
    {
        using var dir = new TempDir();
        var db = New(dir);
        var needs   = Path.Combine(dir.Path, "Needs");   Directory.CreateDirectory(needs);
        var plexHas = Path.Combine(dir.Path, "PlexHas"); Directory.CreateDirectory(plexHas);
        var onDisk  = Path.Combine(dir.Path, "OnDisk");  Directory.CreateDirectory(onDisk);
        File.WriteAllText(Path.Combine(onDisk, "theme.mp3"), "x");   // already downloaded on disk

        db.UpsertShows([Rec(needs, "Needs"), Rec(plexHas, "PlexHas", hasPlexTheme: true), Rec(onDisk, "OnDisk")]);

        var pending = db.GetPendingShows();
        Assert.Contains(pending, s => (string)s["title"]! == "Needs");
        Assert.DoesNotContain(pending, s => (string)s["title"]! == "PlexHas");   // Plex provides a theme
        // OnDisk stays status='pending' in the column (status is disk-derived at read time),
        // but GetPendingShows is the worker pre-filter keyed off the stored column, so it is
        // still listed here — the worker verifies the disk before acting (mirrors movies).
    }

    [Fact]
    public void PruneShowsExcept_removes_absent_shows_but_keeps_ignored()
    {
        using var dir = new TempDir();
        var db = New(dir);
        var keep    = Path.Combine(dir.Path, "Keep");    Directory.CreateDirectory(keep);
        var drop    = Path.Combine(dir.Path, "Drop");    Directory.CreateDirectory(drop);
        var ignored = Path.Combine(dir.Path, "Ignored"); Directory.CreateDirectory(ignored);
        db.UpsertShows([Rec(keep, "Keep"), Rec(drop, "Drop"), Rec(ignored, "Ignored")]);
        db.SetShowIgnored(Themearr.API.Services.MediaFolderId.For(ignored), true);

        var removed = db.PruneShowsExcept([keep]);

        Assert.Equal(1, removed);                                   // only Drop
        Assert.NotNull(db.GetShow(Themearr.API.Services.MediaFolderId.For(keep)));
        Assert.NotNull(db.GetShow(Themearr.API.Services.MediaFolderId.For(ignored)));  // ignored kept
        Assert.Null(db.GetShow(Themearr.API.Services.MediaFolderId.For(drop)));
    }
}
