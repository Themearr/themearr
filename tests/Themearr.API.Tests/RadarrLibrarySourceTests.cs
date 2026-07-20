using System.Net;
using System.Text;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

public class RadarrLibrarySourceTests
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

    private static (RadarrLibrarySource Source, Database Db) New(TempDir dir, HttpMessageHandler handler)
    {
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();
        db.SetSetting("radarr_url", "http://radarr.local:7878");
        db.SetSetting("radarr_api_key", "secret-radarr-key");
        return (new RadarrLibrarySource(db, new LocalFolderResolver(db), new StubFactory(handler)), db);
    }

    [Fact]
    public async Task Fetches_movies_and_resolves_their_folders()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        var handler = new StubHandler(_ => Json($$"""
            [{"id":7,"title":"Heat","year":1995,"hasFile":true,"path":"{{movieDir.Replace("\\","/")}}"}]
            """));
        var (source, _) = New(dir, handler);

        var movies = await source.FetchAsync(_ => { }, CancellationToken.None);

        var m = Assert.Single(movies);
        Assert.Equal(movieDir, m.Folder);
        Assert.Equal("Heat", m.Title);
        Assert.Equal(1995, m.Year);
        Assert.Equal("radarr", m.Source);
        Assert.Equal("7", m.SourceRef);
    }

    [Fact]
    public async Task Sends_the_api_key_as_a_header_not_a_query_parameter()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("[]"));
        var (source, _) = New(dir, handler);

        await source.FetchAsync(_ => { }, CancellationToken.None);

        Assert.True(handler.LastRequest!.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("secret-radarr-key", Assert.Single(values!));
        Assert.DoesNotContain("secret-radarr-key", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task Movies_without_a_file_are_skipped()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        var handler = new StubHandler(_ => Json($$"""
            [{"id":7,"title":"Heat","year":1995,"hasFile":false,"path":"{{movieDir.Replace("\\","/")}}"}]
            """));
        var (source, _) = New(dir, handler);

        Assert.Empty(await source.FetchAsync(_ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Movies_whose_folder_cannot_be_resolved_are_skipped_and_counted()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("""
            [{"id":9,"title":"Ghost","year":1990,"hasFile":true,"path":"/mnt/nowhere/Ghost (1990)"}]
            """));
        var (source, db) = New(dir, handler);

        Assert.Empty(await source.FetchAsync(_ => { }, CancellationToken.None));
        Assert.Equal("1", db.GetSetting("last_sync_unresolved_count", "0"));
        Assert.Contains("Ghost", db.GetSetting("last_sync_unresolved_sample", ""));
    }

    [Fact]
    public async Task A_trailing_separator_on_the_reported_path_resolves_to_the_same_folder_identity()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        var withoutSlash = movieDir.Replace("\\", "/");
        var handler = new StubHandler(_ => Json($$"""
            [{"id":7,"title":"Heat","year":1995,"hasFile":true,"path":"{{withoutSlash}}/"}]
            """));
        var (source, _) = New(dir, handler);

        var movies = await source.FetchAsync(_ => { }, CancellationToken.None);

        // Without trimming, PlexPath.ParentDir would only strip one of the two trailing
        // slashes left by "<path>//placeholder.mkv", handing back a folder with a
        // trailing slash baked in — a different identity string for the same directory.
        var m = Assert.Single(movies);
        Assert.Equal(movieDir, m.Folder);
        Assert.False(m.Folder.EndsWith('/'));
        Assert.False(m.Folder.EndsWith('\\'));
    }

    [Fact]
    public async Task A_malformed_body_reports_cleanly_without_raw_parser_text()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("this is not json"));
        var (source, _) = New(dir, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchAsync(_ => { }, CancellationToken.None));

        Assert.Contains("unexpected response", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JsonException", ex.Message);
        Assert.DoesNotContain("byte", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_json_object_instead_of_an_array_reports_cleanly_without_raw_parser_text()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => Json("""{"error":"not found"}"""));
        var (source, _) = New(dir, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FetchAsync(_ => { }, CancellationToken.None));

        Assert.Contains("unexpected response", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Array", ex.Message);
        Assert.DoesNotContain("ValueKind", ex.Message);
    }

    [Fact]
    public async Task A_movie_with_a_non_boolean_hasFile_is_skipped_without_aborting_the_sync()
    {
        using var dir = new TempDir();
        var movieDir = Path.Combine(dir.Path, "Heat (1995)");
        Directory.CreateDirectory(movieDir);
        var handler = new StubHandler(_ => Json($$"""
            [{"id":9,"title":"Broken","year":1990,"hasFile":"yes","path":"/mnt/nowhere/Broken"},
             {"id":7,"title":"Heat","year":1995,"hasFile":true,"path":"{{movieDir.Replace("\\", "/")}}"}]
            """));
        var (source, _) = New(dir, handler);

        var movies = await source.FetchAsync(_ => { }, CancellationToken.None);

        var m = Assert.Single(movies);
        Assert.Equal("Heat", m.Title);
    }

    [Fact]
    public async Task A_401_reports_a_rejected_key_without_leaking_it()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (source, _) = New(dir, handler);

        var reason = await source.CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("401", reason);
        Assert.DoesNotContain("secret-radarr-key", reason);
    }

    [Fact]
    public async Task An_unreachable_server_reports_cleanly_without_exception_text()
    {
        using var dir = new TempDir();
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused to http://radarr.local:7878 key=secret-radarr-key"));
        var (source, _) = New(dir, handler);

        var reason = await source.CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.DoesNotContain("secret-radarr-key", reason);
        Assert.DoesNotContain("Connection refused", reason);
    }

    [Fact]
    public async Task An_unconfigured_radarr_reports_what_is_missing()
    {
        using var dir = new TempDir();
        var db = new Database(Path.Combine(dir.Path, "test.db"));
        db.Init();
        db.MarkSetupComplete();
        var source = new RadarrLibrarySource(db, new LocalFolderResolver(db),
            new StubFactory(new StubHandler(_ => Json("[]"))));

        var reason = await source.CheckAsync(CancellationToken.None);

        Assert.NotNull(reason);
        Assert.Contains("not configured", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── FetchPosterAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task FetchPosterAsync_returns_the_served_bytes_and_stays_readable_after_returning()
    {
        using var dir = new TempDir();
        byte[] served = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(served),
        });
        var (source, _) = New(dir, handler);

        var stream = await source.FetchPosterAsync("7", width: 300, CancellationToken.None);

        Assert.NotNull(stream);
        // The HttpResponseMessage FetchPosterAsync fetched from is disposed before the
        // method returns. If the bytes had not been copied out first (e.g. handing back
        // response.Content's stream directly), reading here — after the call has already
        // returned — would throw ObjectDisposedException. Reading successfully proves
        // the response did not leak past the method.
        using var read = new MemoryStream();
        await stream!.CopyToAsync(read);
        Assert.Equal(served, read.ToArray());
    }

    [Fact]
    public async Task FetchPosterAsync_returns_null_instead_of_unbounded_data_over_the_byte_cap()
    {
        using var dir = new TempDir();
        // Mirrors the internal StreamLimits.MaxPosterBytes (20 MB); that constant isn't
        // visible from this assembly, so the cap is duplicated here deliberately.
        const long maxPosterBytes = 20L * 1024 * 1024;
        var oversized = new byte[maxPosterBytes + 1];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(oversized),
        });
        var (source, _) = New(dir, handler);

        var stream = await source.FetchPosterAsync("7", width: 300, CancellationToken.None);

        // FetchPosterAsync returns null when the response exceeds the poster byte cap
        // rather than truncating and returning a partial image.
        Assert.Null(stream);
    }
}
