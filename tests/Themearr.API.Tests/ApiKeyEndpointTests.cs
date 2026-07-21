using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ApiKeyEndpointTests
{
    private static (SettingsController Controller, IApiKeyStore Keys) New(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var keys = new ApiKeyStore(db);
        // Read SettingsController's constructor and supply whatever else it needs;
        // only the key store matters for these tests.
        return (TestControllers.NewSettingsController(db, keys), keys);
    }

    [Fact]
    public void Get_returns_the_current_key()
    {
        using var dir = new TempDir();
        var (controller, keys) = New(dir);

        var result = Assert.IsType<OkObjectResult>(controller.GetApiKey());

        Assert.Contains(keys.Current, System.Text.Json.JsonSerializer.Serialize(result.Value));
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
}
