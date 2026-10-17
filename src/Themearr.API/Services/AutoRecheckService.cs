using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>Re-judges stored themes once after a matching-rules upgrade, without searching.</summary>
public sealed class AutoRecheckService(Database db, ThemeRecheckService recheck, ILogger<AutoRecheckService> log)
    : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunScheduledAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Automatic theme re-check failed; will retry"); }
            await Task.Delay(RetryInterval, stoppingToken);
        }
    }

    public async Task RunScheduledAsync(CancellationToken ct)
    {
        await _runGate.WaitAsync(ct);
        try
        {
            if (!db.IsSetupComplete()) return;
            var stored = int.TryParse(db.GetSetting("match_rules_version", "0"), out var version) ? version : 0;
            // Never downgrade the marker if an older binary is temporarily restored.
            if (stored >= ThemeMatch.RulesVersion) return;
            if (!await recheck.StartAsync(ct)) return;
            await recheck.Current.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            // Failed or interrupted scans retain the old marker so the next check retries.
            if (recheck.Succeeded) db.SetSetting("match_rules_version", ThemeMatch.RulesVersion.ToString());
        }
        finally { _runGate.Release(); }
    }
}
