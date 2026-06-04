namespace Themearr.API.Services;

/// <summary>
/// Source of downloadable theme audio for a given YouTube video id. Implementations
/// encapsulate exactly one download backend (currently youtube-mp36 via RapidAPI),
/// so swapping providers later is a new implementation + a DI registration change
/// rather than a rewrite of <see cref="DownloadService"/>.
/// </summary>
public interface IThemeAudioProvider
{
    /// <summary>
    /// Returns <c>null</c> when the provider is ready to download, or a clear,
    /// user-facing message naming exactly what the operator still has to configure.
    /// Cheap (no network) so callers can pre-flight before starting a job.
    /// </summary>
    string? CheckConfiguration();

    /// <summary>
    /// Downloads the theme audio for <paramref name="videoId"/> to
    /// <paramref name="outputPath"/>, reporting human-readable progress through
    /// <paramref name="progress"/>. Returns the provider-reported track title, if
    /// any. Throws on failure (<see cref="ProviderNotConfiguredException"/> when the
    /// provider is not configured).
    /// </summary>
    Task<string?> DownloadAsync(
        string videoId, string outputPath, Action<string> progress, CancellationToken ct = default);
}

/// <summary>
/// Thrown when a download is attempted while the provider is not configured (e.g.
/// missing API credentials). The message is safe to surface directly to the user.
/// </summary>
public class ProviderNotConfiguredException(string message) : Exception(message);
