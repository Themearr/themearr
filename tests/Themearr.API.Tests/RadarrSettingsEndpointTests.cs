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

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static (SettingsController Controller, Database Db) New(TempDir dir, HttpMessageHandler handler)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        var radarr = new RadarrLibrarySource(db, new LocalFolderResolver(db), new StubFactory(handler));
        // These tests exercise Radarr endpoints only — Plex is never probed here, so the
        // same handler (unused for Plex) is fine to wire through.
        var plexService = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));
        var plex = new PlexLibrarySource(plexService, db, new StubFactory(handler));
        return (new SettingsController(db, radarr, plex, plexService, new ApiKeyStore(db)), db);
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
    public async Task TestRadarr_with_a_blank_key_falls_back_to_the_stored_key_when_the_url_matches()
    {
        // A blank key with the URL that key belongs to is the normal "test what I already
        // saved" flow (e.g. re-testing from the settings page after a reload) — this must
        // keep working.
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("""{"version":"5.0.0"}"""));
        var (controller, db) = New(dir, handler);

        db.SetSetting("radarr_url", "http://stored.local:7878");
        db.SetSetting("radarr_api_key", "stored-key");

        var result = await controller.TestRadarr(
            new SettingsController.RadarrPayload("radarr", "http://stored.local:7878", ""),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var okValue = (bool)ok.Value!.GetType().GetProperty("ok")!.GetValue(ok.Value)!;
        Assert.True(okValue);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("stored-key", Assert.Single(values!));
    }

    [Fact]
    public async Task TestRadarr_with_a_blank_key_and_a_mismatched_url_does_not_send_the_stored_key()
    {
        // The vulnerability this guards against: an authenticated caller submits a blank
        // key and a URL of their choosing. Without the fix, TestRadarr would fall back to
        // the real stored key and ship it — in the X-Api-Key header — to that arbitrary
        // host. The browser can't read the secret back, but it can make the server spend it.
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("""{"version":"5.0.0"}"""));
        var (controller, db) = New(dir, handler);

        db.SetSetting("radarr_url", "http://stored.local:7878");
        db.SetSetting("radarr_api_key", "stored-key");

        var result = await controller.TestRadarr(
            new SettingsController.RadarrPayload("radarr", "http://attacker.example:1234", ""),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var okValue = (bool)ok.Value!.GetType().GetProperty("ok")!.GetValue(ok.Value)!;
        Assert.False(okValue);

        // No request was ever made to the attacker-supplied host, so the stored key never
        // left the server.
        Assert.Null(handler.LastRequest);
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

    [Fact]
    public void SaveRadarr_with_a_blank_key_falls_back_to_the_stored_key_when_the_url_is_unchanged()
    {
        // The normal "re-save without touching the key field" flow — the UI never gets
        // the real key back, so resubmitting the same URL with a blank key must keep
        // working exactly as before.
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("[]"));
        var (controller, db) = New(dir, handler);

        db.SetSetting("radarr_url", "http://stored.local:7878");
        db.SetSetting("radarr_api_key", "stored-key");

        var result = Assert.IsType<OkObjectResult>(controller.SaveRadarr(
            new SettingsController.RadarrPayload("radarr", "http://stored.local:7878", "")));

        var configured = (bool)result.Value!.GetType().GetProperty("configured")!.GetValue(result.Value)!;
        Assert.True(configured);
        Assert.Equal("http://stored.local:7878", db.GetSetting("radarr_url", ""));
        Assert.Equal("stored-key", db.GetSetting("radarr_api_key", ""));
    }

    [Fact]
    public void SaveRadarr_with_a_blank_key_and_a_changed_url_is_rejected_and_leaves_stored_config_untouched()
    {
        // The vulnerability this guards against: an authenticated caller (this endpoint
        // accepts the API key credential too) posts a new URL of their choosing with a
        // blank apiKey. Without the fix, SaveRadarr would keep the real stored key and
        // pair it with the attacker's URL — the next health poll or sync would then ship
        // the real key, in an X-Api-Key header, to that host.
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("[]"));
        var (controller, db) = New(dir, handler);

        db.SetSetting("radarr_url", "http://stored.local:7878");
        db.SetSetting("radarr_api_key", "stored-key");

        var result = Assert.IsType<BadRequestObjectResult>(controller.SaveRadarr(
            new SettingsController.RadarrPayload("radarr", "http://attacker.example:1234", "")));

        var detail = (string)result.Value!.GetType().GetProperty("detail")!.GetValue(result.Value)!;
        Assert.Equal("Enter the API key for the new Radarr server.", detail);

        // Neither the stored URL nor the stored key moved — the attacker's URL was never
        // saved, and the real key never got paired with it.
        Assert.Equal("http://stored.local:7878", db.GetSetting("radarr_url", ""));
        Assert.Equal("stored-key", db.GetSetting("radarr_api_key", ""));
    }

    [Fact]
    public void SaveRadarr_with_a_changed_url_and_a_supplied_key_saves_both()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("[]"));
        var (controller, db) = New(dir, handler);

        db.SetSetting("radarr_url", "http://stored.local:7878");
        db.SetSetting("radarr_api_key", "stored-key");

        var result = Assert.IsType<OkObjectResult>(controller.SaveRadarr(
            new SettingsController.RadarrPayload("radarr", "http://new-host.local:9999", "new-key")));

        var configured = (bool)result.Value!.GetType().GetProperty("configured")!.GetValue(result.Value)!;
        Assert.True(configured);
        Assert.Equal("http://new-host.local:9999", db.GetSetting("radarr_url", ""));
        Assert.Equal("new-key", db.GetSetting("radarr_api_key", ""));
    }

    [Fact]
    public void SaveRadarr_treats_a_trailing_slash_only_difference_as_unchanged_and_preserves_the_key()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("[]"));
        var (controller, db) = New(dir, handler);

        db.SetSetting("radarr_url", "http://stored.local:7878");
        db.SetSetting("radarr_api_key", "stored-key");

        var result = Assert.IsType<OkObjectResult>(controller.SaveRadarr(
            new SettingsController.RadarrPayload("radarr", "http://stored.local:7878/", "")));

        var configured = (bool)result.Value!.GetType().GetProperty("configured")!.GetValue(result.Value)!;
        Assert.True(configured);
        Assert.Equal("http://stored.local:7878", db.GetSetting("radarr_url", ""));
        Assert.Equal("stored-key", db.GetSetting("radarr_api_key", ""));
    }
}
