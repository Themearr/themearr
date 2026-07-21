using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ApiAuthMiddlewareTests
{
    private const string Token = "test-bearer-token-at-least-16";

    /// <summary>Counts reads so the hot-path property can be asserted.</summary>
    private sealed class CountingKeyStore(string key) : IApiKeyStore
    {
        public int Reads;
        public string Current { get { Reads++; return key; } }
        public string Regenerate() => key;
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Themearr:AuthToken"] = Token })
            .Build();

    private static async Task<(int Status, bool NextCalled, int KeyReads)> Run(
        Action<HttpContext> setup, string apiKey = "the-api-key")
    {
        var store = new CountingKeyStore(apiKey);
        var nextCalled = false;
        var middleware = new ApiAuthMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Config(), NullLogger<ApiAuthMiddleware>.Instance, store);

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        setup(ctx);

        await middleware.Invoke(ctx);
        return (ctx.Response.StatusCode, nextCalled, store.Reads);
    }

    [Fact]
    public async Task A_valid_bearer_token_is_still_accepted()
    {
        var (_, nextCalled, _) = await Run(c => c.Request.Headers.Authorization = $"Bearer {Token}");

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task A_valid_api_key_is_accepted()
    {
        var (_, nextCalled, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "the-api-key");

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task A_wrong_api_key_is_rejected()
    {
        var (status, nextCalled, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "wrong");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task No_credential_at_all_is_rejected()
    {
        var (status, nextCalled, _) = await Run(_ => { });

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task The_key_store_is_not_read_when_no_api_key_header_is_present()
    {
        // The browser sends Bearer and never sets X-Api-Key. Reading the stored key
        // on that path would put a database hit on every page load and every poll.
        var (_, nextCalled, reads) = await Run(c => c.Request.Headers.Authorization = $"Bearer {Token}");

        Assert.True(nextCalled);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task The_key_store_is_not_read_when_there_is_no_credential_either()
    {
        var (status, _, reads) = await Run(_ => { });

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(0, reads);
    }
}
