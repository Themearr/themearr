namespace Themearr.API.Services;

/// <summary>
/// Filesystem helpers for the per-movie <c>theme.*</c> file: detecting a usable
/// theme, checking the target folder is writable, and writing the download
/// atomically so a failed/killed download can never leave a corrupt theme behind.
/// </summary>
public static class ThemeFiles
{
    // Working extensions that are NOT a finished theme: in-flight download (.part)
    // and yt-dlp's sidecar (.ytdl). Mirrors the read-time status filter.
    private static readonly string[] NonThemeExtensions = [".part", ".ytdl"];

    private static bool IsNonTheme(string path) =>
        NonThemeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="folder"/> contains a finished, non-empty
    /// <c>theme.*</c> file. A zero-byte file (truncated/interrupted download) is
    /// treated as NOT usable so it gets retried instead of being marked downloaded.
    /// </summary>
    public static bool HasUsableTheme(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
        return HasUsableThemeInExistingFolder(folder);
    }

    /// <summary>
    /// As <see cref="HasUsableTheme"/> but WITHOUT the <c>Directory.Exists</c> guard, for
    /// callers that have already confirmed the folder exists (e.g. per-movie status
    /// derivation over a whole library). Skipping the redundant stat halves the filesystem
    /// round-trips per movie — it matters on network-mounted libraries. Throws if the
    /// folder does not exist, so only call it once existence is established.
    /// </summary>
    internal static bool HasUsableThemeInExistingFolder(string folder) =>
        Directory.EnumerateFiles(folder, "theme.*")
            .Any(f => !IsNonTheme(f) && new FileInfo(f).Length > 0);

    /// <summary>
    /// True if the service user can actually create a file in <paramref name="folder"/>.
    /// Used to surface a clear error up front instead of failing every download
    /// silently — the typical Proxmox case where the <c>themearr</c> user lacks write
    /// permission on a bind-mounted media folder. Probes by creating and deleting a
    /// uniquely-named temp file (so it never collides with theme.*).
    /// </summary>
    public static bool IsDirectoryWritable(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
        var probe = Path.Combine(folder, $".themearr-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// True if <paramref name="folder"/>, once canonicalized, is equal to or nested
    /// under one of <paramref name="roots"/>. Used to confine theme writes/deletes to
    /// the configured library roots so a malicious Plex-reported path (absolute, or
    /// containing <c>..</c>) can't target an arbitrary directory. Empty roots → false.
    /// </summary>
    public static bool IsWithinRoots(string folder, IEnumerable<string> roots)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full.Equals(fullRoot, StringComparison.Ordinal)) return true;
            if (full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Streams <paramref name="source"/> into <paramref name="finalPath"/> atomically:
    /// the bytes are written to a sibling <c>.part</c> file (bounded by
    /// <paramref name="maxBytes"/>), and only on success is that file moved into place,
    /// replacing any existing theme. A failed, oversized, killed, or empty download
    /// therefore never clobbers a previously-good theme and never leaves a truncated
    /// <c>theme.mp3</c> on disk. An empty (0-byte) body is rejected. Returns the number
    /// of bytes written.
    /// </summary>
    public static async Task<long> WriteAtomicAsync(
        Stream source, string finalPath, long maxBytes, CancellationToken ct = default)
    {
        var tempPath = finalPath + ".part";
        try
        {
            long written;
            await using (var fileStream = File.Create(tempPath))
            {
                written = await StreamLimits.CopyWithLimitAsync(source, fileStream, maxBytes, ct);
                await fileStream.FlushAsync(ct);
            }

            if (written == 0)
                throw new InvalidOperationException(
                    "Downloaded theme was empty (0 bytes) — refusing to save a corrupt theme.");

            File.Move(tempPath, finalPath, overwrite: true);
            return written;
        }
        catch
        {
            // Never leave the partial file behind; any existing finalPath is untouched
            // because we only Move on success.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
