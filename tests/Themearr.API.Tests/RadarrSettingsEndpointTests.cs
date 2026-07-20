using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// Exercises <see cref="SettingsController"/> itself (not <see cref="RadarrLibrarySource"/>
/// directly) so these tests would actually catch a regression reintroduced inside the
/// controller — e.g. TestRadarr writing the probed URL/key to settings, or SaveRadarr
/// overwriting stored Radarr config on an unrelated Plex save.
/// </summary>
public class RadarrSettingsEndpointTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static (SettingsController Controller, Database Db) New(TempDir dir, HttpMessageHandler handler)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var radarr = new RadarrLibrarySource(db, new LocalFolderResolver(db), new StubFactory(handler));
        return (new SettingsController(db, radarr), db);
    }

    [Fact]
    public async Task TestRadarr_probes_the_submitted_credentials_without_writing_them_to_settings()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("""{"version":"5.0.0"}"""));
        var (controller, db) = New(dir, handler);

        db.SetSetting("radarr_url", "http://stored.local:7878");
        db.SetSetting("radarr_api_key", "stored-key");

        var result = await controller.TestRadarr(
            new SettingsController.RadarrPayload("radarr", "http://typed-but-different:9999", "typed-different-key"),
            CancellationToken.None);

        // Proves the probe actually ran (rather than the test passing because nothing
        // happened): a genuine 200 from the stub means TestRadarr reported success.
        var ok = Assert.IsType<OkObjectResult>(result);
        var okValue = (bool)ok.Value!.GetType().GetProperty("ok")!.GetValue(ok.Value)!;
        Assert.True(okValue);

        // The whole point of the fix: submitting different credentials to /test must not
        // pair them with — or otherwise disturb — what's already stored.
        Assert.Equal("http://stored.local:7878", db.GetSetting("radarr_url", ""));
        Assert.Equal("stored-key", db.GetSetting("radarr_api_key", ""));
    }

    [Fact]
    public async Task SaveRadarr_for_plex_with_no_url_or_key_leaves_stored_radarr_config_intact()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("[]"));
        var (controller, db) = New(dir, handler);

        db.SetSetting("radarr_url", "http://stored.local:7878");
        db.SetSetting("radarr_api_key", "stored-key");

        var result = Assert.IsType<OkObjectResult>(controller.SaveRadarr(
            new SettingsController.RadarrPayload("plex", null, null)));

        var source = (string)result.Value!.GetType().GetProperty("source")!.GetValue(result.Value)!;
        Assert.Equal("plex", source);
        Assert.Equal("http://stored.local:7878", db.GetSetting("radarr_url", ""));
        Assert.Equal("stored-key", db.GetSetting("radarr_api_key", ""));
        Assert.Equal("plex", db.GetSetting("library_source", ""));
    }
}
