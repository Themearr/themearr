using Themearr.API.Services;

namespace Themearr.API.Tests;

public class ShowPosterTests
{
    private static readonly byte[] Key = [1, 2, 3, 4, 5];

    [Fact]
    public void ShowPosterPath_targets_the_public_poster_prefix()
    {
        var signer = new PosterUrlSigner(Key);
        var path = signer.ShowPosterPath("abc", DateTimeOffset.UtcNow.AddHours(1));

        // Must live under /api/poster so the existing auth exemption covers it. Anything
        // under /api/shows would 401 for an <img> tag.
        Assert.StartsWith("/api/poster/show?", path);
    }

    /// <summary>
    /// MediaFolderId is a pure function of the folder path, so a show and a movie on one
    /// folder share an id. Domain-separating the signature means a movie poster URL can
    /// never be replayed against the show route, or vice versa.
    /// </summary>
    [Fact]
    public void A_movie_signature_does_not_verify_as_a_show_signature()
    {
        var signer = new PosterUrlSigner(Key);
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var movieSig = signer.Sign("shared-id", exp);

        Assert.True(signer.Verify("shared-id", exp, movieSig, DateTimeOffset.UtcNow));
        Assert.False(signer.VerifyShow("shared-id", exp, movieSig, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_show_signature_verifies_on_the_show_route_only()
    {
        var signer = new PosterUrlSigner(Key);
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var path = signer.ShowPosterPath("s1", DateTimeOffset.FromUnixTimeSeconds(exp));
        var sig  = path.Split("&sig=")[1];

        Assert.True(signer.VerifyShow("s1", exp, sig, DateTimeOffset.UtcNow));
        Assert.False(signer.Verify("s1", exp, sig, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void An_expired_show_signature_is_rejected()
    {
        var signer = new PosterUrlSigner(Key);
        var exp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var sig = signer.Sign("show:s1", exp);

        Assert.False(signer.VerifyShow("s1", exp, sig, DateTimeOffset.UtcNow));
    }
}
