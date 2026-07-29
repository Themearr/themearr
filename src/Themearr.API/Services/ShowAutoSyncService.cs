using System.Diagnostics;
using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Services;

/// <summary>
/// Background service that triggers a TV show sync once per day when auto-sync is
/// enabled — the show-side parallel of <see cref="AutoSyncService"/>. Also serves the
/// System → Tasks "Sync Shows" row: it reports each run into the
/// <see cref="TaskRegistry"/> and wakes early when the user clicks "Run now".
/// </summary>
public class ShowAutoSyncService(
    IServiceProvider services, TaskRegistry registry, PlexLibrarySource plexSource,
    ILogger<ShowAutoSyncService> log)
    : BackgroundService
{
    public const string SyncTaskId = "syncShows";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JitterMax     = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Taken from the Plex source specifically, not the active one: shows only ever come
    /// from Plex (ShowSyncService talks to PlexService directly), so an operator whose
    /// MOVIE source is Radarr must not end up driving Plex show scans at Radarr's
    /// 15-minute cadence.
    /// </summary>
    private TimeSpan SyncInterval => plexSource.SyncInterval;

    /// <summary>Its own clock — sharing the movie sync's key would let either sync
    /// suppress the other for a full interval.</summary>
    private const string LastRunSettingKey = "last_show_auto_sync_at";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        registry.Register(SyncTaskId, "Sync Shows", SyncInterval);
        SeedLastRunFromDatabase();

        // Delay startup so the API is fully warmed up first. Offset from the movie
        // sync's 2 minutes so the two don't hit Plex simultaneously on boot.
        await Task.Delay(TimeSpan.FromMinutes(3), ct);

        // A manual trigger forces a sync even when auto-sync is off or the interval has
        // not elapsed — that is the entire point of "Run now".
        var forced = false;

        while (!ct.IsCancellationRequested)
        {
            registry.UpdateInterval(SyncTaskId, SyncInterval);

            try { await RunScheduledAsync(forced, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Show auto-sync check failed"); }

            forced = await WaitForNextAsync(ct);
        }
    }

    /// <summary>
    /// Restores "last run" across restarts from the timestamp the scheduler already
    /// persists, so the Tasks tab is not blank after every deploy.
    /// </summary>
    private void SeedLastRunFromDatabase()
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Database>();
            var raw = db.GetSetting(LastRunSettingKey, "");
            if (long.TryParse(raw, out var unix))
                registry.RecordRun(SyncTaskId,
                    DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
                    TimeSpan.Zero,
                    "completed on a previous run");
        }
        catch (Exception ex) { log.LogWarning(ex, "Show auto-sync: could not seed last-run state"); }
    }

    /// <summary>
    /// Sleeps until the next scheduled check OR until the task is triggered, whichever
    /// comes first. Returns true when woken by a trigger. Mirrors
    /// <see cref="AutoSyncService"/>'s race handling, including cancelling and awaiting
    /// the loser so an abandoned reader can never swallow a later "Run now".
    /// </summary>
    private async Task<bool> WaitForNextAsync(CancellationToken ct)
    {
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(
            (int)-JitterMax.TotalMilliseconds,
            (int) JitterMax.TotalMilliseconds));

        var baseWait = CheckInterval < SyncInterval ? CheckInterval : SyncInterval;

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trigger = registry.WaitForTriggerAsync(SyncTaskId, raceCts.Token);
        var delay   = Task.Delay(baseWait + jitter, raceCts.Token);

        await Task.WhenAny(trigger, delay);

        await raceCts.CancelAsync();
        try { await Task.WhenAll(trigger, delay); }
        catch (OperationCanceledException) { /* expected: we cancelled the loser */ }

        var wokenByTrigger = trigger.IsCompletedSuccessfully;

        return wokenByTrigger && !ct.IsCancellationRequested;
    }

    /// <summary>
    /// Decides whether a sync is due and runs it. Public so the gating can be tested
    /// without driving the timer loop.
    /// </summary>
    public async Task RunScheduledAsync(bool forced, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();
        // ShowSyncService is scoped, so it must be resolved from a scope per run — never
        // captured in this singleton.
        var showSync = scope.ServiceProvider.GetRequiredService<ShowSyncService>();

        if (!forced && db.GetSetting("auto_sync", "false") != "true") return;

        // Never forced past setup — there is no Plex server to sync from yet. No sync
        // ran, so RecordRun must NOT be called: lastRunUtc only ever advances when a
        // sync genuinely runs, otherwise the displayed schedule diverges from the real
        // one driven by the stored timestamp.
        if (!db.IsSetupComplete())
        {
            if (forced) log.LogInformation("Show auto-sync: 'Run now' ignored — setup not complete");
            return;
        }

        if (!forced)
        {
            var lastSyncStr = db.GetSetting(LastRunSettingKey, "");
            if (!string.IsNullOrEmpty(lastSyncStr) &&
                long.TryParse(lastSyncStr, out var lastUnix))
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastUnix;
                if (age < (long)SyncInterval.TotalSeconds) return;
            }
        }

        log.LogInformation("Show auto-sync: starting {Kind} show sync", forced ? "manual" : "scheduled");

        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            // RunOnceAsync has no internal try/catch, so an unreachable Plex would take
            // the scheduler loop down with it without this.
            var count = await showSync.RunOnceAsync(ct);
            sw.Stop();

            db.SetSetting(LastRunSettingKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            registry.RecordRun(SyncTaskId, startedAt, sw.Elapsed, $"synced {count} shows");
        }
        catch (Exception ex)
        {
            sw.Stop();
            // No sync completed, so the clock must not move. RecordFailure updates
            // lastResult without touching lastRunUtc. No exception text goes in the
            // user-facing string — it is logged here with its stack trace instead.
            registry.RecordFailure(SyncTaskId, "failed — see the application log");
            log.LogWarning(ex, "Show sync failed");
        }
    }
}
