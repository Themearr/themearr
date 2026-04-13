using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Background service that triggers a Plex sync once per day when auto-sync is enabled.
/// </summary>
public class AutoSyncService(IServiceProvider services, ILogger<AutoSyncService> log)
    : BackgroundService
{
    // Check every 30 minutes whether a sync is due
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SyncInterval  = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Delay startup by 2 minutes so the API is fully warmed up first
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await TryAutoSync(); }
            catch (Exception ex) { log.LogWarning(ex, "AutoSync check failed"); }

            await Task.Delay(CheckInterval, ct);
        }
    }

    private async Task TryAutoSync()
    {
        using var scope = services.CreateScope();
        var db   = scope.ServiceProvider.GetRequiredService<Database>();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();

        if (db.GetSetting("auto_sync", "false") != "true") return;
        if (!db.IsSetupComplete()) return;

        var lastSyncStr = db.GetSetting("last_auto_sync_at", "");
        if (!string.IsNullOrEmpty(lastSyncStr) &&
            long.TryParse(lastSyncStr, out var lastUnix))
        {
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - lastUnix;
            if (age < (long)SyncInterval.TotalSeconds) return;
        }

        log.LogInformation("AutoSync: starting scheduled Plex sync");
        var started = await sync.StartAsync();
        if (started)
            db.SetSetting("last_auto_sync_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        else
            log.LogInformation("AutoSync: sync already in progress, skipping");
    }
}
