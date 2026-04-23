using System.Security.Cryptography;
using System.Text;

namespace Themearr.API.Services;

public class ApiAuthMiddleware(RequestDelegate next, IConfiguration config, ILogger<ApiAuthMiddleware> log)
{
    private readonly byte[] _expected = LoadToken(config, log);

    public async Task Invoke(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var provided = Encoding.UTF8.GetBytes(header[7..].Trim());
            if (provided.Length == _expected.Length &&
                CryptographicOperations.FixedTimeEquals(provided, _expected))
            {
                await next(ctx);
                return;
            }
        }

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"Themearr\"";
        await ctx.Response.WriteAsJsonAsync(new { detail = "Unauthorized" });
    }

    internal static byte[] LoadToken(IConfiguration config, ILogger log)
    {
        var token = Environment.GetEnvironmentVariable("THEMEARR_AUTH_TOKEN")?.Trim()
                    ?? config["Themearr:AuthToken"]?.Trim()
                    ?? "";
        if (string.IsNullOrEmpty(token))
        {
            log.LogCritical("THEMEARR_AUTH_TOKEN is not set — refusing to start with an unauthenticated API.");
            throw new InvalidOperationException("THEMEARR_AUTH_TOKEN must be set.");
        }
        if (token.Length < 16)
        {
            log.LogCritical("THEMEARR_AUTH_TOKEN must be at least 16 characters.");
            throw new InvalidOperationException("THEMEARR_AUTH_TOKEN too short.");
        }
        return Encoding.UTF8.GetBytes(token);
    }

    public static bool Matches(IConfiguration config, string candidate)
    {
        var token = Environment.GetEnvironmentVariable("THEMEARR_AUTH_TOKEN")?.Trim()
                    ?? config["Themearr:AuthToken"]?.Trim()
                    ?? "";
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(candidate)) return false;
        var a = Encoding.UTF8.GetBytes(candidate);
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
