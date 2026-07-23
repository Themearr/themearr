using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Detects a wedged auto-download worker. The worker ticks every 30 seconds, so a
/// gap of minutes means it is stuck rather than idle.
/// </summary>
public sealed class DownloadWorkerCheck(Database db, IDownloadWorkerStatus worker) : IHealthCheck
{
    private static readonly TimeSpan MaxTickAge = TimeSpan.FromMinutes(5);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // A disabled worker does not tick. That is a setting, not a fault.
        if (db.GetSetting("auto_download", "false") != "true")
            return Task.FromResult(HealthCheckResult.Healthy("Auto-download is off"));

        if (worker.LastTickAt is not { } last)
        {
            // Never ticked. "Just started" is fine (there's a warm-up delay before the
            // first tick), but a worker that started long ago and STILL hasn't ticked
            // died during warm-up -- and must not masquerade as "starting up" forever.
            if (worker.StartedAt is { } started && DateTime.UtcNow - started > MaxTickAge)
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"The auto-download worker started {(int)(DateTime.UtcNow - started).TotalMinutes} minutes ago " +
                    "but has never run a tick (it should run every 30 seconds). It may have died during start-up."));
            return Task.FromResult(HealthCheckResult.Healthy("Auto-download worker is starting up"));
        }

        var age = DateTime.UtcNow - last;
        if (age > MaxTickAge)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"The auto-download worker has not run for {(int)age.TotalMinutes} minutes " +
                $"(it should run every 30 seconds). Last result: {worker.LastTickResult}"));

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Last tick {(int)age.TotalSeconds}s ago: {worker.LastTickResult}"));
    }
}
