namespace Themearr.API.Services;

internal static class StreamLimits
{
    // Theme audio is a few MB at most; 100 MB sits comfortably above any real theme
    // while bounding a malicious or mis-pointed URL from filling the data volume.
    public const long MaxThemeBytes = 100L * 1024 * 1024;

    // Poster thumbnails are tens of KB; 20 MB bounds a malicious/oversized image body.
    public const long MaxPosterBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/>, throwing
    /// an <see cref="InvalidOperationException"/> as soon as more than
    /// <paramref name="maxBytes"/> have been read (so an oversized body never fully
    /// lands on disk). Returns the total number of bytes copied.
    /// </summary>
    public static async Task<long> CopyWithLimitAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken ct = default)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException(
                    $"Download exceeded the {maxBytes / (1024 * 1024)} MB limit — aborting.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }
}
