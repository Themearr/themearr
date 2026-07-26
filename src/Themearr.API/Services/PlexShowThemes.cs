using System.Xml.Linq;

namespace Themearr.API.Services;

/// <summary>One TV show from a Plex show-library section: its root folder, and whether
/// Plex already has a theme (`theme` attribute present — Plex-Pass-fetched or a local file).</summary>
public record PlexShow(string RatingKey, string Title, int? Year, bool HasTheme, string RootFolder);

/// <summary>Parses a Plex show listing (/library/sections/{key}/all?type=2) into
/// <see cref="PlexShow"/> records. Pure/HTTP-free so it can be fixture-tested.</summary>
public static class PlexShowThemes
{
    public static IReadOnlyList<PlexShow> Parse(string sectionXml)
    {
        var root = XDocument.Parse(sectionXml).Root
            ?? throw new InvalidOperationException("Plex section response had no root element.");
        return root.Elements("Directory").Select(d =>
        {
            var ratingKey = d.Attribute("ratingKey")?.Value?.Trim() ?? "";
            var title     = d.Attribute("title")?.Value?.Trim() ?? "";
            var year      = int.TryParse(d.Attribute("year")?.Value, out var y) ? y : (int?)null;
            var hasTheme  = !string.IsNullOrWhiteSpace(d.Attribute("theme")?.Value);
            // The show's on-disk root(s) come as <Location path>; use the first.
            var rootFolder = d.Elements("Location").FirstOrDefault()?.Attribute("path")?.Value?.Trim() ?? "";
            return new PlexShow(ratingKey, title, year, hasTheme, rootFolder);
        }).ToList();
    }
}
