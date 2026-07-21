using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ApiKeyEndpointTests
{
    private static (SettingsController Controller, IApiKeyStore Keys) New(
        TempDir dir, string authScheme = ApiAuthMiddleware.BearerScheme)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var keys = new ApiKeyStore(db);
        // Read SettingsController's constructor and supply whatever else it needs;
        // only the key store matters for these tests.
        var controller = TestControllers.NewSettingsController(db, keys);

        // Stand in for what ApiAuthMiddleware does on a real request: record which
        // credential authenticated the call, since the controller reads that marker
        // off HttpContext.Items to decide whether to allow apikey management.
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiAuthMiddleware.AuthSchemeItemKey] = authScheme;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        return (controller, keys);
    }

    [Fact]
    public void Get_returns_the_current_key()
    {
        using var dir = new TempDir();
        var (controller, keys) = New(dir);

        var result = Assert.IsType<OkObjectResult>(controller.GetApiKey());

        // Exact shape, not just "contains the key" — this must fail if the endpoint
        // ever starts returning another secret alongside it.
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(["key"], body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(keys.Current, body.GetProperty("key").GetString());

        Assert.Equal("no-store", controller.HttpContext.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void Regenerate_returns_a_different_key_and_the_old_one_stops_being_current()
    {
        using var dir = new TempDir();
        var (controller, keys) = New(dir);
        var before = keys.Current;

        var result = Assert.IsType<OkObjectResult>(controller.RegenerateApiKey());
        var body = System.Text.Json.JsonSerializer.Serialize(result.Value);

        Assert.DoesNotContain(before, body);
        Assert.Contains(keys.Current, body);
        Assert.NotEqual(before, keys.Current);
    }

    // ── a credential must not be able to manage credentials ─────────────────

    [Fact]
    public void Get_is_refused_when_authenticated_with_the_api_key()
    {
        using var dir = new TempDir();
        var (controller, _) = New(dir, ApiAuthMiddleware.ApiKeyScheme);

        var result = Assert.IsType<ObjectResult>(controller.GetApiKey());

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public void Regenerate_is_refused_when_authenticated_with_the_api_key()
    {
        using var dir = new TempDir();
        var (controller, keys) = New(dir, ApiAuthMiddleware.ApiKeyScheme);
        var before = keys.Current;

        var result = Assert.IsType<ObjectResult>(controller.RegenerateApiKey());

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        // Refused before it could touch the store — the key must not have rotated.
        Assert.Equal(before, keys.Current);
    }

    [Fact]
    public void Get_succeeds_when_authenticated_with_the_bearer_token()
    {
        using var dir = new TempDir();
        var (controller, keys) = New(dir, ApiAuthMiddleware.BearerScheme);

        var result = Assert.IsType<OkObjectResult>(controller.GetApiKey());

        Assert.Contains(keys.Current, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public void Regenerate_succeeds_when_authenticated_with_the_bearer_token()
    {
        using var dir = new TempDir();
        var (controller, keys) = New(dir, ApiAuthMiddleware.BearerScheme);
        var before = keys.Current;

        var result = Assert.IsType<OkObjectResult>(controller.RegenerateApiKey());

        Assert.NotEqual(before, keys.Current);
        Assert.Contains(keys.Current, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }
}
