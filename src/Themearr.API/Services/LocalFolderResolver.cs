using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Turns a path reported by a library source into a folder on Themearr's own
/// filesystem.
///
/// This is not a Plex concern. Any tool that reports paths — Plex on Windows, Radarr
/// in a container — sees a different filesystem than Themearr does, so its paths must
/// be translated before they mean anything here. It also underpins movie identity:
/// two sources describe the same movie with different path strings, and only after
/// resolution do those strings become the same folder.
/// </summary>
public class LocalFolderResolver(Database db)
{
    /// <summary>
    /// Returns the local folder and how it was found: <c>direct</c>, <c>mapping</c>,
    /// <c>suffix</c>, or <c>unresolved</c> with an empty folder.
    /// </summary>
    public (string folder, string mode) Resolve(string sourceFilePath)
    {
        // Normalize '\' → '/' so a Windows Plex server's paths resolve when Themearr
        // runs in a Linux container (otherwise the parent dir comes back empty).
        var parent = PlexPath.ParentDir(sourceFilePath);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            return (parent, "direct");

        var mapped = ApplyPathMappings(sourceFilePath);
        if (!string.IsNullOrEmpty(mapped) && Directory.Exists(mapped))
            return (mapped, "mapping");

        var suffix = FindBySuffix(sourceFilePath);
        if (!string.IsNullOrEmpty(suffix)) return (suffix, "suffix");

        return ("", "unresolved");
    }

    private string ApplyPathMappings(string sourceFilePath)
    {
        var sourceParent = PlexPath.ParentDir(sourceFilePath);
        foreach (var mapping in db.GetPathMappings())
        {
            var mapped = PlexPath.ApplyMapping(
                sourceParent,
                mapping.GetValueOrDefault("source", ""),
                mapping.GetValueOrDefault("target", ""));
            if (!string.IsNullOrEmpty(mapped)) return mapped;
        }
        return "";
    }

    private string FindBySuffix(string sourceFilePath)
    {
        var roots = db.GetLibraryPaths().Where(Directory.Exists).ToList();
        if (roots.Count == 0) return "";

        var sourceParts = PlexPath.Segments(PlexPath.ParentDir(sourceFilePath));
        if (sourceParts.Length == 0) return "";

        var maxSuffix = Math.Min(6, sourceParts.Length);
        foreach (var root in roots)
            for (var size = maxSuffix; size > 0; size--)
            {
                var candidate = Path.Combine(new[] { root }.Concat(sourceParts[^size..]).ToArray());
                if (Directory.Exists(candidate)) return candidate;
            }

        var target = sourceParts[^1].ToLower();
        var maxDirs = int.Parse(db.GetSetting("max_search_dirs", "20000"));
        var maxDepth = int.Parse(db.GetSetting("search_depth", "4"));
        var visited = 0;

        foreach (var root in roots)
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (++visited > maxDirs) return "";
                var depth = dir[root.Length..].Count(c => c == Path.DirectorySeparatorChar);
                if (depth > maxDepth) continue;
                if (Path.GetFileName(dir).ToLower() == target) return dir;
            }
        return "";
    }
}
