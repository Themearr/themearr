using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Something Themearr can read a movie library from. Implementations own their own
/// API and their own path quirks, and hand back movies already resolved to local
/// folders — the folder being the identity Themearr keys everything on.
/// </summary>
public interface ILibrarySource
{
    /// <summary>Stable key stored in the <c>library_source</c> setting.</summary>
    string Name { get; }

    /// <summary>
    /// How often a full sync is worth running. This is a property of the source, not
    /// of Themearr: scanning Plex is expensive, so it is measured in hours.
    /// </summary>
    TimeSpan SyncInterval { get; }

    Task<IReadOnlyList<MovieRecord>> FetchAsync(Action<string> log, CancellationToken ct);

    /// <summary>
    /// Streams this source's poster for <paramref name="sourceRef"/>, or null when it
    /// has none. The caller proxies the bytes same-origin, so the source's credentials
    /// never reach the browser. <paramref name="width"/> is expected to already be
    /// clamped to a sane range by the caller; this is a request concern, not something
    /// the source re-validates.
    /// </summary>
    Task<Stream?> FetchPosterAsync(string sourceRef, int width, CancellationToken ct);

    /// <summary>Null when healthy, otherwise a user-facing reason. Never raw exception text.</summary>
    Task<string?> CheckAsync(CancellationToken ct);
}
