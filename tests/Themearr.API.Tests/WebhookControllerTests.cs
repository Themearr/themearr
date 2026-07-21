using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class WebhookControllerTests
{
    private static (WebhookController Controller, TaskRegistry Registry) New()
    {
        var registry = new TaskRegistry();
        registry.Register(AutoSyncService.SyncTaskId, "Sync Library", TimeSpan.FromHours(24));
        return (new WebhookController(registry, Microsoft.Extensions.Logging.Abstractions
            .NullLogger<WebhookController>.Instance), registry);
    }

    /// <summary>True when a sync is pending — the trigger channel holds one slot.</summary>
    private static bool SyncPending(TaskRegistry r) =>
        !r.Trigger(AutoSyncService.SyncTaskId);   // a second write fails only if one is queued

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void An_import_triggers_a_sync()
    {
        var (controller, registry) = New();

        var result = controller.Radarr(Body("""{"eventType":"Download"}"""));

        Assert.IsType<OkObjectResult>(result);
        Assert.True(SyncPending(registry));
    }

    [Fact]
    public void A_test_ping_succeeds_without_triggering_a_sync()
    {
        var (controller, registry) = New();

        var result = controller.Radarr(Body("""{"eventType":"Test"}"""));

        Assert.IsType<OkObjectResult>(result);
        // Nothing queued, so this write is the first and must succeed.
        Assert.True(registry.Trigger(AutoSyncService.SyncTaskId));
    }

    [Theory]
    [InlineData("Grab")]
    [InlineData("Rename")]
    [InlineData("MovieDelete")]
    [InlineData("Health")]
    public void Other_events_succeed_without_triggering_a_sync(string eventType)
    {
        var (controller, registry) = New();

        var result = controller.Radarr(Body($$"""{"eventType":"{{eventType}}"}"""));

        // Must be 200: a 400 makes Radarr report the connection as failing.
        Assert.IsType<OkObjectResult>(result);
        Assert.True(registry.Trigger(AutoSyncService.SyncTaskId));
    }

    [Fact]
    public void A_body_with_no_event_type_is_a_bad_request()
    {
        var (controller, registry) = New();

        var result = controller.Radarr(Body("""{"movie":{"title":"Heat"}}"""));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(registry.Trigger(AutoSyncService.SyncTaskId));
    }
}
