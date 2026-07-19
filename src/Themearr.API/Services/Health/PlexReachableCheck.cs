using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Themearr.API.Data;

namespace Themearr.API.Services.Health;

/// <summary>
/// Pings the user's own Plex server. The token travels in a header, never the query
/// string, and no exception text is ever surfaced — a raw HttpRequestException can
/// echo the request and we will not risk leaking credentials into the UI.
/// </summary>
public sealed class PlexReachableCheck(Database db, IHttpClientFactory factory) : IHealthCheck
{
    /// <summary>Named client, configured in Program.cs with a short timeout.</summary>
    public const string ClientName = "plex-health";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.IsSetupComplete())
            return HealthCheckResult.Healthy("Setup not complete");

        var servers = db.GetPlexServersDict();
        if (servers.Count == 0)
            return HealthCheckResult.Healthy("No Plex server configured");

        var (url, token) = servers.First().Value;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
            return HealthCheckResult.Healthy("No Plex server configured");

        var http = factory.CreateClient(ClientName);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/identity");
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);

            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return HealthCheckResult.Unhealthy(
                    "Plex rejected the stored token (401). Sign in to Plex again in Settings.");

            if (!response.IsSuccessStatusCode)
                return HealthCheckResult.Unhealthy(
                    $"The Plex server returned HTTP {(int)response.StatusCode}.");

            return HealthCheckResult.Healthy("Plex server is reachable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"The Plex server did not respond within {http.Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException)
        {
            return HealthCheckResult.Unhealthy(
                "The Plex server is unreachable. Check it is running and the URL in Settings is correct.");
        }
    }
}
