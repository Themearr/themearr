using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PlexServerUrlEndpointTests
{
    private const string Token = "tok-123";

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }

    private static Database NewDb(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetPlexServers([new Dictionary<string, object?>
        {
            ["id"] = "srv1", ["name"] = "Tower",
            ["url"] = "https://old.plex.direct:32400",
            ["urls"] = new List<string> { "https://old.plex.direct:32400" },
            ["token"] = Token,
        }]);
        return db;
    }

    private static SettingsController Controller(Database db, string scheme, HttpMessageHandler? plex = null)
    {
        var factory = plex is null ? (IHttpClientFactory?)null : new StubFactory(plex);
        var controller = factory is null
            ? TestControllers.NewSettingsController(db, new ApiKeyStore(db))
            : TestControllers.NewSettingsController(db, new ApiKeyStore(db), factory);
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiAuthMiddleware.AuthSchemeItemKey] = scheme;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }

    // ── save ───────────────────────────────────────────────────────────────
    [Fact]
    public void SavePlexUrl_updates_the_url_and_keeps_the_token()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var controller = Controller(db, ApiAuthMiddleware.BearerScheme);

        var result = controller.SavePlexUrl(new SettingsController.PlexUrlPayload("srv1", "192.168.1.50:32400"));

        Assert.IsType<OkObjectResult>(result);
        var srv = db.GetPlexServersDict()["srv1"];
        Assert.Equal("http://192.168.1.50:32400", srv.Url);   // scheme defaulted to http
        Assert.Equal(Token, srv.Token);
    }

    [Fact]
    public void SavePlexUrl_returns_404_for_an_unknown_server()
    {
        using var dir = new TempDir();
        var result = Controller(NewDb(dir), ApiAuthMiddleware.BearerScheme)
            .SavePlexUrl(new SettingsController.PlexUrlPayload("nope", "http://192.168.1.50:32400"));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<NotFoundObjectResult>(result).StatusCode);
    }

    [Fact]
    public void SavePlexUrl_returns_400_for_a_blank_url()
    {
        using var dir = new TempDir();
        var result = Controller(NewDb(dir), ApiAuthMiddleware.BearerScheme)
            .SavePlexUrl(new SettingsController.PlexUrlPayload("srv1", "   "));
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(result).StatusCode);
    }

    [Fact]
    public void SavePlexUrl_is_refused_403_under_the_api_key_and_does_not_change_the_url()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var result = Controller(db, ApiAuthMiddleware.ApiKeyScheme)
            .SavePlexUrl(new SettingsController.PlexUrlPayload("srv1", "http://attacker.example:32400"));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal("https://old.plex.direct:32400", db.GetPlexServersDict()["srv1"].Url);
    }

    // ── test ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task TestPlex_returns_ok_when_the_supplied_url_is_reachable()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var controller = Controller(NewDb(dir), ApiAuthMiddleware.BearerScheme, handler);

        var result = Assert.IsType<OkObjectResult>(
            await controller.TestPlex(new SettingsController.PlexUrlPayload("srv1", "http://192.168.1.50:32400"), CancellationToken.None));

        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        Assert.True(body.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TestPlex_reports_not_ok_on_401_without_leaking_the_token()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var controller = Controller(NewDb(dir), ApiAuthMiddleware.BearerScheme, handler);

        var result = Assert.IsType<OkObjectResult>(
            await controller.TestPlex(new SettingsController.PlexUrlPayload("srv1", "http://192.168.1.50:32400"), CancellationToken.None));

        var body = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"ok\":false", body);
        Assert.DoesNotContain(Token, body);
    }

    [Fact]
    public async Task TestPlex_is_refused_403_under_the_api_key()
    {
        using var dir = new TempDir();
        var result = await Controller(NewDb(dir), ApiAuthMiddleware.ApiKeyScheme)
            .TestPlex(new SettingsController.PlexUrlPayload("srv1", "http://attacker.example:32400"), CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
