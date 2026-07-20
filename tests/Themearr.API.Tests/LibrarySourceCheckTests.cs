using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Health;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class LibrarySourceCheckTests
{
    private sealed class FakeSource(string name, string? reason) : ILibrarySource
    {
        public string   Name         => name;
        public TimeSpan SyncInterval => TimeSpan.FromHours(24);

        public Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MovieRecord>>([]);

        public Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct) =>
            Task.FromResult<Stream?>(null);

        public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult(reason);

        public string? SyncBlockedReason => null;
    }

    private static Task<HealthCheckResult> Run(TempDir dir, string? reason, bool setupComplete = true)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        if (setupComplete) db.MarkSetupComplete();
        var resolver = new LibrarySourceResolver(db, [new FakeSource("plex", reason)]);
        return new LibrarySourceCheck(db, resolver)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
    }

    [Fact]
    public async Task A_healthy_source_reports_healthy()
    {
        using var dir = new TempDir();
        Assert.Equal(HealthStatus.Healthy, (await Run(dir, reason: null)).Status);
    }

    [Fact]
    public async Task An_unhealthy_source_surfaces_its_reason()
    {
        using var dir = new TempDir();

        var result = await Run(dir, reason: "Radarr rejected the API key (401).");

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("rejected the API key", result.Description);
    }

    [Fact]
    public async Task Before_setup_completes_it_is_healthy_even_if_the_source_is_broken()
    {
        using var dir = new TempDir();

        // A fresh install has nothing configured yet and is not broken.
        Assert.Equal(HealthStatus.Healthy,
            (await Run(dir, reason: "unreachable", setupComplete: false)).Status);
    }
}
