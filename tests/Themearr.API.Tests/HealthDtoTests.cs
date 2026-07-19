using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class HealthDtoTests
{
    private static HealthReport Report(params (string Key, HealthStatus Status, string Desc)[] entries)
    {
        var dict = entries.ToDictionary(
            e => e.Key,
            e => new HealthReportEntry(e.Status, e.Desc, TimeSpan.Zero, exception: null, data: null));
        return new HealthReport(dict, TimeSpan.Zero);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy,   "ok")]
    [InlineData(HealthStatus.Degraded,  "warning")]
    [InlineData(HealthStatus.Unhealthy, "error")]
    public void MapType_maps_each_status_to_the_arr_type(HealthStatus status, string expected)
    {
        Assert.Equal(expected, HealthDto.MapType(status));
    }

    [Fact]
    public void Overall_status_is_the_worst_child()
    {
        var report = Report(
            ("a", HealthStatus.Healthy,  "fine"),
            ("b", HealthStatus.Degraded, "meh"),
            ("c", HealthStatus.Unhealthy, "broken"));

        Assert.Equal("error", HealthDto.From(report).Status);
    }

    [Fact]
    public void Healthy_entries_are_omitted_from_the_list()
    {
        var report = Report(
            ("a", HealthStatus.Healthy,  "fine"),
            ("b", HealthStatus.Degraded, "meh"));

        var response = HealthDto.From(report);

        var item = Assert.Single(response.Checks);
        Assert.Equal("b", item.Source);
        Assert.Equal("warning", item.Type);
        Assert.Equal("meh", item.Message);
    }

    [Fact]
    public void All_healthy_yields_ok_and_an_empty_list()
    {
        var response = HealthDto.From(Report(("a", HealthStatus.Healthy, "fine")));

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Checks);
    }

    [Fact]
    public void Known_sources_carry_a_wiki_link_and_unknown_ones_do_not()
    {
        var report = Report(
            ("libraryPaths", HealthStatus.Unhealthy, "bad path"),
            ("autoDownload", HealthStatus.Unhealthy, "stalled"));

        var response = HealthDto.From(report);

        var paths = response.Checks.Single(c => c.Source == "libraryPaths");
        Assert.NotNull(paths.WikiUrl);
        Assert.Contains("library-paths", paths.WikiUrl);

        Assert.Null(response.Checks.Single(c => c.Source == "autoDownload").WikiUrl);
    }

    [Fact]
    public void A_check_with_no_description_still_produces_a_message()
    {
        var report = Report(("a", HealthStatus.Unhealthy, null!));

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(HealthDto.From(report).Checks).Message));
    }
}
