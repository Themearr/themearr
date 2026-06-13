using System.Text;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class PosterUrlSignerTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("test-signing-key-0123456789");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_000_000);

    private static PosterUrlSigner New() => new(Key);

    [Fact]
    public void Verify_freshSignature_true()
    {
        var s = New();
        var exp = Now.AddHours(1).ToUnixTimeSeconds();
        var sig = s.Sign("srv1:42", exp);
        Assert.True(s.Verify("srv1:42", exp, sig, Now));
    }

    [Fact]
    public void Verify_tamperedId_false()
    {
        var s = New();
        var exp = Now.AddHours(1).ToUnixTimeSeconds();
        var sig = s.Sign("srv1:42", exp);
        Assert.False(s.Verify("srv1:99", exp, sig, Now));
    }

    [Fact]
    public void Verify_tamperedSignature_false()
    {
        var s = New();
        var exp = Now.AddHours(1).ToUnixTimeSeconds();
        Assert.False(s.Verify("srv1:42", exp, "deadbeef", Now));
    }

    [Fact]
    public void Verify_expired_false()
    {
        var s = New();
        var exp = Now.AddHours(-1).ToUnixTimeSeconds(); // already past
        var sig = s.Sign("srv1:42", exp);
        Assert.False(s.Verify("srv1:42", exp, sig, Now));
    }

    [Fact]
    public void Verify_differentKey_false()
    {
        var exp = Now.AddHours(1).ToUnixTimeSeconds();
        var sig = New().Sign("srv1:42", exp);
        var other = new PosterUrlSigner(Encoding.UTF8.GetBytes("a-totally-different-key-9876"));
        Assert.False(other.Verify("srv1:42", exp, sig, Now));
    }

    [Fact]
    public void PosterPath_isSelfConsistent_andTokenFree()
    {
        var s = New();
        var path = s.PosterPath("srv1:42", Now.AddHours(6));

        Assert.StartsWith("/api/poster?", path);
        Assert.Contains("id=srv1%3A42", path); // colon URL-encoded
        Assert.DoesNotContain("X-Plex-Token", path);

        var q = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + path).Query);
        Assert.True(s.Verify(q["id"]!, long.Parse(q["exp"]!), q["sig"]!, Now));
    }
}
