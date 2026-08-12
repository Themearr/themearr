using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// Task 8 of the re-check feature — the API surface: starting a scan, its status, the
/// flagged list, and the two per-item user actions. See
/// docs/superpowers/sdd/2026-08-12-theme-recheck/task-8-brief.md.
/// </summary>
public class RecheckEndpointTests
{
    private sealed class NullProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => "not configured (unused in these tests)";
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static Database NewDb(TempDir dir, out string path)
    {
        path = Path.Combine(dir.Path, "test.db");
        var db = new Database(path);
        db.Init();
        return db;
    }

    private static DownloadService NewDownload(Database db)
    {
        var config = new ConfigurationBuilder().Build();
        return new DownloadService(new NullProvider(), db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);
    }

    private static RecheckController NewRecheckController(Database db, ThemeRecheckService? svc = null) =>
        new(svc ?? new ThemeRecheckService(db, NullLogger<ThemeRecheckService>.Instance), db);

    private static MoviesController NewMoviesController(Database db) =>
        new(db, new YoutubeService(), NewDownload(db), new PosterUrlSigner(new byte[32]),
            new LibrarySourceResolver(db, Array.Empty<ILibrarySource>()), NullLogger<MoviesController>.Instance);

    private static ShowsController NewShowsController(Database db) =>
        new(db, new YoutubeService(), NewDownload(db), new PosterUrlSigner([1, 2, 3]),
            NullLogger<ShowsController>.Instance)
        {
            // DeleteTheme/GetThemeAudio touch Response; a directly-constructed controller
            // has no HttpContext unless one is supplied (mirrors ShowsControllerTests.New).
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static string AddMovie(Database db, TempDir dir, string title)
    {
        var folder = Path.Combine(dir.Path, "movie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        db.UpsertMovies([new MovieRecord(folder, "plex", "ref", title, 2020, folder)]);
        return MediaFolderId.For(folder);
    }

    private static string AddShow(Database db, TempDir dir, string title)
    {
        var folder = Path.Combine(dir.Path, "show-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", "ref", title, 2020, folder, false)]);
        return MediaFolderId.For(folder);
    }

    private static void WriteTheme(string folder) =>
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), [1, 2, 3]);

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    // Reads the column directly rather than through Database, because every reader
    // Database exposes filters by state or by status='downloaded' (GetFlaggedItems,
    // GetRecheckQueued, GetRecheckCandidates) — none can tell "still kept" apart from
    // "cleared to NULL" once a deleted theme has already reset status away from
    // 'downloaded'. Same technique RecheckSchemaTests.cs already uses for this table.
    private static string? RawRecheckState(string dbPath, string table, string id)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT recheck_state FROM {table} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() && !r.IsDBNull(0) ? r.GetString(0) : null;
    }

    // ── POST /api/themes/recheck ────────────────────────────────────────────

    [Fact]
    public async Task Start_launches_a_scan()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out _);

        var result = Assert.IsType<OkObjectResult>(await NewRecheckController(db).Start());

        Assert.True((bool)Prop(result.Value!, "started")!);
        Assert.Null(Prop(result.Value!, "detail"));
    }

    [Fact]
    public async Task Start_reports_already_in_progress_on_the_second_call()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out _);
        var svc = new ThemeRecheckService(db, NullLogger<ThemeRecheckService>.Instance);
        var controller = NewRecheckController(db, svc);

        // Neither call is awaited before the other is issued — same technique as
        // ThemeRecheckServiceTests.StartAsync_calledWhileAlreadyRunning_returnsFalse:
        // StartAsync's semaphore fast-path runs synchronously up to `_inProgress = true`
        // before this line returns control, so the second call is guaranteed to observe
        // it regardless of how fast the (zero-network) scan itself finishes.
        var first  = controller.Start();
        var second = controller.Start();

        var firstResult = Assert.IsType<OkObjectResult>(await first);
        Assert.True((bool)Prop(firstResult.Value!, "started")!);

        var secondResult = Assert.IsType<OkObjectResult>(await second);
        Assert.False((bool)Prop(secondResult.Value!, "started")!);
        Assert.Equal("Re-check already in progress", Prop(secondResult.Value!, "detail"));

        await svc.Current;
    }

    // ── GET /api/themes/recheck/status ──────────────────────────────────────

    [Fact]
    public async Task Status_reflects_the_service_once_a_scan_finishes()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out _);
        var svc = new ThemeRecheckService(db, NullLogger<ThemeRecheckService>.Instance);
        var controller = NewRecheckController(db, svc);

        Assert.True(await svc.StartAsync());
        await svc.Current;

        var result = Assert.IsType<OkObjectResult>(controller.Status());
        Assert.True((bool)Prop(result.Value!, "finished")!);
        Assert.False((bool)Prop(result.Value!, "inProgress")!);
    }

    // ── GET /api/themes/recheck/flagged ─────────────────────────────────────

    [Fact]
    public void Flagged_returns_movies_and_shows_together_each_carrying_mediaType()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out _);
        var movieId = AddMovie(db, dir, "Movie");
        db.SetRecheck(movieId, "movie", "queued", "looks like a trailer, clip or promo");
        var showId = AddShow(db, dir, "Show");
        db.SetRecheck(showId, "show", "review", "looks like a trailer, clip or promo");

        var result = Assert.IsType<OkObjectResult>(NewRecheckController(db).Flagged());
        var items = Assert.IsType<List<RecheckItem>>(result.Value);

        Assert.Contains(items, i => i.Id == movieId && i.MediaType == "movie" && i.State == "queued");
        Assert.Contains(items, i => i.Id == showId && i.MediaType == "show" && i.State == "review");
    }

    // ── POST /api/movies/{id}/recheck/keep, .../replace ─────────────────────

    [Fact]
    public void Movie_recheck_keep_moves_state_to_kept()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out var path);
        var id = AddMovie(db, dir, "Movie");
        db.SetRecheck(id, "movie", "review", "manual pick failed the rules");

        var result = Assert.IsType<OkObjectResult>(NewMoviesController(db).RecheckKeep(id));

        Assert.Equal("kept", Prop(result.Value!, "state"));
        Assert.Equal("kept", RawRecheckState(path, "movies", id));
    }

    [Fact]
    public void Movie_recheck_keep_404s_for_an_unknown_id() =>
        Assert.IsType<NotFoundObjectResult>(NewMoviesController(NewDb(new TempDir(), out _)).RecheckKeep("nope"));

    [Fact]
    public void Movie_recheck_replace_moves_state_to_queued()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out var path);
        var id = AddMovie(db, dir, "Movie");
        db.SetRecheck(id, "movie", "review", "manual pick failed the rules");

        var result = Assert.IsType<OkObjectResult>(NewMoviesController(db).RecheckReplace(id));

        Assert.Equal("queued", Prop(result.Value!, "state"));
        Assert.Equal("queued", RawRecheckState(path, "movies", id));
    }

    [Fact]
    public void Movie_recheck_replace_404s_for_an_unknown_id() =>
        Assert.IsType<NotFoundObjectResult>(NewMoviesController(NewDb(new TempDir(), out _)).RecheckReplace("nope"));

    // ── POST /api/shows/{id}/recheck/keep, .../replace ──────────────────────

    [Fact]
    public void Show_recheck_keep_moves_state_to_kept()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out var path);
        var id = AddShow(db, dir, "Show");
        db.SetRecheck(id, "show", "review", "manual pick failed the rules");

        var result = Assert.IsType<OkObjectResult>(NewShowsController(db).RecheckKeep(id));

        Assert.Equal("kept", Prop(result.Value!, "state"));
        Assert.Equal("kept", RawRecheckState(path, "shows", id));
    }

    [Fact]
    public void Show_recheck_keep_404s_for_an_unknown_id() =>
        Assert.IsType<NotFoundObjectResult>(NewShowsController(NewDb(new TempDir(), out _)).RecheckKeep("nope"));

    [Fact]
    public void Show_recheck_replace_moves_state_to_queued()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out var path);
        var id = AddShow(db, dir, "Show");
        db.SetRecheck(id, "show", "review", "manual pick failed the rules");

        var result = Assert.IsType<OkObjectResult>(NewShowsController(db).RecheckReplace(id));

        Assert.Equal("queued", Prop(result.Value!, "state"));
        Assert.Equal("queued", RawRecheckState(path, "shows", id));
    }

    [Fact]
    public void Show_recheck_replace_404s_for_an_unknown_id() =>
        Assert.IsType<NotFoundObjectResult>(NewShowsController(NewDb(new TempDir(), out _)).RecheckReplace("nope"));

    // ── POST /api/shows/auto-download/{showId} ──────────────────────────────
    // The happy path (search → best match → download.Start(..., "auto", ...)) can't be
    // exercised here: YoutubeService wraps YoutubeExplode directly with no interface to
    // fake, so — like AutoDownloadService and ShowAutoDownloadService before it (see
    // ShowAutoDownloadServiceTests.cs:9-13) — this stops at the guard reachable without a
    // live network call. Read against the running app, the endpoint really does call
    // download.Start(showId, url, "show", "auto", best["score"] as int?), mirroring
    // MoviesController.AutoDownload (MoviesController.cs:161-163).

    [Fact]
    public async Task Show_auto_download_404s_for_an_unknown_show()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out _);

        var result = await NewShowsController(db).AutoDownload("nope");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // ── DeleteTheme clears the re-check flag (movies and shows) ─────────────

    [Fact]
    public void Movie_DeleteTheme_clears_the_recheck_flag_even_from_kept()
    {
        // 'kept' is the escape hatch's starting point: the design says it is permanent,
        // and deleting the theme is the only documented way back. If DeleteTheme left the
        // flag behind, a user who clicked "Keep this" could never undo it.
        using var dir = new TempDir();
        var db = NewDb(dir, out var path);
        var folder = Path.Combine(dir.Path, "Movie");
        Directory.CreateDirectory(folder);
        WriteTheme(folder);
        db.UpsertMovies([new MovieRecord(folder, "plex", "ref", "Movie", 2020, folder)]);
        var id = MediaFolderId.For(folder);
        db.SetMovieStatus(id, "downloaded");
        db.SetRecheck(id, "movie", "kept", null);

        var result = Assert.IsType<OkObjectResult>(NewMoviesController(db).DeleteTheme(id));

        Assert.True((bool)Prop(result.Value!, "deleted")!);
        Assert.Null(RawRecheckState(path, "movies", id));
    }

    [Fact]
    public void Movie_DeleteTheme_clears_the_recheck_flag_from_queued()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out var path);
        var folder = Path.Combine(dir.Path, "Movie");
        Directory.CreateDirectory(folder);
        WriteTheme(folder);
        db.UpsertMovies([new MovieRecord(folder, "plex", "ref", "Movie", 2020, folder)]);
        var id = MediaFolderId.For(folder);
        db.SetMovieStatus(id, "downloaded");
        db.SetRecheck(id, "movie", "queued", "looks like a trailer, clip or promo");

        NewMoviesController(db).DeleteTheme(id);

        Assert.Null(RawRecheckState(path, "movies", id));
    }

    [Fact]
    public void Show_DeleteTheme_clears_the_recheck_flag_even_from_kept()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out var path);
        var folder = Path.Combine(dir.Path, "Show");
        Directory.CreateDirectory(folder);
        WriteTheme(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", "ref", "Show", 2020, folder, false)]);
        var id = MediaFolderId.For(folder);
        db.SetShowStatus(id, "downloaded");
        db.SetRecheck(id, "show", "kept", null);

        var result = Assert.IsType<OkObjectResult>(NewShowsController(db).DeleteTheme(id));

        Assert.True((bool)Prop(result.Value!, "deleted")!);
        Assert.Null(RawRecheckState(path, "shows", id));
    }

    // ── StatsController: needsAttention ──────────────────────────────────────

    // Minimal fake so GetStats' `sources.Active.Name` resolves — library_source defaults
    // to "plex" (LibrarySourceResolver.cs:22), and GetStats only ever reads .Name.
    private sealed class FakePlexSource : ILibrarySource
    {
        public string Name => "plex";
        public TimeSpan SyncInterval => TimeSpan.FromHours(1);
        public Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<string?> CheckAsync(CancellationToken ct) => throw new NotImplementedException();
        public string? SyncBlockedReason => null;
    }

    [Fact]
    public void GetStats_carries_needsAttention()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out _);
        var id = AddMovie(db, dir, "Movie");
        db.SetRecheck(id, "movie", "review", "looks like a trailer, clip or promo");
        var sources = new LibrarySourceResolver(db, [new FakePlexSource()]);
        var controller = new StatsController(db, new PosterUrlSigner(new byte[32]), sources);

        var result = Assert.IsType<OkObjectResult>(controller.GetStats());
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(1, body.GetProperty("needsAttention").GetInt32());
    }

    [Fact]
    public void GetShowStats_carries_needsAttention()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, out _);
        var id = AddShow(db, dir, "Show");
        db.SetRecheck(id, "show", "queued", "looks like a trailer, clip or promo");

        // `sources` is null! deliberately, same as ShowStatsTests.Endpoint_returns_the_counts:
        // GetShowStats returns no poster URLs, so it must never touch the source resolver.
        var controller = new StatsController(db, new PosterUrlSigner(new byte[32]), null!);

        var result = Assert.IsType<OkObjectResult>(controller.GetShowStats());
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.Equal(1, body.GetProperty("needsAttention").GetInt32());
    }

    // ── auth: every new route sits under /api, outside /api/auth and /api/poster ────
    // ApiAuthMiddleware.RequiresAuth (ApiAuthMiddleware.cs:31-34) is a pure prefix check
    // guarding all of /api except those two, so these routes are covered automatically —
    // this pins that they are, without touching RequiresAuth itself. Mirrors
    // AuthBoundaryTests.cs's pattern, which also gets the new prefixes added below.

    [Theory]
    [InlineData("/api/themes/recheck")]
    [InlineData("/api/themes/recheck/status")]
    [InlineData("/api/themes/recheck/flagged")]
    [InlineData("/api/movies/abc123/recheck/keep")]
    [InlineData("/api/movies/abc123/recheck/replace")]
    [InlineData("/api/shows/abc123/recheck/keep")]
    [InlineData("/api/shows/abc123/recheck/replace")]
    [InlineData("/api/shows/auto-download/abc123")]
    public void New_routes_require_auth(string path) =>
        Assert.True(ApiAuthMiddleware.RequiresAuth(new PathString(path)));
}
