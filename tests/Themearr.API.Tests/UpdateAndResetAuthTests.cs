using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// The two most privileged operations — triggering a root host update and factory-resetting
/// app state — must require the master bearer token, never the externally-held API key that
/// lives in Radarr's config. A compromised Radarr must not be able to reach them.
/// </summary>
public class UpdateAndResetAuthTests
{
    private static T WithScheme<T>(T controller, string scheme) where T : ControllerBase
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[ApiAuthMiddleware.AuthSchemeItemKey] = scheme;
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }

    private static SetupController NewSetupController(Database db) =>
        new(db, new PlexService(new HttpClient(), db, new LocalFolderResolver(db)));

    // ── POST /api/update ────────────────────────────────────────────────────

    [Fact]
    public async Task Update_is_refused_and_does_not_start_when_authenticated_with_the_api_key()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var update = new UpdateService(db, new ConfigurationBuilder().Build());
        var controller = WithScheme(new VersionController(update), ApiAuthMiddleware.ApiKeyScheme);

        var result = Assert.IsType<ObjectResult>(await controller.StartUpdate());

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        // Refused before it could kick off the updater.
        var status = System.Text.Json.JsonSerializer.SerializeToElement(update.GetStatus());
        Assert.False(status.GetProperty("inProgress").GetBoolean());
    }

    // ── POST /api/setup/reset ───────────────────────────────────────────────

    [Fact]
    public void Reset_is_refused_and_leaves_state_intact_when_authenticated_with_the_api_key()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();

        var controller = WithScheme(NewSetupController(db), ApiAuthMiddleware.ApiKeyScheme);

        var result = Assert.IsType<ObjectResult>(controller.Reset());

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        // The refusal must happen before ResetAppState runs — state survives.
        Assert.True(db.IsSetupComplete());
    }

    [Fact]
    public void Reset_succeeds_and_clears_state_when_authenticated_with_the_bearer_token()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();

        var controller = WithScheme(NewSetupController(db), ApiAuthMiddleware.BearerScheme);

        Assert.IsType<OkObjectResult>(controller.Reset());
        Assert.False(db.IsSetupComplete());
    }
}
