using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

/// <summary>
/// Picks the configured library source. Exactly one source is active at a time, so
/// there is no merging or precedence to reason about.
/// </summary>
public class LibrarySourceResolver(Database db, IEnumerable<ILibrarySource> sources)
{
    private readonly IReadOnlyList<ILibrarySource> _sources = sources.ToList();

    /// <summary>
    /// The active source. Read fresh each time so changing the setting takes effect
    /// without a restart. An unrecognised value falls back to Plex rather than
    /// throwing — a bad setting must not take the app down.
    /// </summary>
    public ILibrarySource Active
    {
        get
        {
            var configured = db.GetSetting("library_source", "plex");
            return _sources.FirstOrDefault(s => s.Name == configured)
                ?? _sources.First(s => s.Name == "plex");
        }
    }
}
