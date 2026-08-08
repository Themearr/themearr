using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// The Settings library pickers were empty for every install: Settings only ever holds
/// REDACTED servers (GetPlexServersRedacted blanks the token), and the setup endpoint it
/// posted them to skips any server without one — so it always returned nothing. It also
/// defaulted to movie-type libraries only, so the show picker could never have been
/// populated even with a token.
///
/// This endpoint reads the stored servers and their tokens entirely server-side, and
/// returns every library type. The client sends nothing, so no caller-supplied host can
/// ever be handed the Plex token.
/// </summary>
public class SettingsPlexLibrariesTests
{
    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r.RequestUri!.ToString());
            return Task.FromResult(respond(r));
        }
    }

    private sealed class StubKeyStore : IApiKeyStore
    {
        public string Current => "test-key";
        public string Regenerate() => "test-key";
    }

    private sealed class UnusedFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private static SettingsController New(Database db, HttpMessageHandler handler)
    {
        var plexService = new PlexService(new HttpClient(handler), db, new LocalFolderResolver(db));
        return new SettingsController(
            db,
            new RadarrLibrarySource(db, new LocalFolderResolver(db), new UnusedFactory()),
            new PlexLibrarySource(plexService, db, new UnusedFactory()),
            plexService,
            new StubKeyStore());
    }

    private static Database NewDb(TempDir dir, bool withServer = true)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.SetSetting("plex_access_token", "acc");
        db.SetSetting("plex_client_identifier", "c1");
        if (withServer)
            db.SetPlexServers([new Dictionary<string, object?> {
                ["id"] = "srv1", ["name"] = "Tower", ["url"] = "http://plex.local:32400",
                ["urls"] = new List<string> { "http://plex.local:32400" }, ["token"] = "stored-tok" }]);
        return db;
    }

    private const string BothTypes = """
        <MediaContainer size="2">
          <Directory key="1" type="movie" title="Films"/>
          <Directory key="3" type="show"  title="TV Shows"/>
        </MediaContainer>
        """;

    /// <summary>
    /// The show picker needs show-type libraries, the movie picker needs movie-type ones,
    /// and both read the same response. Returning one type is what made the show picker
    /// permanently empty.
    /// </summary>
    [Fact]
    public async Task Returns_both_movie_and_show_libraries()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var handler = new RoutingHandler(_ => Xml(BothTypes));

        var result = Assert.IsType<OkObjectResult>(await New(db, handler).PlexLibraries());
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        var libs = body.GetProperty("libraries").GetProperty("srv1").EnumerateArray().ToList();

        Assert.Contains(libs, l => l.GetProperty("type").GetString() == "movie"
                                && l.GetProperty("title").GetString() == "Films");
        Assert.Contains(libs, l => l.GetProperty("type").GetString() == "show"
                                && l.GetProperty("title").GetString() == "TV Shows");
    }

    /// <summary>
    /// The whole point of the endpoint: the caller sends nothing, so the token has to come
    /// from storage. Settings can never supply one — it only ever holds redacted servers.
    /// </summary>
    [Fact]
    public async Task Authenticates_with_the_stored_token()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var handler = new RoutingHandler(_ => Xml(BothTypes));

        await New(db, handler).PlexLibraries();

        Assert.Contains(handler.Requests, r => r.Contains("X-Plex-Token=stored-tok"));
    }

    /// <summary>A server whose token was never stored is skipped, not fatal.</summary>
    [Fact]
    public async Task Returns_an_empty_map_when_no_servers_are_configured()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: false);
        var handler = new RoutingHandler(_ => throw new InvalidOperationException("should not call Plex"));

        var result = Assert.IsType<OkObjectResult>(await New(db, handler).PlexLibraries());
        var body = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);

        Assert.Empty(body.GetProperty("libraries").EnumerateObject());
        Assert.Empty(handler.Requests);
    }

    /// <summary>An unreachable Plex is reported, not silently rendered as "no libraries" —
    /// that ambiguity is what made the original bug so hard to see from the UI.</summary>
    [Fact]
    public async Task Reports_a_failure_rather_than_pretending_there_are_no_libraries()
    {
        using var dir = new TempDir();
        var db = NewDb(dir);
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await New(db, handler).PlexLibraries();

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
