using Microsoft.AspNetCore.Http;
using Themearr.API.Services;

namespace Themearr.API.Tests;

/// <summary>
/// The public surface is exactly /api/auth and /api/poster. Everything else under /api
/// must require a credential. This is a prefix match, so it is one careless edit away from
/// exposing a whole namespace — hence the explicit table. Show posters deliberately live
/// at /api/poster/show for this reason: no exemption change was needed to add them.
/// </summary>
public class AuthBoundaryTests
{
    [Theory]
    [InlineData("/api/shows")]
    [InlineData("/api/shows/abc123/download")]
    [InlineData("/api/shows/abc123/theme/audio")]
    [InlineData("/api/stats/shows")]
    [InlineData("/api/movies")]
    [InlineData("/api/settings")]
    public void Protected_routes_require_auth(string path) =>
        Assert.True(ApiAuthMiddleware.RequiresAuth(new PathString(path)));

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/poster")]
    [InlineData("/api/poster/show")]
    public void Public_routes_do_not(string path) =>
        Assert.False(ApiAuthMiddleware.RequiresAuth(new PathString(path)));

    [Fact]
    public void Non_api_paths_are_not_guarded_here()
    {
        Assert.False(ApiAuthMiddleware.RequiresAuth(new PathString("/index.html")));
        Assert.False(ApiAuthMiddleware.RequiresAuth(new PathString("/")));
    }

    /// <summary>A segment-boundary check: /api/posterize must NOT inherit the exemption.</summary>
    [Fact]
    public void A_route_that_merely_starts_with_poster_is_still_protected() =>
        Assert.True(ApiAuthMiddleware.RequiresAuth(new PathString("/api/posterize")));
}
