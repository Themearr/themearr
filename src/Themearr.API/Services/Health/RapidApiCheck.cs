using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Passive by design. youtube-mp36 has no free quota endpoint, so actively probing
/// "is RapidAPI healthy" would spend a request off the free tier — quota taken
/// straight from downloads. This reads only state Themearr already holds.
/// </summary>
public sealed class RapidApiCheck(Database db, IThemeAudioProvider provider, IQuotaStatus quota) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.IsSetupComplete())
            return Task.FromResult(HealthCheckResult.Healthy("Setup not complete"));

        if (provider.CheckConfiguration() is { } notReady)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Theme downloads are disabled: {notReady}"));

        // A 429 sets a cooldown. Report it rather than probing, which would cost quota.
        if (quota.IsQuotaCoolingDown(out var until))
            return Task.FromResult(HealthCheckResult.Degraded(
                $"RapidAPI quota is exhausted — downloads are paused until {until:HH:mm} UTC"));

        return Task.FromResult(HealthCheckResult.Healthy("RapidAPI is configured"));
    }
}
