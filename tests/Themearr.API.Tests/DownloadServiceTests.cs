using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class DownloadServiceTests
{
    private const string YtUrl = "https://www.youtube.com/watch?v=abc12345678";

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>Provider that never returns until its token is cancelled — models a stalled CDN.</summary>
    private sealed class HangingProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public async Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    /// <summary>Provider that hangs forever and IGNORES its cancellation token — models a
    /// pathological backend that doesn't honour the timeout, which the watchdog must survive.</summary>
    private sealed class StubbornProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public async Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None); // never observes ct
            return null;
        }
    }

    /// <summary>Provider that always reports the RapidAPI quota is exhausted (HTTP 429).</summary>
    private sealed class QuotaExhaustedProvider : IThemeAudioProvider
    {
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
            => throw new QuotaExceededException("RapidAPI quota exceeded (HTTP 429)");
    }

    /// <summary>Provider that records the token it received and writes a valid theme file.</summary>
    private sealed class RecordingProvider : IThemeAudioProvider
    {
        public CancellationToken SeenToken;
        public string? CheckConfiguration() => null;
        public Task<string?> DownloadAsync(string videoId, string outputPath, Action<string> progress, CancellationToken ct)
        {
            SeenToken = ct;
            File.WriteAllBytes(outputPath, new byte[] { 0x49, 0x44, 0x33, 9, 9, 9 });
            return Task.FromResult<string?>("Recorded Theme");
        }
    }

    private static (DownloadService svc, Database db, string movieId) Build(
        string movieFolder, IThemeAudioProvider provider, int timeoutSeconds, int watchdogGraceSeconds = 30)
    {
        var dbDir = Path.Combine(Path.GetTempPath(), "themearr-test-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        var db = new Database(Path.Combine(dbDir, "themearr.db"));
        db.Init();

        var movieId = MovieFolderId.For(movieFolder);
        db.UpsertMovies([new MovieRecord(movieFolder, "srv1", "rk1", "Test Movie", 2020, "/plex/Test Movie/movie.mkv")]);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Themearr:DownloadTimeoutSeconds"] = timeoutSeconds.ToString(),
                ["Themearr:DownloadWatchdogGraceSeconds"] = watchdogGraceSeconds.ToString(),
            })
            .Build();

        var svc = new DownloadService(provider, db, new StubHttpClientFactory(), config, NullLogger<DownloadService>.Instance);
        return (svc, db, movieId);
    }

    private static async Task<object> WaitForFinish(DownloadService svc, string movieId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = svc.GetStatus(movieId);
            if ((bool)Prop(status, "finished")!) return status;
            await Task.Delay(100);
        }
        return svc.GetStatus(movieId);
    }

    private static object? Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o);

    // ── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_providerHangs_isBoundedByTimeout_andJobNotStuckInProgress()
    {
        using var movieDir = new TempDir();
        var (svc, _, movieId) = Build(movieDir.Path, new HangingProvider(), timeoutSeconds: 1);

        Assert.True(svc.Start(movieId, YtUrl));

        // Pre-fix (no timeout, default token) the job stays InProgress forever, which
        // wedges the auto-download loop's IsAnyInProgress() gate until a restart.
        var status = await WaitForFinish(svc, movieId, TimeSpan.FromSeconds(8));

        Assert.True((bool)Prop(status, "finished")!);
        Assert.False((bool)Prop(status, "inProgress")!);
        Assert.False(svc.IsAnyInProgress());
        Assert.Contains("timed out", (string?)Prop(status, "error") ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_jobStuckPastWatchdog_noLongerBlocksAutoLoop()
    {
        using var movieDir = new TempDir();
        // 1s timeout + 1s grace = stale after ~2s, even if the provider ignores cancellation.
        var (svc, _, movieId) = Build(movieDir.Path, new StubbornProvider(), timeoutSeconds: 1, watchdogGraceSeconds: 1);

        Assert.True(svc.Start(movieId, YtUrl));
        Assert.True(svc.IsAnyInProgress()); // blocks the loop right after starting

        // The watchdog must age the stuck job out so the auto-download loop is never
        // wedged forever by a single pathological download.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (svc.IsAnyInProgress() && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        Assert.False(svc.IsAnyInProgress());
    }

    [Fact]
    public async Task Download_passesCancellableTokenToProvider_andMarksDownloaded()
    {
        using var movieDir = new TempDir();
        var provider = new RecordingProvider();
        var (svc, db, movieId) = Build(movieDir.Path, provider, timeoutSeconds: 900);

        Assert.True(svc.Start(movieId, YtUrl));
        var status = await WaitForFinish(svc, movieId, TimeSpan.FromSeconds(8));

        Assert.True((bool)Prop(status, "finished")!);
        Assert.Null((string?)Prop(status, "error"));
        // The poison-pill fix: the provider must get a real (cancellable) token, not default.
        Assert.True(provider.SeenToken.CanBeCanceled);
        Assert.Equal("downloaded", db.GetMovie(movieId)!["status"]);
    }

    [Fact]
    public async Task DownloadBlockedReason_afterQuota429_gatesProviderUrls_butNotDirectUrls()
    {
        using var movieDir = new TempDir();
        var (svc, _, movieId) = Build(movieDir.Path, new QuotaExhaustedProvider(), timeoutSeconds: 900);

        Assert.Null(svc.DownloadBlockedReason(isProviderUrl: true)); // not cooling down yet

        Assert.True(svc.Start(movieId, YtUrl));
        await WaitForFinish(svc, movieId, TimeSpan.FromSeconds(8));

        // After a 429, provider-bound downloads (manual AND auto) must be gated with a
        // clear quota message instead of burning another billed request.
        var reason = svc.DownloadBlockedReason(isProviderUrl: true);
        Assert.NotNull(reason);
        Assert.Contains("quota", reason, StringComparison.OrdinalIgnoreCase);

        // A direct (non-provider) URL doesn't touch RapidAPI, so it must not be gated.
        Assert.Null(svc.DownloadBlockedReason(isProviderUrl: false));
    }

    [Fact]
    public async Task Download_unwritableFolder_failsWithClearError()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX perms only

        using var movieDir = new TempDir();
        var (svc, _, movieId) = Build(movieDir.Path, new RecordingProvider(), timeoutSeconds: 900);
        System.Diagnostics.Process.Start("chmod", $"555 {movieDir.Path}")!.WaitForExit();
        try
        {
            Assert.True(svc.Start(movieId, YtUrl));
            var status = await WaitForFinish(svc, movieId, TimeSpan.FromSeconds(8));

            Assert.True((bool)Prop(status, "finished")!);
            var error = (string?)Prop(status, "error") ?? "";
            Assert.Contains("write", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            System.Diagnostics.Process.Start("chmod", $"755 {movieDir.Path}")!.WaitForExit();
        }
    }
}
