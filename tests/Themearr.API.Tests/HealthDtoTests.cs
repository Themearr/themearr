using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class HealthDtoTests
{
    // No GITHUB_REPO/Themearr:GithubRepo configured, so GithubRepoResolver falls back
    // to the "Themearr/themearr" default — matches this suite's pre-fork-support
    // expectations everywhere a config isn't the point of the test.
    private static readonly IConfiguration DefaultConfig = new ConfigurationBuilder().Build();

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

        Assert.Equal("error", HealthDto.From(report, DefaultConfig).Status);
    }

    [Fact]
    public void Healthy_entries_are_omitted_from_the_list()
    {
        var report = Report(
            ("a", HealthStatus.Healthy,  "fine"),
            ("b", HealthStatus.Degraded, "meh"));

        var response = HealthDto.From(report, DefaultConfig);

        var item = Assert.Single(response.Checks);
        Assert.Equal("b", item.Source);
        Assert.Equal("warning", item.Type);
        Assert.Equal("meh", item.Message);
    }

    [Fact]
    public void All_healthy_yields_ok_and_an_empty_list()
    {
        var response = HealthDto.From(Report(("a", HealthStatus.Healthy, "fine")), DefaultConfig);

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Checks);
    }

    [Fact]
    public void Known_sources_carry_a_wiki_link_and_unknown_ones_do_not()
    {
        var report = Report(
            ("libraryPaths", HealthStatus.Unhealthy, "bad path"),
            ("autoDownload", HealthStatus.Unhealthy, "stalled"));

        var response = HealthDto.From(report, DefaultConfig);

        var paths = response.Checks.Single(c => c.Source == "libraryPaths");
        Assert.NotNull(paths.WikiUrl);
        Assert.Contains("library-paths", paths.WikiUrl);

        Assert.Null(response.Checks.Single(c => c.Source == "autoDownload").WikiUrl);
    }

    [Fact]
    public void Wiki_links_point_at_a_configured_forks_repo_not_the_upstream_one()
    {
        // A fork's health links must not 404 against the upstream repo — that link is
        // the entire point of the check. Reuses UpdateService's resolution order:
        // GITHUB_REPO env var, then Themearr:GithubRepo config, then the default.
        var forkConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Themearr:GithubRepo"] = "someuser/their-fork",
            })
            .Build();

        var report = Report(("libraryPaths", HealthStatus.Unhealthy, "bad path"));
        var response = HealthDto.From(report, forkConfig);

        var wikiUrl = Assert.Single(response.Checks).WikiUrl;
        Assert.NotNull(wikiUrl);
        Assert.StartsWith("https://github.com/someuser/their-fork#", wikiUrl);
    }

    [Fact]
    public void A_check_with_no_description_still_produces_a_message()
    {
        var report = Report(("a", HealthStatus.Unhealthy, null!));

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(HealthDto.From(report, DefaultConfig).Checks).Message));
    }

    [Fact]
    public void An_uncaught_exception_never_leaks_its_message_to_the_ui()
    {
        // DefaultHealthCheckService sets Description = ex.Message when a check throws
        // an exception type it doesn't catch. A SqliteException/InvalidOperationException
        // message could contain paths or other internals — the mapper must substitute a
        // fixed message instead of forwarding it, for every current and future check.
        const string secret = "password=hunter2;Data Source=/opt/themearr/secret.db";
        var exception = new InvalidOperationException(secret);
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["broken"] = new(HealthStatus.Unhealthy, secret, TimeSpan.Zero, exception, data: null),
        };
        var report = new HealthReport(entries, TimeSpan.Zero);

        var message = Assert.Single(HealthDto.From(report, DefaultConfig).Checks).Message;

        Assert.DoesNotContain("hunter2", message);
        Assert.DoesNotContain(secret, message);
    }
}
