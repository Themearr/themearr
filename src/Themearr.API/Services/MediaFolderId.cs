using System.Security.Cryptography;
using System.Text;

namespace Themearr.API.Services;

/// <summary>
/// Derives a media item's stable id from the local folder its theme lives in.
///
/// The folder is the real identity — it is what Themearr acts on, and every library
/// source can name it — but folders are not usable as ids directly: they appear in
/// URLs like /api/media/{id}/theme, where a raw path needs escaping, reads badly,
/// and leaks the server's filesystem layout to the browser. Hashing keeps the id
/// short and URL-safe while staying derivable from the folder alone, so no mapping
/// table is ever stored.
/// </summary>
public static class MediaFolderId
{
    /// <summary>
    /// Case is significant: Themearr runs on Linux, where two folders differing only
    /// in case are genuinely different folders.
    /// </summary>
    public static string For(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return "";

        var normalised = folder.TrimEnd('/', '\\');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
