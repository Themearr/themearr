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

    private static async Task<(int Status, bool NextCalled, int KeyReads, string? AuthScheme)> Run(
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
        ctx.Items.TryGetValue(ApiAuthMiddleware.AuthSchemeItemKey, out var scheme);
        return (ctx.Response.StatusCode, nextCalled, store.Reads, scheme as string);
    }

    [Fact]
    public async Task A_valid_bearer_token_is_still_accepted()
    {
        var (_, nextCalled, _, _) = await Run(c => c.Request.Headers.Authorization = $"Bearer {Token}");

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task A_valid_api_key_is_accepted()
    {
        var (_, nextCalled, _, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "the-api-key");

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task A_valid_api_key_with_surrounding_whitespace_is_accepted()
    {
        var (_, nextCalled, _, _) = await Run(c => c.Request.Headers["X-Api-Key"] = " the-api-key\n");

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task A_wrong_api_key_is_rejected()
    {
        var (status, nextCalled, _, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "wrong");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task No_credential_at_all_is_rejected()
    {
        var (status, nextCalled, _, _) = await Run(_ => { });

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task An_empty_api_key_header_is_rejected()
    {
        var (status, nextCalled, _, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task A_whitespace_only_api_key_header_is_rejected()
    {
        var (status, nextCalled, _, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "   ");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task The_key_store_is_read_when_the_api_key_header_is_present()
    {
        var (_, nextCalled, reads, _) = await Run(c => c.Request.Headers["X-Api-Key"] = "the-api-key");

        Assert.True(nextCalled);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task The_key_store_is_not_read_when_no_api_key_header_is_present()
    {
        // The browser sends Bearer and never sets X-Api-Key. Reading the stored key
        // on that path would put a database hit on every page load and every poll.
        var (_, nextCalled, reads, _) = await Run(c => c.Request.Headers.Authorization = $"Bearer {Token}");

        Assert.True(nextCalled);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task The_key_store_is_not_read_when_there_is_no_credential_either()
    {
        var (status, _, reads, _) = await Run(_ => { });

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.Equal(0, reads);
    }

    // ── auth scheme marker ───────────────────────────────────────────────────
    // SettingsController's apikey actions rely on this marker to refuse the API key
    // managing itself (see ApiKeyEndpointTests). Verify the middleware actually sets it.

    [Fact]
    public async Task The_auth_scheme_marker_is_set_to_bearer_for_a_valid_bearer_token()
    {
        var (_, _, _, scheme) = await Run(c => c.Request.Headers.Authorization = $"Bearer {Token}");

        Assert.Equal(ApiAuthMiddleware.BearerScheme, scheme);
    }

    [Fact]
    public async Task The_auth_scheme_marker_is_set_to_apikey_for_a_valid_api_key()
    {
        var (_, _, _, scheme) = await Run(c => c.Request.Headers["X-Api-Key"] = "the-api-key");

        Assert.Equal(ApiAuthMiddleware.ApiKeyScheme, scheme);
    }

    [Fact]
    public async Task The_auth_scheme_marker_is_not_set_when_authentication_fails()
    {
        var (_, _, _, scheme) = await Run(_ => { });

        Assert.Null(scheme);
    }
}
