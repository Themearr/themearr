using System.Diagnostics;
using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Services;

/// <summary>
/// Background service that triggers a library sync once per day when auto-sync is enabled.
/// Also serves the System → Tasks "Sync Library" row: it reports each run into the
/// <see cref="TaskRegistry"/> and wakes early when the user clicks "Run now".
/// </summary>
public class AutoSyncService(
    IServiceProvider services, TaskRegistry registry, LibrarySourceResolver sources, ILogger<AutoSyncService> log)
    : BackgroundService
{
    public const string SyncTaskId = "syncLibrary";

    // Check every 30 minutes (±5 min jitter) whether a sync is due. Jitter keeps
    // retries from all firing on the same second after a Plex outage recovers.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JitterMax     = TimeSpan.FromMinutes(5);

    // How often a sync is due is a property of the source, not of Themearr.
    private TimeSpan SyncInterval => sources.Active.SyncInterval;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // SyncService is a singleton, so this reference stays valid for the process
        // lifetime and always reflects the sync that's actually running (if any) —
        // unlike TaskRegistry's own IsRunning flag, which StartAsync's fire-and-forget
        // shape (see SyncService.StartAsync) would otherwise make momentarily true at
        // best, since RecordRun clears it again microseconds later.
        var sync = services.GetRequiredService<SyncService>();
        registry.Register(SyncTaskId, "Sync Library", SyncInterval, () => sync.InProgress);
        SeedLastRunFromDatabase();

        // Delay startup by 2 minutes so the API is fully warmed up first
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        // A manual trigger forces a sync even when auto-sync is off or the 24h
        // interval has not elapsed — that is the entire point of "Run now".
        var forced = false;

        while (!ct.IsCancellationRequested)
        {
            try { await TryAutoSync(forced, ct); }
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

        await raceCts.CancelAsync();
        try { await Task.WhenAll(trigger, delay); }
        catch (OperationCanceledException) { /* expected: we cancelled the loser */ }

        // Read the winner AFTER the loser settles. If a trigger landed in the gap
        // between WhenAny resuming and the cancel, the channel byte has already been
        // consumed — reading earlier would report "not triggered" and silently drop
        // that click. A cancelled trigger task is not CompletedSuccessfully, so the
        // timer-won case still returns false.
        var wokenByTrigger = trigger.IsCompletedSuccessfully;

        return wokenByTrigger && !ct.IsCancellationRequested;
    }

    private async Task TryAutoSync(bool forced, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db   = scope.ServiceProvider.GetRequiredService<Database>();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();

        if (!forced && db.GetSetting("auto_sync", "false") != "true") return;

        // Never forced past setup — there is no Plex server to sync from yet. No sync
        // ran, so RecordRun must NOT be called: lastRunUtc only ever advances when a
        // sync genuinely starts, otherwise nextRunUtc (derived from it) diverges from
        // the real schedule driven by the last_auto_sync_at setting.
        if (!db.IsSetupComplete())
        {
            if (forced) log.LogInformation("AutoSync: 'Run now' ignored — setup not complete");
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

        log.LogInformation("AutoSync: starting {Kind} library sync", forced ? "manual" : "scheduled");

        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        try
        {
            var started = await sync.StartAsync();

            if (started)
            {
                db.SetSetting("last_auto_sync_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

                // StartAsync only reports that the job was launched — it runs in the
                // background and fails there. Await the outcome so the Tasks tab can say
                // what actually happened, and so the recorded duration is the sync's
                // rather than the near-zero time it took to start one.
                await sync.Current.WaitAsync(ct);
                sw.Stop();

                registry.RecordRun(SyncTaskId, startedAt, sw.Elapsed,
                    SyncOutcome.Describe(sync.Error, sync.Synced));
            }
            else
            {
                // A sync was already running, so nothing new started here — lastRunUtc
                // must not move (that would silently desync the displayed schedule from
                // the real one, driven by last_auto_sync_at, by up to a full interval).
                // IsRunning is read straight from SyncService via TaskRegistry's probe,
                // so there is nothing here that needs to be cleared either.
                log.LogInformation("AutoSync: sync already in progress, skipping");
            }
        }
        catch
        {
            sw.Stop();
            // Starting failed, so no sync ran here either — lastRunUtc must not move.
            // RecordFailure updates lastResult without touching lastRunUtc, so the
            // Tasks tab stops showing a stale "sync started" as if nothing went
            // wrong. No exception text goes in the fixed string here — ExecuteAsync
            // still logs the exception itself (with its stack trace) one level up.
            registry.RecordFailure(SyncTaskId, "failed to start — see the application log");
            throw;
        }
    }
}
