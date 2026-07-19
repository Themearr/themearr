using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class PassiveHealthCheckTests
{
    private sealed class FakeProvider(string? configurationError) : IThemeAudioProvider
    {
        public string? CheckConfiguration() => configurationError;

        public Task<string?> DownloadAsync(
            string videoId, string outputPath, Action<string> progress, CancellationToken ct = default) =>
            throw new NotSupportedException("not used by health checks");
    }

    private sealed class FakeWorker(DateTime? lastTickAt, string lastResult) : IDownloadWorkerStatus
    {
        public DateTime? LastTickAt     => lastTickAt;
        public string    LastTickResult => lastResult;
    }

    private static Database NewDb(TempDir dir, bool setupComplete)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        if (setupComplete) db.MarkSetupComplete();
        return db;
    }

    private static Task<HealthCheckResult> Run(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    // ── RapidApiCheck ────────────────────────────────────────────────────────

    [Fact]
    public async Task RapidApi_before_setup_is_healthy_even_with_no_key()
    {
        using var dir = new TempDir();
        var check = new RapidApiCheck(NewDb(dir, setupComplete: false), new FakeProvider("no key"));

        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task RapidApi_without_a_key_is_an_error_carrying_the_reason()
    {
        using var dir = new TempDir();
        var check = new RapidApiCheck(NewDb(dir, setupComplete: true), new FakeProvider("RapidAPI key is not set"));

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("RapidAPI key is not set", result.Description);
    }

    [Fact]
    public async Task RapidApi_configured_is_healthy()
    {
        using var dir = new TempDir();
        var check = new RapidApiCheck(NewDb(dir, setupComplete: true), new FakeProvider(null));

        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);
    }

    // ── DownloadWorkerCheck ──────────────────────────────────────────────────

    [Fact]
    public async Task Worker_is_healthy_when_auto_download_is_off_despite_a_stale_tick()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, setupComplete: true);
        db.SetSetting("auto_download", "false");
        var worker = new FakeWorker(DateTime.UtcNow.AddHours(-3), "skipped: auto_download is off");

        Assert.Equal(HealthStatus.Healthy, (await Run(new DownloadWorkerCheck(db, worker))).Status);
    }

    [Fact]
    public async Task Worker_with_a_stale_tick_while_enabled_is_an_error()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, setupComplete: true);
        db.SetSetting("auto_download", "true");
        var worker = new FakeWorker(DateTime.UtcNow.AddMinutes(-30), "started 'Heat' -> abc123");

        var result = await Run(new DownloadWorkerCheck(db, worker));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("30", result.Description);
    }

    [Fact]
    public async Task Worker_that_ticked_recently_is_healthy()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, setupComplete: true);
        db.SetSetting("auto_download", "true");
        var worker = new FakeWorker(DateTime.UtcNow.AddSeconds(-20), "skipped: no pending movies");

        Assert.Equal(HealthStatus.Healthy, (await Run(new DownloadWorkerCheck(db, worker))).Status);
    }

    [Fact]
    public async Task Worker_that_has_never_ticked_is_healthy_because_it_is_still_warming_up()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, setupComplete: true);
        db.SetSetting("auto_download", "true");

        Assert.Equal(HealthStatus.Healthy,
            (await Run(new DownloadWorkerCheck(db, new FakeWorker(null, "never run")))).Status);
    }
}
