namespace Themearr.API.Services;

/// <summary>
/// Filesystem helpers for the per-media <c>theme.*</c> file (movies and shows alike):
/// detecting a usable theme, locating and typing it for playback, deleting it, checking
/// the target folder is writable, and writing the download atomically so a failed/killed
/// download can never leave a corrupt theme behind.
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

    /// <summary>
    /// The playable theme file in <paramref name="folder"/>, or null when there isn't one.
    /// Shared by the movie and show theme-audio endpoints so the two can never disagree
    /// about which file is "the theme".
    /// </summary>
    public static string? FindThemeFile(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "theme.*").FirstOrDefault(f => !IsNonTheme(f))
            : null;

    /// <summary>
    /// The extension the theme's leading bytes say it should have (issue #48). Decided
    /// from the bytes we actually stored, never from a CDN Content-Type header: the
    /// confirmed production failure is precisely a promise (an "mp3" converter API)
    /// contradicted by the delivered bytes (an MP4/AAC stream inside theme.mp3), so the
    /// header is the one witness known to lie. The result is a closed two-value set —
    /// a filename must never be derived from remote data. An MP4-family file opens with
    /// a box: 4-byte size, then "ftyp"; everything else — including genuine MP3 (an ID3
    /// tag, or a 0xFF-plus-3-bits frame sync) and bytes we cannot identify — keeps the
    /// historical .mp3 name. Unknown-keeps-mp3 is deliberate: possibly wrong, but
    /// exactly as wrong as every download was before sniffing existed, so it can never
    /// regress a working install.
    /// </summary>
    public static string SniffedThemeExtension(ReadOnlySpan<byte> header)
        => header.Length >= 8 && header[4..8].SequenceEqual("ftyp"u8) ? ".m4a" : ".mp3";

    /// <summary>
    /// Renames a just-downloaded theme so its extension states the container actually
    /// received (issue #48), returning the (possibly new) path. The bytes are stored as
    /// received — there is no transcode step — so this is the only point where name and
    /// content can be made to agree. A same-directory <c>File.Move</c> is atomic and
    /// overwrites, so a stale sibling from a previous run with the target name is
    /// replaced rather than collided with.
    /// </summary>
    public static string NormalizeThemeExtension(string path)
    {
        Span<byte> header = stackalloc byte[8];
        int read;
        using (var fs = File.OpenRead(path))
            read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        var ext = SniffedThemeExtension(header[..read]);
        if (string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase))
            return path;

        var renamed = Path.ChangeExtension(path, ext);
        File.Move(path, renamed, overwrite: true);
        return renamed;
    }

    /// <summary>Content type for a theme file, by extension. Falls back to audio/mpeg.</summary>
    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3"  => "audio/mpeg",
        ".m4a"  => "audio/mp4",
        ".ogg"  => "audio/ogg",
        ".opus" => "audio/opus",
        ".webm" => "audio/webm",
        ".flac" => "audio/flac",
        _       => "audio/mpeg",
    };

    /// <summary>
    /// Deletes every theme file in <paramref name="folder"/>, leaving in-flight downloads
    /// (<c>.part</c>/<c>.ytdl</c>) alone. Returns true if anything was deleted. Callers MUST
    /// have already confirmed the folder is inside the configured library roots — see
    /// <see cref="IsWithinRoots"/>. This is a delete path shared by movies and shows;
    /// keeping it in one place is what stops the two containment checks from drifting.
    /// </summary>
    public static bool DeleteThemes(string folder)
    {
        var deleted = false;
        foreach (var f in Directory.EnumerateFiles(folder, "theme.*"))
        {
            if (IsNonTheme(f)) continue;
            File.Delete(f);
            deleted = true;
        }
        return deleted;
    }
}
