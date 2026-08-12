using Themearr.API.Data;

namespace Themearr.API.Tests;

public class RecheckStoreTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(dir.File("t.db"));
        db.Init();
        return db;
    }

    // Seeds one movie row with a theme already 'downloaded'. Needs a REAL folder, not the
    // brief's "/lib/{title}" placeholder: GetAllMovies (used here and by every caller below
    // to look up the id UpsertMovies derived) filters out any row whose folder doesn't exist
    // on disk (Database.ReadMediaRow), so a placeholder path would make every id lookup come
    // back empty and Single()/dictionary-key-not-found before the recheck logic is even
    // exercised.
    private static void SeedMovie(Database db, TempDir dir, string id, string title, int? year = 2021)
    {
        var folder = dir.File($"movie-{id}");
        Directory.CreateDirectory(folder);
        db.UpsertMovies(new[] { new MovieRecord(folder, "plex", id, title, year, folder) });
        db.SetMovieStatus(db.GetAllMovies().Single(m => m["title"]?.ToString() == title)["id"]!.ToString()!, "downloaded");
    }

    [Fact]
    public void GetLatestThemeProvenance_returnsTheMostRecentRow_notTheFirst()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.AddThemeHistory("m1", "Dune", 2021, "Dune Trailer", "https://youtube.com/watch?v=a", "movie", "auto", 45);
        db.AddThemeHistory("m1", "Dune", 2021, "Dune Main Theme", "https://youtube.com/watch?v=b", "movie", "manual", null);

        var p = db.GetLatestThemeProvenance("m1", "movie");

        Assert.NotNull(p);
        Assert.Equal("Dune Main Theme", p!.ThemeTitle);
        Assert.Equal("manual", p.PickedBy);
        Assert.Null(p.MatchScore);
    }

    [Fact]
    public void GetLatestThemeProvenance_doesNotCrossMediaTypes()
    {
        // Movie and show ids come from the same MediaFolderId hash space, so a show and
        // a movie can share an id — the same reason DownloadService namespaces job keys.
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.AddThemeHistory("x1", "Dune", 2021, "Movie Theme", "https://youtube.com/watch?v=a", "movie", "auto", 60);
        db.AddThemeHistory("x1", "Dune", 2021, "Show Theme",  "https://youtube.com/watch?v=b", "show",  "auto", 61);

        Assert.Equal("Movie Theme", db.GetLatestThemeProvenance("x1", "movie")!.ThemeTitle);
        Assert.Equal("Show Theme",  db.GetLatestThemeProvenance("x1", "show")!.ThemeTitle);
    }

    [Fact]
    public void GetLatestThemeProvenance_withNoHistory_returnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(NewDb(dir).GetLatestThemeProvenance("nope", "movie"));
    }

    [Fact]
    public void SetRecheck_thenClearRecheck_roundTrips()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, dir, "p1", "Dune");
        var id = db.GetAllMovies().Single()["id"]!.ToString()!;

        db.SetRecheck(id, "movie", "queued", "looks like a trailer, clip or promo");
        var queued = db.GetRecheckQueued("movie");
        Assert.Single(queued);
        Assert.Equal(id, queued[0]["id"]?.ToString());

        db.ClearRecheck(id, "movie");
        Assert.Empty(db.GetRecheckQueued("movie"));
    }

    [Fact]
    public void GetRecheckQueued_excludesReviewAndKept()
    {
        // 'review' and 'kept' are both waiting on a human; only 'queued' is work the
        // auto-download worker may pick up.
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, dir, "a", "Alpha");
        SeedMovie(db, dir, "b", "Bravo");
        var ids = db.GetAllMovies().ToDictionary(m => m["title"]!.ToString()!, m => m["id"]!.ToString()!);

        db.SetRecheck(ids["Alpha"], "movie", "review", "r");
        db.SetRecheck(ids["Bravo"], "movie", "kept",   "r");

        Assert.Empty(db.GetRecheckQueued("movie"));
    }

    [Fact]
    public void GetRecheckCandidates_excludesIgnoredAndPendingRows()
    {
        // The scan only judges themes that exist. A pending row has none, and an ignored
        // row is not the app's business.
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, dir, "a", "Downloaded");
        db.UpsertMovies(new[] { new MovieRecord("/lib/Pending", "plex", "b", "Pending", 2020, "/lib/Pending") });
        SeedMovie(db, dir, "c", "Ignored");
        var ids = db.GetAllMovies().ToDictionary(m => m["title"]!.ToString()!, m => m["id"]!.ToString()!);
        db.SetMovieIgnored(ids["Ignored"], true);

        var candidates = db.GetRecheckCandidates("movie");

        Assert.Single(candidates);
        Assert.Equal("Downloaded", candidates[0]["title"]?.ToString());
    }

    [Fact]
    public void GetFlaggedItems_returnsMoviesAndShowsTogether_eachCarryingItsMediaType()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, dir, "a", "Dune");
        var movieId = db.GetAllMovies().Single()["id"]!.ToString()!;
        db.SetRecheck(movieId, "movie", "review", "looks like a trailer, clip or promo");

        var flagged = db.GetFlaggedItems();

        Assert.Single(flagged);
        Assert.Equal("movie", flagged[0].MediaType);
        Assert.Equal("looks like a trailer, clip or promo", flagged[0].Reason);
    }

    [Fact]
    public void CountNeedsAttention_countsQueuedAndReview_butNotKept()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        SeedMovie(db, dir, "a", "Alpha"); SeedMovie(db, dir, "b", "Bravo"); SeedMovie(db, dir, "c", "Charlie");
        var ids = db.GetAllMovies().ToDictionary(m => m["title"]!.ToString()!, m => m["id"]!.ToString()!);
        db.SetRecheck(ids["Alpha"],   "movie", "queued", "r");
        db.SetRecheck(ids["Bravo"],   "movie", "review", "r");
        db.SetRecheck(ids["Charlie"], "movie", "kept",   "r");

        Assert.Equal(2, db.CountNeedsAttention("movie"));
    }

    [Fact]
    public void AddThemeHistory_withoutProvenance_storesNulls_soExistingCallersAreUnchanged()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.AddThemeHistory("m1", "Dune", 2021, "Dune Theme", "https://example.com/a.mp3");

        var p = db.GetLatestThemeProvenance("m1", "movie");
        Assert.Null(p!.PickedBy);
        Assert.Null(p.MatchScore);
    }
}
