using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Themearr.API.Services.Health;

/// <summary>One health problem, shaped like Radarr's health API so the UI feels familiar.</summary>
public sealed record HealthItem(string Source, string Type, string Message, string? WikiUrl);

/// <summary>Overall status plus every non-healthy check.</summary>
public sealed record HealthResponse(string Status, IReadOnlyList<HealthItem> Checks);

public static class HealthDto
{
    private const string ReadmeBase = "https://github.com/Themearr/themearr#";

    // The README already documents the fix for these; a health message that links
    // straight to it is the support reply we would otherwise write by hand.
    private static readonly Dictionary<string, string> WikiAnchors = new(StringComparer.Ordinal)
    {
        ["libraryPaths"] = ReadmeBase + "library-paths--path-mappings",
        ["rapidapi"]     = ReadmeBase + "downloads-require-a-rapidapi-key",
    };

    public static string? WikiUrlFor(string source) => WikiAnchors.GetValueOrDefault(source);

    public static string MapType(HealthStatus status) => status switch
    {
        HealthStatus.Healthy  => "ok",
        HealthStatus.Degraded => "warning",
        _                     => "error",
    };

    /// <summary>
    /// Only non-healthy entries are listed, matching arr behaviour: the health page
    /// is a problem list, not an inventory. Overall status is already the worst child.
    /// </summary>
    public static HealthResponse From(HealthReport report)
    {
        var checks = report.Entries
            .Where(e => e.Value.Status != HealthStatus.Healthy)
            .Select(e => new HealthItem(
                e.Key,
                MapType(e.Value.Status),
                string.IsNullOrWhiteSpace(e.Value.Description) ? "Check failed" : e.Value.Description,
                WikiUrlFor(e.Key)))
            .OrderBy(c => c.Source, StringComparer.Ordinal)
            .ToList();

        return new HealthResponse(MapType(report.Status), checks);
    }
}
