using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Passive by design. youtube-mp36 has no free quota endpoint, so actively probing
/// "is RapidAPI healthy" would spend a request off the free tier — quota taken
/// straight from downloads. This reads only state Themearr already holds.
/// </summary>
public sealed class RapidApiCheck(Database db, IThemeAudioProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.IsSetupComplete())
            return Task.FromResult(HealthCheckResult.Healthy("Setup not complete"));

        if (provider.CheckConfiguration() is { } notReady)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Theme downloads are disabled: {notReady}"));

        return Task.FromResult(HealthCheckResult.Healthy("RapidAPI is configured"));
    }
}
