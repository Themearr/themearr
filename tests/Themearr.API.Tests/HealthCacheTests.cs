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
}
