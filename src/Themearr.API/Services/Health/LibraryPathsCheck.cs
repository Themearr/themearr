using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Catches the misconfiguration that silently breaks every download: a library path
/// that is missing, read-only, or unreachable from the paths Plex reports.
/// </summary>
public sealed class LibraryPathsCheck(Database db) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Before setup there is nothing configured yet; a fresh install is not broken.
        if (!db.IsSetupComplete())
            return Task.FromResult(HealthCheckResult.Healthy("Setup not complete"));

        var paths = db.GetLibraryPaths();
        if (paths.Count == 0)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No library paths are configured — Themearr has nowhere to write theme.mp3. " +
                "Add one under Settings → Local Library Paths."));

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Library path {path} does not exist. Check the mount is present inside Themearr."));

            if (!ThemeFiles.IsDirectoryWritable(path))
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Library path {path} is not writable — every download will fail silently. " +
                    "Check the mount is not read-only and that the themearr user can write to it."));
        }

        var warnings = new List<string>();
        foreach (var (prefix, media) in new[] { ("last_sync", "movies"), ("last_show_sync", "shows") })
        {
            var unresolved = int.TryParse(db.GetSetting($"{prefix}_unresolved_count", "0"), out var n) ? n : 0;
            if (unresolved <= 0) continue;
            var sample = db.GetSetting($"{prefix}_unresolved_sample", "");
            var message = $"{unresolved} {media} could not be resolved to a local path — check Path Mappings.";
            if (!string.IsNullOrEmpty(sample)) message += $" Example: {sample}";
            warnings.Add(message);
        }
        if (warnings.Count > 0)
            return Task.FromResult(HealthCheckResult.Degraded(string.Join(" ", warnings)));

        return Task.FromResult(HealthCheckResult.Healthy(
            $"{paths.Count} library path(s) present and writable"));
    }
}
