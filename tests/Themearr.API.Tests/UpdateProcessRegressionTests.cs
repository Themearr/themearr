using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

[CollectionDefinition("Updater environment", DisableParallelization = true)]
public class UpdaterEnvironmentCollection;

[Collection("Updater environment")]
public class UpdateProcessRegressionTests
{
    [Fact]
    public async Task Updater_drains_stderr_before_exit_and_reports_the_real_failure()
    {
        using var dir = new TempDir();
        var script = Path.Combine(dir.Path, "stderr.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho $$ > '" + dir.Path + "/pid'\ni=0\nwhile [ $i -lt 2000 ]; do printf '%0100d stderr-diagnostic\\n' 0 >&2; i=$((i+1)); done\nexit 23\n");
        await RunWithUpdater(dir.Path, $"/bin/sh '{script}'", async service =>
        {
            Assert.True(await service.StartAsync());
            var status = await Finished(service, TimeSpan.FromSeconds(3));
            Assert.Contains("23", status.GetProperty("error").GetString());
            Assert.Contains(status.GetProperty("logs").EnumerateArray(), line => line.GetString()!.Contains("stderr-diagnostic"));
        });
    }

    [Fact]
    public async Task Hung_updater_times_out_releases_admission_and_persists_failure()
    {
        using var dir = new TempDir();
        var script = Path.Combine(dir.Path, "hang.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\necho $$ > '" + dir.Path + "/pid'\nexec sleep 30\n");
        await RunWithUpdater(dir.Path, $"/bin/sh '{script}'", async service =>
        {
            Assert.True(await service.StartAsync());
            var status = await Finished(service, TimeSpan.FromSeconds(3));
            Assert.Contains("timed out", status.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(await service.StartAsync());
            await Finished(service, TimeSpan.FromSeconds(3));
        }, timeoutSeconds: "1");
    }

    [Fact]
    public async Task Bootstrap_download_failure_does_not_execute_partial_script_or_report_success()
    {
        using var dir = new TempDir();
        var bin = Path.Combine(dir.Path, "bin");
        Directory.CreateDirectory(bin);
        var curl = Path.Combine(bin, "curl");
        var marker = Path.Combine(dir.Path, "executed");
        await File.WriteAllTextAsync(curl, $"#!/bin/sh\nprintf \"touch '{marker}'\\n\"\nexit 22\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(curl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldEuid = Environment.GetEnvironmentVariable("EUID");
        Assert.False(File.Exists("/usr/local/bin/themearr-update"), "This fixture must not invoke an installed host updater.");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + oldPath);
            Environment.SetEnvironmentVariable("EUID", "0");
            await RunWithUpdater(dir.Path, "", async service =>
            {
                Assert.True(await service.StartAsync());
                var status = await Finished(service, TimeSpan.FromSeconds(3));
                Assert.False(File.Exists(marker));
                Assert.NotEmpty(status.GetProperty("error").GetString()!);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("EUID", oldEuid);
        }
    }

    private static async Task RunWithUpdater(string path, string command, Func<UpdateService, Task> body, string timeoutSeconds = "600")
    {
        var prior = Environment.GetEnvironmentVariable("THEMEARR_UPDATER_CMD");
        Environment.SetEnvironmentVariable("THEMEARR_UPDATER_CMD", command);
        var db = new Database(Path.Combine(path, "test.db"));
        db.Init();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Themearr:UpdateTimeoutSeconds"] = timeoutSeconds,
        }).Build();
        var service = new UpdateService(db, config);
        try { await body(service); }
        finally
        {
            var pidFile = Path.Combine(path, "pid");
            if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile), out var pid))
            {
                try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            // Ensure the background updater has stopped before deleting its database.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Snapshot(service).GetProperty("inProgress").GetBoolean() && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Environment.SetEnvironmentVariable("THEMEARR_UPDATER_CMD", prior);
        }
    }

    private static JsonElement Snapshot(UpdateService service) => JsonSerializer.SerializeToElement(service.GetStatus());

    private static async Task<JsonElement> Finished(UpdateService service, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = Snapshot(service);
            if (status.GetProperty("finished").GetBoolean()) return status;
            await Task.Delay(10);
        }
        Assert.Fail("The updater did not finish within the bounded wait.");
        return default;
    }
}
