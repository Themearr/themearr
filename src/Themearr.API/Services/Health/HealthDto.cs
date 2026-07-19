using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Themearr.API.Services.Health;

/// <summary>One health problem, shaped like Radarr's health API so the UI feels familiar.</summary>
public sealed record HealthItem(string Source, string Type, string Message, string? WikiUrl);

/// <summary>Overall status plus every non-healthy check.</summary>
public sealed record HealthResponse(string Status, IReadOnlyList<HealthItem> Checks);

public static class HealthDto
{
    // The README already documents the fix for these; a health message that links
    // straight to it is the support reply we would otherwise write by hand. Anchors
    // only — the repo itself is resolved per-call via GithubRepoResolver (the same
    // env var → config → default order UpdateService uses) so a fork's health links
    // point at the fork's own README instead of 404ing against the upstream repo.
    private static readonly Dictionary<string, string> WikiAnchors = new(StringComparer.Ordinal)
    {
        ["libraryPaths"] = "library-paths--path-mappings",
        ["rapidapi"]     = "downloads-require-a-rapidapi-key",
    };

    public static string? WikiUrlFor(string source, IConfiguration config) =>
        WikiAnchors.TryGetValue(source, out var anchor)
            ? $"https://github.com/{GithubRepoResolver.Resolve(config)}#{anchor}"
            : null;

    public static string MapType(HealthStatus status) => status switch
    {
        HealthStatus.Healthy  => "ok",
        HealthStatus.Degraded => "warning",
        _                     => "error",
    };

    // Shown instead of HealthReportEntry.Description whenever a check threw an
    // exception it did not catch. ASP.NET Core's DefaultHealthCheckService sets
    // Description = ex.Message in that case, and that message (a SqliteException,
    // an InvalidOperationException from some deep dependency, etc.) can contain
    // paths, connection details, or other internals that must never reach the
    // browser. This is the one place the invariant can be enforced for every
    // current and future check, since individual checks can throw types we don't
    // control.
    private const string UnexpectedFailureMessage =
        "This check failed unexpectedly — see the application log.";

    /// <summary>
    /// Only non-healthy entries are listed, matching arr behaviour: the health page
    /// is a problem list, not an inventory. Overall status is already the worst child.
    /// </summary>
    public static HealthResponse From(HealthReport report, IConfiguration config)
    {
        var checks = report.Entries
            .Where(e => e.Value.Status != HealthStatus.Healthy)
            .Select(e => new HealthItem(
                e.Key,
                MapType(e.Value.Status),
                MessageFor(e.Value),
                WikiUrlFor(e.Key, config)))
            .OrderBy(c => c.Source, StringComparer.Ordinal)
            .ToList();

        return new HealthResponse(MapType(report.Status), checks);
    }

    private static string MessageFor(HealthReportEntry entry)
    {
        if (entry.Exception is not null) return UnexpectedFailureMessage;
        return string.IsNullOrWhiteSpace(entry.Description) ? "Check failed" : entry.Description;
    }
}
