using System.Diagnostics;

namespace Themearr.API.Services;

public sealed class NativePermissionRepairService(ILogger<NativePermissionRepairService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Docker and development checkouts must never invoke a host updater. Native
        // systemd installations use this fixed directory and a non-root service user.
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root"
            || Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) != "/opt/themearr") return;
        try
        {
            await RepairIfNeededAsync("/opt/themearr", "/usr/local/bin/themearr-update", true, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Native permission repair failed: {Error}. Run the release deploy.sh as root to repair application ownership; existing data is retained.",
                LogSanitizer.Clean(ex.Message));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task<bool> RepairIfNeededAsync(string installDir, string helper, bool useSudo, CancellationToken token)
    {
        if (!File.Exists(helper) || !Directory.Exists(Path.Combine(installDir, "data"))) return false;
        // Root-owned application trees fail this probe. Testing mode bits alone misses
        // ACL grants and cannot tell which uid owns a legacy installation.
        var probe = Path.Combine(installDir, $".permission-probe-{Guid.NewGuid():N}");
        try
        {
            using var writable = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }

        var marker = Path.Combine(installDir, "data", ".repair-permissions");
        try
        {
            using var request = new FileStream(marker, FileMode.CreateNew, FileAccess.Write);
        }
        catch (IOException) when (File.Exists(marker)) { }

        // Old helpers discard arguments, so deploy.sh also recognizes the marker.
        // This path performs ownership repair only, with no second release download.
        var start = new ProcessStartInfo(useSudo ? "sudo" : helper);
        if (useSudo)
        {
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add(helper);
        }
        start.ArgumentList.Add("--repair-permissions");
        var exit = await UpdateProcessRunner.RunAsync(start,
            line => logger.LogInformation("Native permission repair: {Message}", LogSanitizer.Clean(line)),
            TimeSpan.FromSeconds(30), token);
        if (exit != 0 || File.Exists(marker))
            throw new InvalidOperationException($"Permission repair helper did not complete (exit {exit}).");
        return true;
    }
}
