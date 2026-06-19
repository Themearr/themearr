using System.Net;
using Themearr.API.Services;

namespace Themearr.API.Tests;

public class HostGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("100.64.0.1")]      // CGNAT
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fc00::1")]         // IPv6 unique-local
    [InlineData("fe80::1")]         // IPv6 link-local
    public void IsPrivateOrLoopback_privateLiteral_true(string host)
    {
        Assert.True(HostGuard.IsPrivateOrLoopback(host));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")] // example.com
    [InlineData("172.15.0.1")]    // just below the private 172.16/12 block
    [InlineData("172.32.0.1")]    // just above it
    public void IsPrivateOrLoopback_publicLiteral_false(string host)
    {
        Assert.False(HostGuard.IsPrivateOrLoopback(host));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPrivateOrLoopback_localhostOrBlank_true(string host)
    {
        Assert.True(HostGuard.IsPrivateOrLoopback(host));
    }

    [Fact]
    public void IsPrivateAddress_matchesLoopback()
    {
        Assert.True(HostGuard.IsPrivateAddress(IPAddress.Loopback));
        Assert.True(HostGuard.IsPrivateAddress(IPAddress.IPv6Loopback));
        Assert.False(HostGuard.IsPrivateAddress(IPAddress.Parse("8.8.8.8")));
    }
}
