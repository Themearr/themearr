using System.Net;
using System.Net.Sockets;

namespace Themearr.API.Services;

/// <summary>
/// SSRF guard: rejects hosts/IPs in private, loopback, link-local, CGNAT, or IPv6
/// unique-local ranges. Used both to pre-validate a user-supplied download URL and to
/// re-validate every redirect hop before following it (redirects are the classic
/// bypass of an initial-host-only check).
/// </summary>
public static class HostGuard
{
    public static bool IsPrivateOrLoopback(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
            addresses = [literal];
        else
            try { addresses = Dns.GetHostAddresses(host); }
            catch { return true; } // fail-closed on DNS errors

        return addresses.Length == 0 || addresses.Any(IsPrivateAddress);
    }

    public static bool IsPrivateAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10/8, 172.16/12, 192.168/16, 169.254/16, 100.64/10 (CGNAT), 0/8
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            if (b[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique-local
        }
        return false;
    }
}
