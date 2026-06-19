using System.Security.Cryptography;
using System.Text;

namespace Themearr.API.Services;

/// <summary>
/// Signs short-lived, capability-scoped poster URLs so the Plex access token never
/// has to appear in a client-visible <c>&lt;img src&gt;</c>. The signed URL is exempt
/// from bearer auth (an &lt;img&gt; can't send an Authorization header) but
/// self-authenticates via an HMAC over the movie id + expiry, keyed off a secret
/// derived from the API auth token.
/// </summary>
public sealed class PosterUrlSigner
{
    private readonly byte[] _key;

    public PosterUrlSigner(byte[] key) => _key = key;

    public PosterUrlSigner(IConfiguration config) : this(DeriveKey(config)) { }

    private static byte[] DeriveKey(IConfiguration config)
    {
        var token = Environment.GetEnvironmentVariable("THEMEARR_AUTH_TOKEN")?.Trim()
                    ?? config["Themearr:AuthToken"]?.Trim()
                    ?? "";
        // Domain-separated so the signing key is never the raw auth token.
        return SHA256.HashData(Encoding.UTF8.GetBytes("themearr-poster-v1:" + token));
    }

    public string Sign(string id, long expUnix)
    {
        using var h = new HMACSHA256(_key);
        var mac = h.ComputeHash(Encoding.UTF8.GetBytes($"{id}\n{expUnix}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    public bool Verify(string id, long expUnix, string? sig, DateTimeOffset now)
    {
        if (expUnix < now.ToUnixTimeSeconds()) return false;
        var expected = Encoding.UTF8.GetBytes(Sign(id, expUnix));
        var provided = Encoding.UTF8.GetBytes(sig ?? "");
        return expected.Length == provided.Length
               && CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public string PosterPath(string id, DateTimeOffset expiry)
    {
        var exp = expiry.ToUnixTimeSeconds();
        return $"/api/poster?id={Uri.EscapeDataString(id)}&exp={exp}&sig={Sign(id, exp)}";
    }
}
