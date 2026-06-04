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
    /// Downloads the theme audio for <paramref name="videoId"/> to
    /// <paramref name="outputPath"/>, reporting human-readable progress through
    /// <paramref name="progress"/>. Returns the provider-reported track title, if
    /// any. Throws on failure.
    /// </summary>
    Task<string?> DownloadAsync(
        string videoId, string outputPath, Action<string> progress, CancellationToken ct = default);
}
