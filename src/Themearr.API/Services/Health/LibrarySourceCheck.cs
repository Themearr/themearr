using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Sources;

namespace Themearr.API.Services.Health;

/// <summary>
/// Reports on whichever library source is active. One check rather than one per source:
/// only the configured source can ever be relevant, so a second would be noise, and a
/// third source gets health coverage for free.
/// </summary>
public sealed class LibrarySourceCheck(Database db, LibrarySourceResolver sources) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Before setup there is nothing configured; a fresh install is not broken.
        if (!db.IsSetupComplete()) return HealthCheckResult.Healthy("Setup not complete");

        var source = sources.Active;
        var reason = await source.CheckAsync(cancellationToken);
        return reason is null
            ? HealthCheckResult.Healthy($"{source.Name} is reachable")
            : HealthCheckResult.Unhealthy(reason);
    }
}
