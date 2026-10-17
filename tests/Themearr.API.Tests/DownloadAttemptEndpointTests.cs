using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Controllers;
using Themearr.API.Services;
using static Themearr.API.Tests.DownloadAttemptProtocolTests;

namespace Themearr.API.Tests;

public class DownloadAttemptEndpointTests
{
    private sealed class Search : YoutubeService
    {
        public override Task<List<Dictionary<string, object?>>> SearchAsync(string query, int maxResults = 8, string? title = null, int? year = null) =>
            Task.FromResult(new List<Dictionary<string, object?>> { new() { ["videoId"] = "abcdefghijk", ["title"] = "Theme", ["bestMatch"] = true, ["score"] = 100 } });
    }
    private static MoviesController Movies(Fixture f) => new(f.Db, new Search(), f.Service, new PosterUrlSigner(new byte[32]), null!, NullLogger<MoviesController>.Instance);
    private static ShowsController Shows(Fixture f) => new(f.Db, new Search(), f.Service, new PosterUrlSigner(new byte[32]), NullLogger<ShowsController>.Instance);

    [Theory]
    [InlineData("movie", "manual")]
    [InlineData("movie", "url")]
    [InlineData("movie", "auto")]
    [InlineData("show", "manual")]
    [InlineData("show", "url")]
    [InlineData("show", "auto")]
    public async Task Every_accepted_route_returns_the_reserved_id_and_conflict_stays_409(string media, string route)
    {
        using var f = new Fixture();
        async Task<IActionResult> Start() => (media, route) switch
        {
            ("movie", "manual") => Movies(f).Download(new DownloadRequest(f.Id, "abcdefghijk")),
            ("movie", "url") => Movies(f).DownloadUrl(new DownloadUrlRequest(f.Id, Url)),
            ("movie", _) => await Movies(f).AutoDownload(f.Id),
            ("show", "manual") => Shows(f).Download(f.Id, new ShowDownloadRequest("abcdefghijk")),
            ("show", "url") => Shows(f).DownloadUrl(f.Id, new ShowDownloadUrlRequest(Url)),
            _ => await Shows(f).AutoDownload(f.Id),
        };
        try
        {
            var result = Assert.IsType<AcceptedResult>(await Start()); var body = Snapshot(result.Value!);
            Assert.True(body.GetProperty("started").GetBoolean());
            Assert.Equal(f.Id, body.GetProperty(media == "movie" ? "movieId" : "showId").GetString());
            Assert.True(body.TryGetProperty("attemptId", out var id), "accepted route must return reserved attemptId");
            Assert.Equal(f.Service.GetAttemptStatus(f.Id, media, null).AttemptId, id.GetGuid());
            Assert.IsType<ConflictObjectResult>(await Start());
        }
        finally { f.Provider.Release.TrySetResult(); await f.Finish(media); }
    }
    [Theory]
    [InlineData("movie")]
    [InlineData("show")]
    public async Task Status_accepts_optional_identity_and_rejects_malformed_queries(string media)
    {
        using var f = new Fixture();
        IActionResult Status(string? attempt) => media == "movie" ? Movies(f).DownloadStatus(f.Id, attempt) : Shows(f).DownloadStatus(f.Id, attempt);
        Assert.IsType<BadRequestObjectResult>(Status("not-a-guid"));
        Assert.Equal("unknown", Snapshot(Assert.IsType<OkObjectResult>(Status(Guid.NewGuid().ToString())).Value!).GetProperty("state").GetString());
        var start = f.Service.StartAttempt(f.Id, Url, media, "manual");
        try
        {
            var current = Snapshot(Assert.IsType<OkObjectResult>(Status(start.AttemptId!.Value.ToString())).Value!);
            Assert.Equal(start.AttemptId, current.GetProperty("attemptId").GetGuid()); Assert.True(current.GetProperty("inProgress").GetBoolean());
            var old = Snapshot(Assert.IsType<OkObjectResult>(Status(Guid.NewGuid().ToString())).Value!);
            Assert.Equal("superseded", old.GetProperty("state").GetString()); Assert.Empty(old.GetProperty("logs").EnumerateArray());
            Assert.False(old.GetProperty("finished").GetBoolean());
            Assert.True(Snapshot(Assert.IsType<OkObjectResult>(Status(null)).Value!).GetProperty("inProgress").GetBoolean());
        }
        finally { f.Provider.Release.TrySetResult(); await f.Finish(media); }
    }

    [Theory]
    [InlineData("/api/download/status/id")]
    [InlineData("/api/shows/id/download/status")]
    public async Task Attempt_query_does_not_bypass_authentication(string path)
    {
        using var f = new Fixture(); var reached = false;
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Themearr:AuthToken"] = "test-token-with-enough-characters" }).Build();
        var middleware = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; }, config, NullLogger<ApiAuthMiddleware>.Instance, new ApiKeyStore(f.Db));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext(); context.Request.Path = path;
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?attemptId=" + Guid.NewGuid());
        context.Response.Body = new MemoryStream();
        Assert.True(ApiAuthMiddleware.RequiresAuth(context.Request.Path)); await middleware.Invoke(context);
        Assert.Equal(401, context.Response.StatusCode); Assert.False(reached);
    }

}
