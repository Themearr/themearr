using System.Net;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// Direct coverage of <see cref="PlexLibrarySource"/>'s real Plex behaviour — the
/// health ping in CheckAsync and the poster fetch in FetchPosterAsync. These used to
/// live against a dedicated PlexReachableCheck; that type is gone, but the source
/// still owns exactly the same responsibilities (and the same "never leak the token"
/// contract) so the coverage belongs here now.
/// </summary>
public class PlexLibrarySourceTests
{
    private const string ServerId = "srv1";
    private const string Token = "secret-token-value";

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

    private static Database NewDb(TempDir dir, bool withServer, string token = Token)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();
        if (withServer)
        {
            db.SetPlexServers([new Dictionary<string, object?>
            {
                ["id"]    = ServerId,
                ["name"]  = "Tower",
                ["url"]   = "http://plex.local:32400",
                ["token"] = token,
            }]);
        }
        return db;
    }

    private static PlexLibrarySource NewSource(Database db, HttpMessageHandler handler)
    {
        var plex = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));
        return new PlexLibrarySource(plex, db, new StubFactory(handler));
    }

    // ── CheckAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task No_configured_server_returns_null()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: false);
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not be called"));

        var reason = await NewSource(db, handler).CheckAsync(CancellationToken.None);

        Assert.Null(reason);
    }

    [Fact]
    public async Task A_reachable_server_returns_null()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var reason = await NewSource(db, handler).CheckAsync(CancellationToken.None);

        Assert.Null(reason);
    }

    [Fact]
    public async Task A_401_reports_the_rejected_token()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var reason = await NewSource(db, handler).CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("token", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_non_success_status_names_the_status_code()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var reason = await NewSource(db, handler).CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("500", reason);
    }

    [Fact]
    public async Task A_timeout_reports_no_response_when_the_caller_token_was_not_cancelled()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));

        var reason = await NewSource(db, handler).CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("did not respond", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_connection_failure_reports_unreachable()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused to http://plex.local:32400"));

        var reason = await NewSource(db, handler).CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("unreachable", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_token_never_appears_in_the_health_message_or_the_request_uri()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        var exceptionText = $"boom {Token}";
        var handler = new StubHandler(_ => throw new HttpRequestException(exceptionText));

        var reason = await NewSource(db, handler).CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.DoesNotContain(Token, reason);
        Assert.DoesNotContain(exceptionText, reason);
        // The token belongs in the X-Plex-Token header, never the URI the handler saw.
        Assert.DoesNotContain(Token, handler.LastRequest?.RequestUri?.ToString() ?? "");
    }

    // ── FetchPosterAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task FetchPosterAsync_returns_a_stream_for_a_well_formed_ref()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        });

        var stream = await NewSource(db, handler)
            .FetchPosterAsync($"{ServerId}:12345", width: 300, CancellationToken.None);

        Assert.NotNull(stream);
    }

    [Theory]
    [InlineData("no-colon-here")]
    [InlineData("")]
    [InlineData("unknown-server:12345")]
    public async Task FetchPosterAsync_returns_null_for_bad_or_unknown_refs(string sourceRef)
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        var handler = new StubHandler(_ => throw new InvalidOperationException("should not be called"));

        var stream = await NewSource(db, handler).FetchPosterAsync(sourceRef, width: 300, CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public async Task FetchPosterAsync_returns_the_served_bytes_and_stays_readable_after_returning()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        byte[] served = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(served),
        });

        var stream = await NewSource(db, handler)
            .FetchPosterAsync($"{ServerId}:12345", width: 300, CancellationToken.None);

        Assert.NotNull(stream);
        // The HttpResponseMessage FetchPosterAsync fetched from is disposed before the
        // method returns. If the bytes had not been copied out first (e.g. the old code
        // that handed back resp.Content's stream directly), reading here — after the
        // call has already returned — would throw ObjectDisposedException. Reading
        // successfully proves the response did not leak past the method.
        using var read = new MemoryStream();
        await stream!.CopyToAsync(read);
        Assert.Equal(served, read.ToArray());
    }

    [Fact]
    public async Task FetchPosterAsync_returns_null_instead_of_unbounded_data_over_the_byte_cap()
    {
        using var dir = new TempDir();
        var db = NewDb(dir, withServer: true);
        // Mirrors the internal StreamLimits.MaxPosterBytes (20 MB); that constant isn't
        // visible from this assembly, so the cap is duplicated here deliberately.
        const long maxPosterBytes = 20L * 1024 * 1024;
        var oversized = new byte[maxPosterBytes + 1];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(oversized),
        });

        var stream = await NewSource(db, handler)
            .FetchPosterAsync($"{ServerId}:12345", width: 300, CancellationToken.None);

        // FetchPosterAsync returns null when the response exceeds the poster byte cap
        // (mirroring PosterController's "any failure -> NotFound" behaviour) rather than
        // truncating and returning a partial image.
        Assert.Null(stream);
    }
}
