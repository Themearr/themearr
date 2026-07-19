using System.Diagnostics;
using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Background service that triggers a Plex sync once per day when auto-sync is enabled.
/// Also serves the System → Tasks "Sync Library" row: it reports each run into the
/// <see cref="TaskRegistry"/> and wakes early when the user clicks "Run now".
/// </summary>
public class AutoSyncService(IServiceProvider services, TaskRegistry registry, ILogger<AutoSyncService> log)
    : BackgroundService
{
    public const string SyncTaskId = "syncLibrary";

    // Check every 30 minutes (±5 min jitter) whether a sync is due. Jitter keeps
    // retries from all firing on the same second after a Plex outage recovers.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JitterMax     = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SyncInterval  = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        registry.Register(SyncTaskId, "Sync Library", SyncInterval);
        SeedLastRunFromDatabase();

        // Delay startup by 2 minutes so the API is fully warmed up first
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        // A manual trigger forces a sync even when auto-sync is off or the 24h
        // interval has not elapsed — that is the entire point of "Run now".
        var forced = false;

        while (!ct.IsCancellationRequested)
        {
            try { await TryAutoSync(forced); }
            catch (Exception ex) { log.LogWarning(ex, "AutoSync check failed"); }

            forced = await WaitForNextAsync(ct);
        }
    }

    /// <summary>
    /// Restores "last run" across restarts from the timestamp auto-sync already
    /// persists, so the Tasks tab is not blank after every deploy.
    /// </summary>
    private void SeedLastRunFromDatabase()
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Database>();
            var raw = db.GetSetting("last_auto_sync_at", "");
            if (long.TryParse(raw, out var unix))
                registry.RecordRun(SyncTaskId,
                    DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime,
                    TimeSpan.Zero,
                    "completed on a previous run");
        }
        catch (Exception ex) { log.LogWarning(ex, "AutoSync: could not seed last-run state"); }
    }

    /// <summary>
    /// Sleeps until the next scheduled check OR until the task is triggered,
    /// whichever comes first. Returns true when woken by a trigger.
    /// The loser of the race is cancelled and awaited, so an abandoned reader can
    /// never sit on the trigger channel and swallow a later "Run now".
    /// </summary>
    private async Task<bool> WaitForNextAsync(CancellationToken ct)
    {
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(
            (int)-JitterMax.TotalMilliseconds,
            (int) JitterMax.TotalMilliseconds));

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trigger = registry.WaitForTriggerAsync(SyncTaskId, raceCts.Token);
        var delay   = Task.Delay(CheckInterval + jitter, raceCts.Token);

        await Task.WhenAny(trigger, delay);
        var wokenByTrigger = trigger.IsCompletedSuccessfully;

        await raceCts.CancelAsync();
        try { await Task.WhenAll(trigger, delay); }
        catch (OperationCanceledException) { /* expected: we cancelled the loser */ }

        return wokenByTrigger && !ct.IsCancellationRequested;
    }

    private async Task TryAutoSync(bool forced)
    {
        using var scope = services.CreateScope();
        var db   = scope.ServiceProvider.GetRequiredService<Database>();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();

        if (!forced && db.GetSetting("auto_sync", "false") != "true") return;

        // Never forced past setup — there is no Plex server to sync from yet.
        if (!db.IsSetupComplete())
        {
            if (forced) registry.RecordRun(SyncTaskId, DateTime.UtcNow, TimeSpan.Zero, "skipped: setup not complete");
            return;
        }

        if (!forced)
        {
            var lastSyncStr = db.GetSetting("last_auto_sync_at", "");
            if (!string.IsNullOrEmpty(lastSyncStr) &&
                long.TryParse(lastSyncStr, out var lastUnix))
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastUnix;
                if (age < (long)SyncInterval.TotalSeconds) return;
            }
        }

        log.LogInformation("AutoSync: starting {Kind} Plex sync", forced ? "manual" : "scheduled");

        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        registry.MarkRunning(SyncTaskId, true);
        try
        {
            var started = await sync.StartAsync();
            sw.Stop();

            if (started)
            {
                db.SetSetting("last_auto_sync_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                registry.RecordRun(SyncTaskId, startedAt, sw.Elapsed, "sync started");
            }
            else
            {
                log.LogInformation("AutoSync: sync already in progress, skipping");
                registry.RecordRun(SyncTaskId, startedAt, sw.Elapsed, "skipped: a sync was already running");
            }
        }
        catch
        {
            sw.Stop();
            // RecordRun also clears IsRunning, so the Run now button recovers.
            registry.RecordRun(SyncTaskId, startedAt, sw.Elapsed, "failed to start — see the application log");
            throw;   // ExecuteAsync still logs the exception with its stack trace
        }
    }
}
