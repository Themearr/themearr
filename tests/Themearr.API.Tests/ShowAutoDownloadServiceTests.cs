using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// Pins the worker's guard conditions and its query shape. The happy path (searched →
/// best match → started) can't be faked here because YoutubeService has no interface,
/// so it stops at the guards — same level at which the movie AutoDownloadService is
/// tested. The end-to-end path is covered by the live-Plex manual check.
/// </summary>
public class ShowAutoDownloadServiceTests
{
    private sealed class ReadyProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    /// <summary>Provider that reports itself unconfigured — models a missing RapidAPI key.</summary>
    private sealed class UnconfiguredProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => "RapidAPI key is not configured";
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static ShowAutoDownloadService Build(Database db, IThemeAudioProvider? provider = null)
    {
        provider ??= new ReadyProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<YoutubeService>();
        var sp = services.BuildServiceProvider();

        var download = new DownloadService(provider, db, new StubHttpClientFactory(), config,
            NullLogger<DownloadService>.Instance);

        return new ShowAutoDownloadService(sp, download, provider, NullLogger<ShowAutoDownloadService>.Instance);
    }

    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    private static string AddPendingShow(Database db, TempDir dir, string title, int year, string sourceRef)
    {
        var folder = Path.Combine(dir.Path, title);
        Directory.CreateDirectory(folder);
        db.UpsertShows([new ShowRecord(folder, "plex", sourceRef, title, year, folder, false)]);
        return folder;
    }

    [Fact]
    public async Task TryDownloadOnce_skips_when_auto_download_off()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        // auto_download defaults to false; a show is pending but must not be touched.
        AddPendingShow(db, dir, "Show", 2010, "srv1:1");

        var result = await Build(db).TryDownloadOnceAsync(CancellationToken.None);

        Assert.Contains("auto_download is off", result);
        Assert.Single(db.GetPendingShows());   // still pending — nothing was started
    }

    [Fact]
    public async Task TryDownloadOnce_skips_when_setup_is_not_complete()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("auto_download", "true");
        // MarkSetupComplete deliberately not called.
        AddPendingShow(db, dir, "Show", 2010, "srv1:1");

        var result = await Build(db).TryDownloadOnceAsync(CancellationToken.None);

        Assert.Contains("setup not complete", result);
    }

    [Fact]
    public async Task TryDownloadOnce_skips_when_the_provider_is_not_configured()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("auto_download", "true");
        db.MarkSetupComplete();
        AddPendingShow(db, dir, "Show", 2010, "srv1:1");

        var result = await Build(db, new UnconfiguredProvider()).TryDownloadOnceAsync(CancellationToken.None);

        Assert.Contains("RapidAPI key is not configured", result);
    }

    [Fact]
    public async Task TryDownloadOnce_reports_no_pending_shows_when_the_queue_is_empty()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("auto_download", "true");
        db.MarkSetupComplete();
        // no shows at all

        var result = await Build(db).TryDownloadOnceAsync(CancellationToken.None);

        Assert.Contains("no pending shows", result);
    }

    /// <summary>
    /// A show whose theme appeared out-of-band should be reconciled to 'downloaded'
    /// rather than searched for again — the same self-healing the movie worker does.
    /// </summary>
    [Fact]
    public async Task TryDownloadOnce_reconciles_a_show_that_already_has_a_theme_on_disk()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetSetting("auto_download", "true");
        db.MarkSetupComplete();
        var folder = AddPendingShow(db, dir, "Themed Show", 2010, "srv1:1");
        File.WriteAllBytes(Path.Combine(folder, "theme.mp3"), new byte[] { 0x49, 0x44, 0x33, 9 });

        var result = await Build(db).TryDownloadOnceAsync(CancellationToken.None);

        Assert.Contains("no pending shows", result);
        Assert.Empty(db.GetPendingShows());   // stored status reconciled to downloaded
    }

    /// <summary>
    /// A show isn't per-year, so the search query must NOT carry one — "The Wire 2002
    /// theme" biases YouTube towards a single season/episode upload.
    /// </summary>
    [Fact]
    public void Search_query_is_year_free()
    {
        Assert.Equal("The Wire theme song", ShowAutoDownloadService.BuildQuery("The Wire"));
        Assert.Equal("Severance theme song", ShowAutoDownloadService.BuildQuery("  Severance  "));
    }
}
