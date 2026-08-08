using Themearr.API.Controllers;
using Themearr.API.Data;
using Themearr.API.Services;
using Themearr.API.Services.Sources;

namespace Themearr.API.Tests;

/// <summary>
/// Factories for building controllers whose constructors carry dependencies a given test
/// doesn't care about, so each test file only has to supply the piece it's actually
/// exercising.
/// </summary>
internal static class TestControllers
{
    // SettingsController's Radarr dependency needs an IHttpClientFactory; tests that build
    // a controller through this helper aren't exercising Radarr, so any HTTP call it made
    // would be a bug — fail loudly rather than silently returning something.
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException(
                    "Unexpected HTTP call — this test's SettingsController isn't set up to make Radarr requests.");
        }

        public HttpClient CreateClient(string name) => new(new ThrowingHandler());
    }

    public static SettingsController NewSettingsController(Database db, IApiKeyStore keys) =>
        NewSettingsController(db, keys, new UnusedHttpClientFactory());

    // plexFactory supplies the HttpClient PlexLibrarySource.ProbeAsync uses — pass a stub
    // returning canned responses for the /plex/test path; the default throws (probe unused).
    public static SettingsController NewSettingsController(Database db, IApiKeyStore keys, IHttpClientFactory plexFactory)
    {
        // One PlexService instance for both the source adapter and the controller's own
        // library listing, so a test that stubs Plex stubs it for both.
        var plexService = new PlexService(new HttpClient(), db, new LocalFolderResolver(db));
        return new SettingsController(db,
            new RadarrLibrarySource(db, new LocalFolderResolver(db), new UnusedHttpClientFactory()),
            new PlexLibrarySource(plexService, db, plexFactory),
            plexService,
            keys);
    }
}
