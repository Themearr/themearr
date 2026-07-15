namespace Themearr.API.Services;

/// <summary>
/// Separator-agnostic parsing of Plex-reported media paths. A Plex server on Windows
/// reports paths with '\' separators (e.g. <c>M:\Movies\Red One (2024)\file.mkv</c>),
/// but Themearr often runs in a Linux container where <see cref="System.IO.Path"/>
/// only understands '/'. Without normalizing, the parent directory comes back empty and
/// every movie fails to resolve ("unresolved path"). These helpers normalize both
/// separators so path mappings and suffix search work regardless of the Plex host OS.
/// </summary>
public static class PlexPath
{
    public static string Normalize(string? path) => (path ?? "").Replace('\\', '/');

    /// <summary>Parent directory of a (possibly Windows) file path, normalized to '/'.</summary>
    public static string ParentDir(string filePath)
    {
        var p = Normalize(filePath).TrimEnd('/');
        var idx = p.LastIndexOf('/');
        return idx < 0 ? "" : p[..idx];
    }

    /// <summary>
    /// Translates <paramref name="sourceParent"/> under a <c>src → tgt</c> mapping,
    /// normalizing separators and matching the source case-insensitively (Windows paths
    /// are case-insensitive). Returns "" when the mapping doesn't apply.
    /// </summary>
    public static string ApplyMapping(string sourceParent, string src, string tgt)
    {
        sourceParent = Normalize(sourceParent).TrimEnd('/');
        src = Normalize(src).TrimEnd('/');
        tgt = Normalize(tgt).TrimEnd('/');
        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) return "";

        if (sourceParent.Equals(src, StringComparison.OrdinalIgnoreCase))
            return tgt;
        if (sourceParent.StartsWith(src + "/", StringComparison.OrdinalIgnoreCase))
            return tgt + sourceParent[src.Length..];   // preserve the real case of the suffix
        return "";
    }

    /// <summary>Path segments, split on either separator (for suffix search).</summary>
    public static string[] Segments(string path) =>
        Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
}
