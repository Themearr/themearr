using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Health;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class AuditCredentialDataRegressionTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Keys : IApiKeyStore
    {
        public string Current => "test-key";
        public string Regenerate() => "test-key";
    }
    private sealed class CaptureLogger : ILogger<ShowSyncService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
    }
    private static Database Db(TempDir dir)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetSetting("plex_access_token", "account-secret");
        db.SetSetting("plex_client_identifier", "client");
        return db;
    }
    private static Dictionary<string, object?> Server(string token = "server-secret", string id = "s1") => new()
    {
        ["id"] = id, ["name"] = "Home", ["url"] = "http://plex.local:32400",
        ["urls"] = new List<string> { "http://plex.local:32400" }, ["token"] = token,
    };
    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/xml"),
    };
    private const string Libraries = """<MediaContainer><Directory key="1" type="movie" title="Movies"/></MediaContainer>""";
    private const string Discovery = """
        <MediaContainer><Device clientIdentifier="s1" provides="server" name="Home" accessToken="server-secret" owned="1" presence="1">
        <Connection uri="http://plex.local:32400" local="1" protocol="http"/>
        </Device></MediaContainer>
        """;
    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    private static List<Dictionary<string, object?>> Servers(JsonElement body) =>
        body.GetProperty("servers").Deserialize<List<Dictionary<string, object?>>>()!;

    [Fact]
    public async Task Discovery_is_redacted_and_does_not_replace_the_selected_servers()
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.SetPlexServers([Server("old-token", "old-server")]);
        var controller = new SetupController(db, new PlexService(new HttpClient(new Handler(_ => Xml(Discovery))), db, new LocalFolderResolver(db)));

        var response = Body(await controller.PlexServers());

        Assert.Equal("", response.GetProperty("servers")[0].GetProperty("token").GetString());
        Assert.DoesNotContain("server-secret", response.GetRawText());
        Assert.Equal("old-server", Assert.Single(db.GetPlexServers())["id"]?.ToString());
    }

    [Fact]
    public async Task New_setup_can_list_and_save_redacted_discovery_after_controller_recreation()
    {
        using var dir = new TempDir();
        var db = Db(dir);
        var requests = new List<HttpRequestMessage>();
        var handler = new Handler(r =>
        {
            requests.Add(r);
            return Xml(r.RequestUri!.Host == "plex.tv" ? Discovery : Libraries);
        });
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var discovered = Servers(Body(await new SetupController(db, plex).PlexServers()));
        foreach (var server in discovered) server["token"] = "";
        // Redacted responses also survive an API restart, without a browser-held secret.
        var reopened = new Database(Path.Combine(dir.Path, "test.db"));
        var controller = new SetupController(reopened, new PlexService(new HttpClient(handler), reopened, new LocalFolderResolver(reopened)));

        var libraries = Body(await controller.PlexLibraries(new() { Servers = discovered }));
        Assert.True(libraries.GetProperty("libraries").TryGetProperty("s1", out _));
        Body(controller.SaveSelection(new() { Servers = discovered, SelectedLibraries = new() { ["s1"] = ["1"] } }));

        Assert.Equal("server-secret", reopened.GetPlexServersDict()["s1"].Token);
        Assert.Contains(requests, r => r.RequestUri!.Host == "plex.local" && r.Headers.GetValues("X-Plex-Token").Single() == "server-secret");
        Assert.DoesNotContain("server-secret", Body(controller.Status()).GetRawText());
    }

    [Theory]
    [InlineData("")]
    [InlineData("legacy-explicit-token")]
    public async Task Existing_selected_servers_can_list_and_save_with_legacy_or_redacted_tokens(string token)
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.SetPlexServers([Server()]);
        var expected = token.Length == 0 ? "server-secret" : token;
        var seen = new List<string>();
        var handler = new Handler(r => { seen.Add(r.Headers.GetValues("X-Plex-Token").Single()); return Xml(Libraries); });
        var controller = new SetupController(db, new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db)));
        var incoming = Server(token);

        var libraries = Body(await controller.PlexLibraries(new() { Servers = [incoming] }));
        Assert.True(libraries.GetProperty("libraries").TryGetProperty("s1", out _));
        Body(controller.SaveSelection(new() { Servers = [incoming], SelectedLibraries = new() { ["s1"] = ["1"] } }));

        Assert.Equal(expected, db.GetPlexServersDict()["s1"].Token);
        Assert.Equal(expected, Assert.Single(seen));
    }

    [Fact]
    public async Task Blank_token_library_request_never_tries_posted_alternate_destinations()
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.SetPlexServers([Server()]);
        var calls = new List<string>();
        var handler = new Handler(r => { calls.Add(r.RequestUri!.Host); return new(HttpStatusCode.ServiceUnavailable); });
        var controller = new SetupController(db, new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db)));
        var incoming = Server("");
        incoming["urls"] = JsonSerializer.SerializeToElement(new[] { "http://attacker.example" });

        await controller.PlexLibraries(new() { Servers = [incoming] });

        Assert.NotEmpty(calls);
        Assert.All(calls, host => Assert.Equal("plex.local", host));
    }

    [Fact]
    public async Task Redacted_selection_roundtrip_cannot_attach_the_stored_token_to_new_alternate_urls()
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.SetPlexServers([Server()]);
        var calls = new List<string>();
        var handler = new Handler(r => { calls.Add(r.RequestUri!.Host); return new(HttpStatusCode.ServiceUnavailable); });
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var controller = new SetupController(db, plex);
        var incoming = db.GetPlexServersRedacted();
        incoming[0]["urls"] = new List<string> { "http://attacker.example" };
        Body(controller.SaveSelection(new() { Servers = incoming, SelectedLibraries = new() { ["s1"] = ["1"] } }));

        await Assert.ThrowsAnyAsync<Exception>(() => plex.FetchMoviesAsync());

        Assert.NotEmpty(calls);
        Assert.All(calls, host => Assert.Equal("plex.local", host));
        Assert.Equal("server-secret", db.GetPlexServersDict()["s1"].Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_settings_roundtrip_preserves_credentials_configuration_and_trusted_destinations(bool forgedAlternate)
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.SetPlexServers([Server()]);
        db.SetSelectedLibraries(new() { ["s1"] = ["1"] });
        db.SetSelectedShowLibraries(new() { ["s1"] = ["3"] });
        db.SetPathMappings([new() { ["source"] = "/remote", ["target"] = dir.Path }]);
        db.SetLibraryPaths([dir.Path]);
        db.SetSetting("operator_custom", "preserved");
        var calls = new List<string>();
        var handler = new Handler(r => { calls.Add(r.RequestUri!.Host); return new(HttpStatusCode.ServiceUnavailable); });
        var factory = new Factory(handler);
        var plex = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        var controller = new SettingsController(db, new RadarrLibrarySource(db, new LocalFolderResolver(db), factory),
            new PlexLibrarySource(plex, db, factory), plex, new Keys());
        var previous = Body(controller.Get());
        var payload = previous.Deserialize<SettingsPayload>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        payload.SelectedShowLibraries = null; // a cached bundle predating show settings
        if (forgedAlternate) payload.SelectedServers[0]["urls"] = new List<string> { "http://attacker.example" };

        var saved = Body(controller.Save(payload));
        await controller.PlexLibraries();

        Assert.Equal("server-secret", db.GetPlexServersDict()["s1"].Token);
        Assert.All(calls, host => Assert.Equal("plex.local", host));
        Assert.NotEmpty(calls);
        Assert.Equal("", saved.GetProperty("selectedServers")[0].GetProperty("token").GetString());
        Assert.Equal("3", Assert.Single(db.GetSelectedShowLibraries()["s1"]));
        Assert.Equal(dir.Path, Assert.Single(db.GetPathMappings())["target"]);
        Assert.Equal(dir.Path, Assert.Single(db.GetLibraryPaths()));
        Assert.Equal("preserved", db.GetSetting("operator_custom"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_server_with_no_selected_movie_libraries_is_not_contacted(bool explicitEmpty)
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.SetPlexServers([Server(id: "selected"), Server(id: "deselected")]);
        var selected = new Dictionary<string, List<string>> { ["selected"] = ["1"] };
        if (explicitEmpty) selected["deselected"] = [];
        db.SetSelectedLibraries(selected);
        var calls = 0;
        var plex = new PlexService(new HttpClient(new Handler(r =>
        {
            calls++;
            return Xml(r.RequestUri!.AbsolutePath == "/library/sections" ? Libraries : "<MediaContainer size=\"0\"/>" );
        })), db, new LocalFolderResolver(db));

        await plex.FetchMoviesAsync();

        Assert.Equal(2, calls); // sections and the selected library, for one server only
    }

    [Theory]
    [InlineData("{\"hasFile\":\"yes\"}")]
    [InlineData("{\"hasFile\":true}")]
    [InlineData("{\"id\":2,\"path\":\"/missing\"}")]
    [InlineData("null")]
    public async Task Malformed_radarr_rows_do_not_prune_previously_synced_movies(string malformed)
    {
        using var dir = new TempDir();
        var db = Db(dir);
        var current = Path.Combine(dir.Path, "Current");
        var retained = Path.Combine(dir.Path, "Retained");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(retained);
        db.UpsertMovies([new MovieRecord(retained, "radarr", "2", "Retained", 2000, retained)]);
        db.SetSetting("library_source", "radarr");
        db.SetSetting("radarr_url", "http://radarr.local");
        db.SetSetting("radarr_api_key", "key");
        var good = JsonSerializer.Serialize(new { id = 1, title = "Current", year = 2001, hasFile = true, path = current });
        var source = new RadarrLibrarySource(db, new LocalFolderResolver(db), new Factory(new Handler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent($"[{good},{malformed}]") })));
        var sync = new SyncService(db, new LibrarySourceResolver(db, [source]), NullLogger<SyncService>.Instance);

        Assert.True(await sync.StartAsync());
        await sync.Current.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(db.GetAllMovies(), m => m["title"]?.ToString() == "Retained");
        Assert.Contains(db.GetAllMovies(), m => m["title"]?.ToString() == "Current");
        Assert.Equal("1", db.GetSetting("last_sync_unresolved_count"));
    }

    [Fact]
    public async Task Unresolved_show_paths_degrade_health_alongside_movie_failures()
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.MarkSetupComplete();
        db.SetLibraryPaths([dir.Path]);
        db.SetSetting("last_sync_unresolved_count", "3");
        db.SetSetting("last_sync_unresolved_sample", "movie-example");
        db.SetSetting("last_show_sync_unresolved_count", "2");
        db.SetSetting("last_show_sync_unresolved_sample", "show-example");

        var result = await new LibraryPathsCheck(db).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("3 movies", result.Description);
        Assert.Contains("2 shows", result.Description);
        Assert.Contains("show-example", result.Description);
    }

    [Fact]
    public async Task Show_only_path_failures_degrade_health()
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.MarkSetupComplete();
        db.SetLibraryPaths([dir.Path]);
        db.SetSetting("last_show_sync_unresolved_count", "2");
        db.SetSetting("last_show_sync_unresolved_sample", "show-example");
        var result = await new LibraryPathsCheck(db).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("2 shows", result.Description);
    }

    [Fact]
    public async Task Show_sync_sanitizes_user_controlled_callback_log_messages()
    {
        using var dir = new TempDir();
        var db = Db(dir);
        db.SetPlexServers([Server()]);
        db.SetSelectedShowLibraries(new() { ["s1"] = ["3"] });
        var handler = new Handler(r => Xml(r.RequestUri!.AbsolutePath == "/library/sections"
            ? """<MediaContainer><Directory key="3" type="show" title="TV"/></MediaContainer>"""
            : """<MediaContainer size="1" totalSize="1"><Directory ratingKey="1" type="show" title="Safe&#13;&#10;FORGED"><Location path="/unmounted"/></Directory></MediaContainer>"""));
        var logger = new CaptureLogger();
        var sync = new ShowSyncService(db, new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db)), logger);

        await sync.RunOnceAsync(CancellationToken.None);

        Assert.Contains(logger.Messages, m => m.Contains("FORGED"));
        Assert.All(logger.Messages, m => { Assert.DoesNotContain("\r", m); Assert.DoesNotContain("\n", m); });
    }
}
