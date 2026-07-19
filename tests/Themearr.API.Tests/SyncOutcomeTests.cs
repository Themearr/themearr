using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class SyncOutcomeTests
{
    // ── Outcome wording ──────────────────────────────────────────────────────

    [Fact]
    public void A_successful_sync_reports_the_movie_count()
    {
        Assert.Equal("1451 movies synced", SyncOutcome.Describe(error: "", synced: 1451));
    }

    [Fact]
    public void One_movie_is_not_pluralised()
    {
        Assert.Equal("1 movie synced", SyncOutcome.Describe(error: "", synced: 1));
    }

    [Fact]
    public void A_failed_sync_says_so_without_leaking_the_exception()
    {
        // The real error text can carry a Plex server URL or token, and this string is
        // rendered on the System page. It must never reach the browser.
        var leaky = "Connection refused to http://plex.local:32400?X-Plex-Token=secret-token";

        var result = SyncOutcome.Describe(error: leaky, synced: 0);

        Assert.DoesNotContain("secret-token", result);
        Assert.DoesNotContain("plex.local", result);
        Assert.Contains("failed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failure_wins_over_a_partial_count()
    {
        var result = SyncOutcome.Describe(error: "boom", synced: 12);

        Assert.DoesNotContain("12", result);
        Assert.Contains("failed", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── SyncService completion handle ────────────────────────────────────────

    private static SyncService NewSyncService(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        // No Plex credentials configured, so the sync fails fast inside FetchMoviesAsync.
        return new SyncService(db, new PlexService(new HttpClient(), db, new LocalFolderResolver(db)), NullLogger<SyncService>.Instance);
    }

    [Fact]
    public async Task Current_completes_once_the_sync_has_finished()
    {
        using var dir = new TempDir();
        var sync = NewSyncService(dir);

        Assert.True(await sync.StartAsync());
        await sync.Current.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(sync.InProgress);
    }

    [Fact]
    public async Task Error_is_readable_after_a_failed_sync()
    {
        using var dir = new TempDir();
        var sync = NewSyncService(dir);

        await sync.StartAsync();
        await sync.Current.WaitAsync(TimeSpan.FromSeconds(10));

        // Unconfigured Plex must surface as an error, not a silent success.
        Assert.False(string.IsNullOrEmpty(sync.Error));
        Assert.Equal(0, sync.Synced);
    }

    [Fact]
    public async Task Current_is_already_complete_before_any_sync_has_run()
    {
        using var dir = new TempDir();
        var sync = NewSyncService(dir);

        // Awaiting before the first sync must not hang.
        await sync.Current.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
