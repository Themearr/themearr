using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class LibraryPathsCheckTests
{
    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        return db;
    }

    private static Task<HealthCheckResult> Run(Database db) =>
        new LibraryPathsCheck(db).CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task Before_setup_completes_it_reports_healthy()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.SetLibraryPaths(["/definitely/does/not/exist"]);

        Assert.Equal(HealthStatus.Healthy, (await Run(db)).Status);
    }

    [Fact]
    public async Task No_configured_paths_is_an_error()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.MarkSetupComplete();

        var result = await Run(db);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("No library paths", result.Description);
    }

    [Fact]
    public async Task A_missing_path_is_an_error_naming_the_path()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.MarkSetupComplete();
        var missing = Path.Combine(dir.Path, "gone");
        db.SetLibraryPaths([missing]);

        var result = await Run(db);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(missing, result.Description);
    }

    [Fact]
    public async Task Unresolved_movies_from_the_last_sync_are_a_warning_with_a_sample()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.MarkSetupComplete();
        db.SetLibraryPaths([dir.Path]);
        db.SetSetting("last_sync_unresolved_count", "142");
        db.SetSetting("last_sync_unresolved_sample", @"P:\Movies\Heat (1995)\heat.mkv");

        var result = await Run(db);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("142", result.Description);
        Assert.Contains("Path Mappings", result.Description);
        Assert.Contains(@"P:\Movies", result.Description);
    }

    [Fact]
    public async Task A_good_writable_path_with_no_unresolved_movies_is_healthy()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        db.MarkSetupComplete();
        db.SetLibraryPaths([dir.Path]);
        db.SetSetting("last_sync_unresolved_count", "0");

        Assert.Equal(HealthStatus.Healthy, (await Run(db)).Status);
    }
}
