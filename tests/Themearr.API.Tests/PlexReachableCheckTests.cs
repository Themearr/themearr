using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;
using Themearr.API.Services.Health;

namespace Themearr.API.Tests;

public class PlexReachableCheckTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Database NewDb(TempDir dir, bool withServer)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();
        if (withServer)
        {
            db.SetPlexServers([new Dictionary<string, object?>
            {
                ["id"]    = "srv1",
                ["name"]  = "Tower",
                ["url"]   = "http://plex.local:32400",
                ["token"] = "secret-token-value",
            }]);
        }
        return db;
    }

    private static Task<HealthCheckResult> Run(Database db, HttpMessageHandler handler) =>
        new PlexReachableCheck(db, new StubFactory(handler))
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task Before_setup_completes_it_is_healthy_even_with_a_server_configured()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"]    = "srv1",
            ["name"]  = "Tower",
            ["url"]   = "http://plex.local:32400",
            ["token"] = "secret-token-value",
        }]);
        // Deliberately no MarkSetupComplete(): a fresh install is not broken.

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Equal(HealthStatus.Healthy, (await Run(db, handler)).Status);
    }

    [Fact]
    public async Task No_configured_server_is_healthy()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(HealthStatus.Healthy, (await Run(NewDb(dir, withServer: false), handler)).Status);
    }

    [Fact]
    public async Task A_reachable_server_is_healthy()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(HealthStatus.Healthy, (await Run(NewDb(dir, withServer: true), handler)).Status);
    }

    [Fact]
    public async Task A_401_reports_a_rejected_token()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await Run(NewDb(dir, withServer: true), handler);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("401", result.Description);
    }

    [Fact]
    public async Task A_timeout_reports_no_response()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));

        var result = await Run(NewDb(dir, withServer: true), handler);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("did not respond", result.Description);
    }

    [Fact]
    public async Task A_connection_failure_reports_unreachable()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused to http://plex.local:32400"));

        var result = await Run(NewDb(dir, withServer: true), handler);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unreachable", result.Description);
    }

    [Fact]
    public async Task The_token_never_appears_in_any_message_or_in_the_request_url()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new HttpRequestException("boom secret-token-value"));

        var result = await Run(NewDb(dir, withServer: true), handler);

        Assert.DoesNotContain("secret-token-value", result.Description);
        Assert.DoesNotContain("secret-token-value", handler.LastRequest?.RequestUri?.ToString() ?? "");
    }
}
