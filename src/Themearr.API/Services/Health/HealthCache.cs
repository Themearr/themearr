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
public sealed class HealthCache(HealthCheckService health, IConfiguration? config = null, TimeSpan? refreshTimeout = null)
{
    // Falls back to an empty configuration (env var / hardcoded default still apply
    // via GithubRepoResolver) when constructed without DI, e.g. in tests that don't
    // care about the repo-link resolution this parameter exists for.
    private readonly IConfiguration _config = config ?? EmptyConfig;

    private static readonly IConfiguration EmptyConfig = new ConfigurationBuilder().Build();

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    // A hung network mount (NFS/SMB) is a real, expected failure mode here — it is
    // precisely the kind of problem this page exists to diagnose — and
    // ThemeFiles.IsDirectoryWritable's blocking File.Create has no timeout of its
    // own. Without bounding the refresh, one wedged mount holds `_lock` forever and
    // wedges /health and /api/system/health for every browser tab, since the
    // sidebar badge polls both. 10s is comfortably above the 3s Plex client timeout
    // so a merely-slow (but working) Plex server still completes normally.
    private static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _refreshTimeout = refreshTimeout ?? DefaultRefreshTimeout;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private CachedHealth? _cached;
    private DateTime _expiresAt = DateTime.MinValue;

    // Cancellation is cooperative, but the failure mode this guards against is
    // not: LibraryPathsCheck is fully synchronous and never observes its token,
    // and ThemeFiles.IsDirectoryWritable's File.Create on a wedged NFS/SMB mount
    // is an uninterruptible syscall. So a timed-out probe is ABANDONED, not
    // cancelled — WaitAsync below hands control back to the caller regardless,
    // but the CheckHealthAsync Task keeps running (or hanging) in the
    // background for as long as the mount stays wedged. If every TTL expiry
    // during that window started a brand new probe, those would accumulate
    // without bound. Instead we hold the one outstanding probe (and the
    // CancellationTokenSource bounding its cooperative parts) here and reuse it
    // across refreshes until it actually completes — capping the leak at
    // exactly one probe no matter how long the mount stays hung.
    private Task<HealthReport>? _pendingProbe;
    private CancellationTokenSource? _pendingCts;

    public async Task<CachedHealth> GetAsync(CancellationToken ct = default)
    {
        // Only waiting for the lock is cancellable by an individual caller. Once a
        // caller wins the lock it is refreshing the shared cache on behalf of every
        // caller, including ones that haven't asked yet — if its own request were
        // cancelled mid-probe (e.g. a monitor whose client timeout is shorter than a
        // slow Plex response) and that cancelled the refresh too, the cache would
        // never get populated and the next caller would just start another probe.
        // /health is unauthenticated and unrate-limited, so that would be a probe
        // storm waiting to happen.
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTime.UtcNow < _expiresAt) return _cached;

            CachedHealth result;
            try
            {
                // Reuse the outstanding probe if the previous refresh's is still running
                // (see the field comments above); otherwise start a fresh one.
                if (_pendingProbe is null || _pendingProbe.IsCompleted)
                {
                    _pendingCts?.Dispose();
                    _pendingCts    = new CancellationTokenSource(_refreshTimeout);
                    _pendingProbe  = health.CheckHealthAsync(_pendingCts.Token);
                }

                var report = await _pendingProbe.WaitAsync(_refreshTimeout);
                result = new CachedHealth(report.Status, HealthDto.From(report, _config));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // WaitAsync gave up on THIS attempt only — do not propagate a raw
                // cancellation/timeout to the caller, and do not leave the cache
                // empty: cache a degraded-but-useful result for the normal TTL so a
                // wedged mount can't cause a probe storm either. _pendingProbe is
                // deliberately left set (see field comments) so the next refresh
                // reuses it instead of piling on a second stuck probe.
                result = TimedOutResult();
            }
            catch (Exception)
            {
                // A HealthCheckRegistration whose factory throws (e.g. bad DI
                // config) must not escape as a 500 from the unauthenticated,
                // unrate-limited /health endpoint — that would leave the cache
                // unpopulated and reopen the exact probe storm this cache exists to
                // prevent. Cache a degraded (never "healthy") result instead.
                result = ErrorResult();
            }

            _cached    = result;
            _expiresAt = DateTime.UtcNow.Add(Ttl);
            return _cached;
        }
        finally { _lock.Release(); }
    }

    private CachedHealth TimedOutResult() => DegradedResult(
        $"Health checks did not complete within {_refreshTimeout.TotalSeconds:0}s — a dependency " +
        "(e.g. a network mount) may be hung. See the application log.");

    private static CachedHealth ErrorResult() => DegradedResult(
        "Health checks failed unexpectedly. See the application log.");

    private static CachedHealth DegradedResult(string message)
    {
        var item = new HealthItem("health", HealthDto.MapType(HealthStatus.Unhealthy), message, null);
        return new CachedHealth(HealthStatus.Unhealthy, new HealthResponse("error", [item]));
    }
}
