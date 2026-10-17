using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class AudioProcessingOptionsTests
{
    private static SettingsController Controller(Database db) => new(db, null!, null!, null!, new ApiKeyStore(db));
    private static JsonElement Body(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    private static SettingsPayload Payload(string json) => JsonSerializer.Deserialize<SettingsPayload>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    private static Database NewDb(TempDir dir) { var db = new Database(dir.File("test.db")); db.Init(); return db; }
    [Fact]
    public void Existing_installs_default_to_disabled_without_trimming_or_normalization()
    {
        using var dir = new TempDir(); var db = NewDb(dir); var body = Body(Controller(db).Get());
        Assert.True(body.TryGetProperty("audioProcessing", out var options), "settings must expose opt-in processing");
        Assert.False(options.GetProperty("enabled").GetBoolean()); Assert.False(options.GetProperty("normalize").GetBoolean());
        Assert.Equal(JsonValueKind.Null, options.GetProperty("trimSeconds").ValueKind); Assert.Equal(192, options.GetProperty("bitrateKbps").GetInt32());
    }
    [Fact]
    public void Explicit_options_round_trip_and_old_client_omission_preserves_them()
    {
        using var dir = new TempDir(); var db = NewDb(dir); var controller = Controller(db);
        var saved = Body(controller.Save(Payload("""{"audioProcessing":{"enabled":true,"trimSeconds":30,"normalize":true,"bitrateKbps":256}}""")));
        Assert.True(saved.TryGetProperty("audioProcessing", out var options), "saved settings must expose processing");
        Assert.True(options.GetProperty("enabled").GetBoolean()); Assert.Equal(30, options.GetProperty("trimSeconds").GetInt32());
        Assert.Equal(options.GetRawText(), Body(controller.Save(new SettingsPayload())).GetProperty("audioProcessing").GetRawText());
    }
    [Theory]
    [InlineData("{\"trimSeconds\":4}")]
    [InlineData("{\"trimSeconds\":901}")]
    [InlineData("{\"bitrateKbps\":193}")]
    [InlineData("{\"enabled\":\"yes\"}")]
    [InlineData("[]")]
    public void Invalid_provided_options_are_rejected_before_other_settings_are_written(string options)
    {
        using var dir = new TempDir(); var db = NewDb(dir); db.SetSetting("auto_download", "true");
        Assert.IsType<BadRequestObjectResult>(Controller(db).Save(Payload("{\"audioProcessing\":" + options + "}")));
        Assert.Equal("true", db.GetSetting("auto_download"));
    }
}
