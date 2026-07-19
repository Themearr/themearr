using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class HealthCacheTests
{
    private sealed class CountingHealthCheckService : HealthCheckService
    {
        public int Calls;

        public override Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
        {
            Calls++;
            var entries = new Dictionary<string, HealthReportEntry>
            {
                ["plex"] = new(HealthStatus.Healthy, "reachable", TimeSpan.Zero, exception: null, data: null),
            };
            return Task.FromResult(new HealthReport(entries, TimeSpan.Zero));
        }
    }

    [Fact]
    public async Task Repeated_calls_within_the_ttl_run_the_checks_only_once()
    {
        var service = new CountingHealthCheckService();
        var cache   = new HealthCache(service);

        for (var i = 0; i < 5; i++) await cache.GetAsync();

        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_collapse_into_a_single_run()
    {
        var service = new CountingHealthCheckService();
        var cache   = new HealthCache(service);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetAsync()));

        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Both_shapes_describe_the_same_report()
    {
        var cache = new HealthCache(new CountingHealthCheckService());

        var cached = await cache.GetAsync();

        Assert.Equal(HealthStatus.Healthy, cached.Status);
        Assert.Equal("ok", cached.Response.Status);
    }

    // Simulates a hung dependency (e.g. ThemeFiles.IsDirectoryWritable blocking on a
    // wedged NFS/SMB mount): CheckHealthAsync never completes on its own, and —
    // critically — Thread.Sleep is NON-cooperative, so it ignores the cancellation
    // token entirely, exactly like the real File.Create syscall it stands in for.
    //
    // An earlier version of this fake used Task.Delay(Timeout.Infinite,
    // cancellationToken), which is COOPERATIVE: it returns as soon as the token
    // fires. That made a broken fix (a bare `await health.CheckHealthAsync(...)`
    // with no independent bound) look correct, because the fake honored
    // cancellation the real blocking call never would — the test gave false
    // confidence. Thread.Sleep closes that gap.
    private sealed class HangingHealthCheckService : HealthCheckService
    {
        public override async Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
        {
            // Verified empirically that the real DefaultHealthCheckService.CheckHealthAsync
            // yields before invoking a check's delegate (it returns a pending Task
            // promptly even when the check blocks synchronously) — Task.Yield here
            // reproduces that so this fake is called the same way GetAsync calls the
            // real thing, rather than blocking the caller synchronously at the call site.
            await Task.Yield();
            Thread.Sleep(Timeout.Infinite);
            throw new InvalidOperationException("unreachable — Thread.Sleep(Infinite) never returns");
        }
    }

    [Fact]
    public async Task A_hung_check_does_not_wedge_GetAsync_forever()
    {
        // Use a short refresh timeout so the test itself stays fast; production uses 10s.
        var cache = new HealthCache(new HangingHealthCheckService(), refreshTimeout: TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        // The 5s WaitAsync here is only a CI safety valve (fail fast instead of
        // hanging the suite if this regresses) — the real proof that GetAsync is
        // bounded by the refresh timeout, not by luck, is the elapsed-time assert
        // below.
        var cached = await cache.GetAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HealthStatus.Unhealthy, cached.Status);
        Assert.NotEmpty(cached.Response.Checks);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"expected GetAsync to return promptly (~200ms refreshTimeout), took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task A_hung_checks_degraded_result_is_cached_for_the_normal_ttl()
    {
        // A wedged mount must not cause a fresh probe on every single poll — the
        // degraded result should be served from cache like any other result.
        var service = new HangingHealthCheckService();
        var cache = new HealthCache(service, refreshTimeout: TimeSpan.FromMilliseconds(200));

        var first  = await cache.GetAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var second = await cache.GetAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HealthStatus.Unhealthy, first.Status);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task The_winning_callers_own_cancellation_does_not_abort_the_refresh_or_leave_the_cache_empty()
    {
        // A caller that wins the semaphore and then disconnects mid-probe (its token
        // fires while the refresh is under way) must not cancel that refresh —
        // otherwise the cache is never populated and the next caller just starts
        // another probe (a probe storm on the unauthenticated, unrate-limited
        // /health endpoint). Only _lock.WaitAsync may observe the caller's token.
        var service = new SlowThenHealthyService(TimeSpan.FromMilliseconds(300));
        var cache = new HealthCache(service);

        // Fires well before the 300ms refresh finishes, but after the (uncontended,
        // effectively instant) lock acquisition.
        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var cached = await cache.GetAsync(callerCts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, service.Calls);
        Assert.Equal(HealthStatus.Healthy, cached.Status);
    }

    [Fact]
    public async Task A_caller_still_waiting_for_the_lock_is_cancellable()
    {
        // The other half of the contract: a caller queued behind an in-progress
        // refresh CAN be cancelled while it is merely waiting for the lock — that
        // wait, and only that wait, is the caller's own token to cancel.
        var service = new SlowThenHealthyService(TimeSpan.FromSeconds(2));
        var cache = new HealthCache(service);

        var winner = cache.GetAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(50)); // let the winner take the lock

        using var waiterCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(waiterCts.Token));

        var result = await winner.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private sealed class SlowThenHealthyService(TimeSpan delay) : HealthCheckService
    {
        public int Calls;

        public override async Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            // Intentionally ignores cancellationToken: an individual caller's token
            // must not be able to cancel a refresh already in progress.
            await Task.Delay(delay, CancellationToken.None);
            var entries = new Dictionary<string, HealthReportEntry>
            {
                ["plex"] = new(HealthStatus.Healthy, "reachable", TimeSpan.Zero, exception: null, data: null),
            };
            return new HealthReport(entries, TimeSpan.Zero);
        }
    }
}
