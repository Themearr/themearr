using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// The scan itself: judging every already-downloaded theme against today's matching
/// rules with zero network calls. Task 5 of the re-check feature — see
/// docs/superpowers/... task-5-brief.md.
/// </summary>
public class ThemeRecheckServiceTests
{
    private const string YtUrl = "https://www.youtube.com/watch?v=abc12345678";

    private static Database NewDb(TempDir dir)
    {
        var db = new Database(dir.File("t.db"));
        db.Init();
        return db;
    }

    private static ThemeRecheckService NewService(Database db) =>
        new(db, NullLogger<ThemeRecheckService>.Instance);

    private static string SeedMovie(Database db, TempDir dir, string title, int? year = 2021)
    {
        var folder = dir.File($"movie-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var id = MediaFolderId.For(folder);
        db.UpsertMovies(new[] { new MovieRecord(folder, "plex", id, title, year, folder) });
        db.SetMovieStatus(id, "downloaded");
        return id;
    }

    private static string SeedShow(Database db, TempDir dir, string title, int? year = 2015)
    {
        var folder = dir.File($"show-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var id = MediaFolderId.For(folder);
        db.UpsertShows(new[] { new ShowRecord(folder, "plex", id, title, year, folder, false) });
        db.SetShowStatus(id, "downloaded");
        return id;
    }

    // A real, non-empty theme.mp3 — HasUsableThemeInExistingFolder stats the disk, so a
    // DB row that merely says 'downloaded' is not enough to make the scan consider it.
    private static void WriteTheme(string folder) =>
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), new byte[] { 1, 2, 3 });

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    // ── ResolveProvenance ───────────────────────────────────────────────────

    [Theory]
    [InlineData("manual")]
    [InlineData("auto")]
    public void ResolveProvenance_anExplicitPickedBy_alwaysWins(string pickedBy)
    {
        Assert.Equal(pickedBy, ThemeRecheckService.ResolveProvenance(pickedBy, "https://example.com/song.mp3"));
        Assert.Equal(pickedBy, ThemeRecheckService.ResolveProvenance(pickedBy, YtUrl));
        Assert.Equal(pickedBy, ThemeRecheckService.ResolveProvenance(pickedBy, null));
    }

    [Fact]
    public void ResolveProvenance_nullPickedBy_aProviderUrl_resolvesToAuto()
    {
        Assert.Equal("auto", ThemeRecheckService.ResolveProvenance(null, YtUrl));
    }

    [Fact]
    public void ResolveProvenance_nullPickedBy_aNonProviderUrl_resolvesToManual()
    {
        // Only POST /api/download-url (MoviesController.cs:187) can hand Start() a URL
        // that isn't a YouTube watch URL — every other call site builds one itself.
        Assert.Equal("manual", ThemeRecheckService.ResolveProvenance(null, "https://example.com/song.mp3"));
    }

    [Fact]
    public void ResolveProvenance_nullPickedBy_nullUrl_resolvesToAuto()
    {
        // Nothing about a missing URL says a human was involved.
        Assert.Equal("auto", ThemeRecheckService.ResolveProvenance(null, null));
    }

    // ── Scan behaviour ──────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_autoPickedTrailer_withThemeFileOnDisk_isQueued()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedMovie(db, dir, "Dune");
        WriteTheme(db.GetMovie(id)!["folderName"]!.ToString()!);
        db.AddThemeHistory(id, "Dune", 2021, "Dune Official Trailer #1", YtUrl, "movie", "auto", 45);

        var svc = NewService(db);
        Assert.True(await svc.StartAsync());
        await svc.Current;

        var flagged = db.GetFlaggedItems().Single(f => f.Id == id);
        Assert.Equal("queued", flagged.State);
        Assert.Equal("looks like a trailer, clip or promo", flagged.Reason);
    }

    [Fact]
    public async Task Scan_manualPickedTrailer_isReview_neverQueued()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedMovie(db, dir, "Dune");
        WriteTheme(db.GetMovie(id)!["folderName"]!.ToString()!);
        db.AddThemeHistory(id, "Dune", 2021, "Dune Official Trailer #1",
            "https://example.com/dune-trailer.mp3", "movie", "manual", null);

        var svc = NewService(db);
        Assert.True(await svc.StartAsync());
        await svc.Current;

        var flagged = db.GetFlaggedItems().Single(f => f.Id == id);
        Assert.Equal("review", flagged.State);
    }

    [Fact]
    public async Task Scan_aPassingPick_isNotFlagged()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedMovie(db, dir, "Dune");
        WriteTheme(db.GetMovie(id)!["folderName"]!.ToString()!);
        db.AddThemeHistory(id, "Dune", 2021, "Dune Original Score", YtUrl, "movie", "auto", 50);

        var svc = NewService(db);
        Assert.True(await svc.StartAsync());
        await svc.Current;

        Assert.Empty(db.GetFlaggedItems());
        Assert.Null(db.GetRecheckCandidates("movie").Single()["recheckState"]);
        var status = svc.GetStatus();
        Assert.Equal(0, (int)Prop(status, "flagged")!);
        Assert.Equal(1, (int)Prop(status, "scanned")!);
    }

    [Fact]
    public async Task Scan_noStoredThemeTitle_isUntouched()
    {
        // A direct-URL download never records a theme title (DownloadService.cs:156,
        // 189-207 — themeTitle is declared null and never reassigned on that path), so
        // there is no evidence to judge; flagging it would be noise.
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedMovie(db, dir, "Dune");
        WriteTheme(db.GetMovie(id)!["folderName"]!.ToString()!);
        db.AddThemeHistory(id, "Dune", 2021, null, "https://example.com/dune.mp3", "movie", "manual", null);

        var svc = NewService(db);
        Assert.True(await svc.StartAsync());
        await svc.Current;

        Assert.Empty(db.GetFlaggedItems());
        Assert.Null(db.GetRecheckCandidates("movie").Single()["recheckState"]);
    }

    [Fact]
    public async Task Scan_noThemeFileOnDisk_isUntouched()
    {
        // The row says 'downloaded' but the file was removed out-of-band; nothing to judge.
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedMovie(db, dir, "Dune"); // folder exists; WriteTheme() deliberately skipped
        db.AddThemeHistory(id, "Dune", 2021, "Dune Official Trailer", YtUrl, "movie", "auto", 45);

        var svc = NewService(db);
        Assert.True(await svc.StartAsync());
        await svc.Current;

        Assert.Empty(db.GetFlaggedItems());
    }

    [Fact]
    public async Task Scan_ignoredItem_isUntouched()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedMovie(db, dir, "Dune");
        WriteTheme(db.GetMovie(id)!["folderName"]!.ToString()!);
        db.AddThemeHistory(id, "Dune", 2021, "Dune Official Trailer", YtUrl, "movie", "auto", 45);
        db.SetMovieIgnored(id, true);

        var svc = NewService(db);
        Assert.True(await svc.StartAsync());
        await svc.Current;

        Assert.Empty(db.GetFlaggedItems());
    }

    [Fact]
    public async Task Scan_alreadyKept_isUntouched_evenThoughTheTitleStillFails()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedMovie(db, dir, "Dune");
        WriteTheme(db.GetMovie(id)!["folderName"]!.ToString()!);
        db.AddThemeHistory(id, "Dune", 2021, "Dune Official Trailer", YtUrl, "movie", "auto", 45);
        db.SetRecheck(id, "movie", "kept", null); // the user's prior, permanent answer

        var svc = NewService(db);
        Assert.True(await svc.StartAsync());
        await svc.Current;

        Assert.Empty(db.GetFlaggedItems());
        Assert.Equal("kept", db.GetRecheckCandidates("movie").Single()["recheckState"]);
    }

    [Fact]
    public async Task Scan_coversShowsToo()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedShow(db, dir, "Chernobyl");
        WriteTheme(db.GetShow(id)!["folderName"]!.ToString()!);
        db.AddThemeHistory(id, "Chernobyl", 2019, "Chernobyl Official Trailer", YtUrl, "show", "auto", 45);

        var svc = NewService(db);
        Assert.True(await svc.StartAsync());
        await svc.Current;

        var flagged = db.GetFlaggedItems().Single(f => f.Id == id);
        Assert.Equal("show", flagged.MediaType);
        Assert.Equal("queued", flagged.State);
    }

    // ── Concurrency guard ───────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_calledWhileAlreadyRunning_returnsFalse()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var svc = NewService(db);

        // Neither call is awaited before the other is issued. StartAsync only truly
        // suspends on the semaphore wait, which resolves synchronously when uncontended
        // (SemaphoreSlim's fast path), so the first call runs to `_inProgress = true` and
        // beyond, entirely on this thread, before this line returns control — the second
        // call is guaranteed to observe `_inProgress == true` regardless of how fast the
        // background scan itself finishes.
        var first  = svc.StartAsync();
        var second = svc.StartAsync();

        Assert.True(await first);
        Assert.False(await second);

        await svc.Current;
    }
}
