using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Themearr.API.Services.Health;

/// <summary>Both shapes of the same cached report: the framework status for the bare
/// monitoring endpoint, and the arr-shaped payload for the UI.</summary>
public sealed record CachedHealth(HealthStatus Status, HealthResponse Response);

/// <summary>
/// Caches the health report server-side for 60 seconds. Without this, the sidebar
/// badge would ping the user's Plex server once per open browser tab per poll —
/// three tabs left open overnight would be thousands of probes. Caching here (not
/// in the client) collapses N tabs into one probe. Mirrors UpdateService's cache.
/// </summary>
public sealed class HealthCache(HealthCheckService health)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private CachedHealth? _cached;
    private DateTime _expiresAt = DateTime.MinValue;

    public async Task<CachedHealth> GetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTime.UtcNow < _expiresAt) return _cached;

            var report = await health.CheckHealthAsync(ct);
            _cached    = new CachedHealth(report.Status, HealthDto.From(report));
            _expiresAt = DateTime.UtcNow.Add(Ttl);
            return _cached;
        }
        finally { _lock.Release(); }
    }
}
