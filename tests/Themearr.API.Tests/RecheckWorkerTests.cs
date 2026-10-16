using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Task 7 of the re-check feature: teaching the auto-download workers to replace flagged
/// themes. YoutubeService has no interface, so — exactly like
/// <see cref="ShowAutoDownloadServiceTests"/> — nothing here can drive a live search;
/// these tests exercise the candidate-selection, outcome-rolling and no-match logic
/// directly through the internal `...ForTest` seams (see the InternalsVisibleTo at the
/// top of AutoDownloadService.cs), asserting on state transitions and candidate
/// selection rather than on search results.
/// </summary>
public class RecheckWorkerTests
{
    private sealed class ReadyProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static Database NewDb(TempDir dir)
    {
        var db = new Database(dir.File("t.db"));
        db.Init();
        return db;
    }

    private static AutoDownloadService BuildMovieWorker(Database db)
    {
        var provider = new ReadyProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<YoutubeService>();
        var sp = services.BuildServiceProvider();

        var download = new DownloadService(provider, db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);

        return new AutoDownloadService(sp, download, provider, NullLogger<AutoDownloadService>.Instance);
    }

    private static ShowAutoDownloadService BuildShowWorker(Database db)
    {
        var provider = new ReadyProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<YoutubeService>();
        var sp = services.BuildServiceProvider();

        var download = new DownloadService(provider, db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);

        return new ShowAutoDownloadService(sp, download, provider, NullLogger<ShowAutoDownloadService>.Instance);
    }

    // A real, non-empty theme.mp3 — GetMovie/GetShow's "status" is NOT the raw stored
    // column: Database.ReadMediaRow (Database.cs:1161-1170) recomputes it from
    // ThemeFiles.HasUsableThemeInExistingFolder every read, "ignored"/"downloaded"/
    // "pending" depending on disk state. A row that merely says 'downloaded' via
    // SetMovieStatus is not enough to make GetMovie agree — this is exactly the signal
    // RollLastOutcome's non-recheck branch reads, so a test standing in for a real
    // download must put a real file on disk, not just flip the DB column. Mirrors
    // ThemeRecheckServiceTests.WriteTheme (ThemeRecheckServiceTests.cs:48-49).
    private static void WriteTheme(string folder) =>
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), new byte[] { 1, 2, 3 });

    // Seeds one movie row already 'downloaded' — a re-check candidate has a theme, just a
    // bad one. Needs a real folder: GetAllMovies/GetRecheckQueued's callers filter on the
    // folder existing on disk (Database.ReadMediaRow), matching
    // ThemeRecheckServiceTests.SeedMovie (ThemeRecheckServiceTests.cs:26-34) and
    // RecheckStoreTests.SeedMovie (RecheckStoreTests.cs:20-26), which this mirrors — plus
    // WriteTheme so GetMovie's disk-derived status genuinely reads 'downloaded' too (see
    // WriteTheme's comment above).
    private static string SeedDownloadedMovie(Database db, TempDir dir, string title, int year = 2021)
    {
        var folder = dir.File($"movie-{title}");
        Directory.CreateDirectory(folder);
        WriteTheme(folder);
        var id = MediaFolderId.For(folder);
        db.UpsertMovies(new[] { new MovieRecord(folder, "plex", id, title, year, folder) });
        db.SetMovieStatus(id, "downloaded");
        return id;
    }

    // A movie with no theme at all — the ordinary pending case, seeded the same way
    // AutoDownloadService's own GetVerifiedPending expects: a real folder (or the
    // candidate is silently dropped before selection) and the default 'pending' status
    // UpsertMovies assigns (Database.cs:530).
    private static string AddPendingMovie(Database db, TempDir dir, string title, int year = 2021)
    {
        var folder = dir.File($"pending-movie-{title}");
        Directory.CreateDirectory(folder);
        var id = MediaFolderId.For(folder);
        db.UpsertMovies(new[] { new MovieRecord(folder, "plex", id, title, year, folder) });
        return id;
    }

    private static string SeedDownloadedShow(Database db, TempDir dir, string title, int year = 2015)
    {
        var folder = dir.File($"show-{title}");
        Directory.CreateDirectory(folder);
        WriteTheme(folder);
        var id = MediaFolderId.For(folder);
        db.UpsertShows(new[] { new ShowRecord(folder, "plex", id, title, year, folder, false) });
        db.SetShowStatus(id, "downloaded");
        return id;
    }

    private static string AddPendingShow(Database db, TempDir dir, string title, int year = 2015)
    {
        var folder = dir.File($"pending-show-{title}");
        Directory.CreateDirectory(folder);
        var id = MediaFolderId.For(folder);
        db.UpsertShows(new[] { new ShowRecord(folder, "plex", id, title, year, folder, false) });
        return id;
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    // ── THE HAZARD ──────────────────────────────────────────────────────────
    //
    // The outcome block decides whether the last-started item worked by testing
    // `status != "downloaded"` (before this task's fix — AutoDownloadService.cs:151-155
    // in the pre-fix version this test is written against). A re-check item is ALREADY
    // 'downloaded' — it has a theme, just a bad one — so a failed replacement reads as
    // success: no cooldown, the item stays 'queued', and the worker retries it every 30s
    // forever. Judging by `recheck_state` instead of `status` is what this task adds.

    [Fact]
    public void FailedReplacement_isCooledDown_notRetriedEveryTick()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedMovie(db, dir, "Dune");
        db.SetRecheck(id, "movie", "queued", "looks like a trailer, clip or promo");

        var worker = BuildMovieWorker(db);
        worker.NoteStartedForTest(id, wasRecheck: true);   // stand-in for _lastStartedMovieId
        worker.RollLastOutcomeForTest();                   // the outcome-rolling block

        // The download never landed, so the flag is untouched — and THAT, not `status`,
        // is what must drive the cooldown.
        Assert.Single(db.GetRecheckQueued("movie"));
        Assert.True(worker.IsCoolingDownForTest(id),
            "a failed replacement must cool down; judging it by `status` reports success "
            + "for an item that was already 'downloaded' and retries it every tick");
    }

    [Fact]
    public void FailedReplacement_isCooledDown_notRetriedEveryTick_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedShow(db, dir, "Chernobyl");
        db.SetRecheck(id, "show", "queued", "looks like a trailer, clip or promo");

        var worker = BuildShowWorker(db);
        worker.NoteStartedForTest(id, wasRecheck: true);
        worker.RollLastOutcomeForTest();

        Assert.Single(db.GetRecheckQueued("show"));
        Assert.True(worker.IsCoolingDownForTest(id),
            "a failed replacement must cool down; judging it by `status` reports success "
            + "for an item that was already 'downloaded' and retries it every tick");
    }

    // A successful replacement is the mirror image: DownloadService.ClearRecheck runs
    // before the next tick, so GetRecheckQueued no longer returns the row and no cooldown
    // should be applied — otherwise a title that just got fixed would be locked out of
    // being picked again for an hour for no reason.
    [Fact]
    public void SuccessfulReplacement_isNotCooledDown()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedMovie(db, dir, "Dune");
        db.SetRecheck(id, "movie", "queued", "looks like a trailer, clip or promo");

        var worker = BuildMovieWorker(db);
        worker.NoteStartedForTest(id, wasRecheck: true);
        db.ClearRecheck(id, "movie");   // stand-in for DownloadService's success path
        worker.RollLastOutcomeForTest();

        Assert.False(worker.IsCoolingDownForTest(id));
    }

    // ── Ordering: pending before queued ────────────────────────────────────

    [Fact]
    public void SelectCandidate_prefersPending_overQueuedRecheck()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        AddPendingMovie(db, dir, "Alpha");
        var flaggedId = SeedDownloadedMovie(db, dir, "Bravo");
        db.SetRecheck(flaggedId, "movie", "queued", "r");

        var (candidate, isRecheck) = BuildMovieWorker(db).SelectCandidateForTest();

        Assert.NotNull(candidate);
        Assert.Equal("Alpha", candidate!["title"]?.ToString());
        Assert.False(isRecheck);
    }

    [Fact]
    public void SelectCandidate_prefersPending_overQueuedRecheck_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        AddPendingShow(db, dir, "Severance");
        var flaggedId = SeedDownloadedShow(db, dir, "Chernobyl");
        db.SetRecheck(flaggedId, "show", "queued", "r");

        var (candidate, isRecheck) = BuildShowWorker(db).SelectCandidateForTest();

        Assert.NotNull(candidate);
        Assert.Equal("Severance", candidate!["title"]?.ToString());
        Assert.False(isRecheck);
    }

    [Fact]
    public void SelectCandidate_picksQueuedRecheck_whenPendingIsDry()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var flaggedId = SeedDownloadedMovie(db, dir, "Dune");
        db.SetRecheck(flaggedId, "movie", "queued", "r");

        var (candidate, isRecheck) = BuildMovieWorker(db).SelectCandidateForTest();

        Assert.NotNull(candidate);
        Assert.Equal(flaggedId, candidate!["id"]?.ToString());
        Assert.True(isRecheck);
    }

    [Fact]
    public void SelectCandidate_picksQueuedRecheck_whenPendingIsDry_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var flaggedId = SeedDownloadedShow(db, dir, "Chernobyl");
        db.SetRecheck(flaggedId, "show", "queued", "r");

        var (candidate, isRecheck) = BuildShowWorker(db).SelectCandidateForTest();

        Assert.NotNull(candidate);
        Assert.Equal(flaggedId, candidate!["id"]?.ToString());
        Assert.True(isRecheck);
    }

    // The pending queue isn't literally empty here — every pending movie is in cooldown —
    // but there is nothing else productive the tick can do with it, so the worker still
    // falls through to a ready re-check item rather than idling. See SelectCandidate's
    // doc comment (AutoDownloadService.cs) for why "dry" is read this way.
    [Fact]
    public void SelectCandidate_fallsThroughToRecheck_whenAllPendingAreCoolingDown()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var pendingId = AddPendingMovie(db, dir, "Alpha");
        var flaggedId = SeedDownloadedMovie(db, dir, "Bravo");
        db.SetRecheck(flaggedId, "movie", "queued", "r");

        var worker = BuildMovieWorker(db);
        worker.NoteStartedForTest(pendingId, wasRecheck: false);
        worker.RollLastOutcomeForTest(); // GetMovie's status is still 'pending' -> cools down

        Assert.True(worker.IsCoolingDownForTest(pendingId));

        var (candidate, isRecheck) = worker.SelectCandidateForTest();

        Assert.NotNull(candidate);
        Assert.Equal(flaggedId, candidate!["id"]?.ToString());
        Assert.True(isRecheck);
    }

    // ── No confident match on a re-check item: review, not cooldown-and-retry ─

    [Fact]
    public void HandleNoMatch_onARecheckItem_movesToReview_withoutCooldown()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedMovie(db, dir, "Dune");
        db.SetRecheck(id, "movie", "queued", "looks like a trailer, clip or promo");

        var worker = BuildMovieWorker(db);
        worker.HandleNoMatchForTest(id, "Dune", isRecheck: true);

        var flagged = db.GetFlaggedItems().Single(f => f.Id == id);
        Assert.Equal("review", flagged.State);
        Assert.Equal("no better match found", flagged.Reason);
        // The defining contrast with the old pending path: no NoMatchCooldown. A repeat
        // search for the same query will not turn up anything new, so retrying would only
        // burn RapidAPI quota chasing an answer that cannot change.
        Assert.False(worker.IsCoolingDownForTest(id));
    }

    [Fact]
    public void HandleNoMatch_onARecheckItem_movesToReview_withoutCooldown_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedShow(db, dir, "Chernobyl");
        db.SetRecheck(id, "show", "queued", "looks like a trailer, clip or promo");

        var worker = BuildShowWorker(db);
        worker.HandleNoMatchForTest(id, "Chernobyl", isRecheck: true);

        var flagged = db.GetFlaggedItems().Single(f => f.Id == id);
        Assert.Equal("review", flagged.State);
        Assert.Equal("no better match found", flagged.Reason);
        Assert.False(worker.IsCoolingDownForTest(id));
    }

    // A pending (non-recheck) item's no-match handling is unchanged: it still cools down
    // and retries, because unlike a re-check there really is a gap to fill.
    [Fact]
    public void HandleNoMatch_onAPendingItem_stillCoolsDown_unlikeARecheckItem()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = AddPendingMovie(db, dir, "Alpha");

        var worker = BuildMovieWorker(db);
        worker.HandleNoMatchForTest(id, "Alpha", isRecheck: false);

        Assert.True(worker.IsCoolingDownForTest(id));
        Assert.Empty(db.GetFlaggedItems()); // never touched recheck_state
    }

    // The compare-and-set guard (ruling 2): between the worker reading the row as 'queued'
    // and the search returning, the user can click "Keep this". The worker's "no better
    // match" write must not clobber that — see TrySetRecheck's doc comment
    // (Database.cs:1071-1082) for the race this closes.
    [Fact]
    public void HandleNoMatch_neverOverwrites_aRowTheUserAlreadyKept()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedMovie(db, dir, "Dune");
        db.SetRecheck(id, "movie", "queued", "looks like a trailer, clip or promo");

        // The user's decision lands first, while the worker still believes it is 'queued'.
        db.SetRecheck(id, "movie", "kept", null);

        var worker = BuildMovieWorker(db);
        worker.HandleNoMatchForTest(id, "Dune", isRecheck: true);

        var flagged = db.GetRecheckCandidates("movie").Single(m => m["id"]?.ToString() == id);
        Assert.Equal("kept", flagged["recheckState"]);
    }

    // ── 'review' items are never picked up ─────────────────────────────────

    [Fact]
    public void SelectCandidate_neverPicksAReviewItem()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedMovie(db, dir, "Dune");
        db.SetRecheck(id, "movie", "review", "no better match found");
        // no pending movies either — the review item is the only row in the library

        var (candidate, _) = BuildMovieWorker(db).SelectCandidateForTest();

        Assert.Null(candidate);
    }

    [Fact]
    public void SelectCandidate_neverPicksAReviewItem_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var id = SeedDownloadedShow(db, dir, "Chernobyl");
        db.SetRecheck(id, "show", "review", "no better match found");

        var (candidate, _) = BuildShowWorker(db).SelectCandidateForTest();

        Assert.Null(candidate);
    }

    // ── GetDiagnostics surfaces the new candidate set ──────────────────────

    [Fact]
    public void GetDiagnostics_reportsRecheckQueuedCount_movie()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var a = SeedDownloadedMovie(db, dir, "Alpha");
        var b = SeedDownloadedMovie(db, dir, "Bravo");
        db.SetRecheck(a, "movie", "queued", "r");
        db.SetRecheck(b, "movie", "review", "r"); // not counted — only 'queued' is worker candidate set

        var diagnostics = BuildMovieWorker(db).GetDiagnostics();

        Assert.Equal(1, (int)Prop(diagnostics, "recheckQueuedCount")!);
    }

    [Fact]
    public void GetDiagnostics_reportsRecheckQueuedCount_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var a = SeedDownloadedShow(db, dir, "Alpha");
        var b = SeedDownloadedShow(db, dir, "Bravo");
        db.SetRecheck(a, "show", "queued", "r");
        db.SetRecheck(b, "show", "review", "r");

        var diagnostics = BuildShowWorker(db).GetDiagnostics();

        Assert.Equal(1, (int)Prop(diagnostics, "recheckQueuedCount")!);
    }
}
