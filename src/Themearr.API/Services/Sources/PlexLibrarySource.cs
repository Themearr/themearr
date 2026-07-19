using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Plex as a library source. A thin adapter: all of the Plex API work stays in
/// <see cref="PlexService"/>, which is left untouched apart from the record it builds.
/// </summary>
public class PlexLibrarySource(PlexService plex) : ILibrarySource
{
    public string Name => "plex";

    /// <summary>Scanning a Plex library is expensive, so once a day.</summary>
    public TimeSpan SyncInterval => TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct) =>
        await plex.FetchMoviesAsync(log);
}
