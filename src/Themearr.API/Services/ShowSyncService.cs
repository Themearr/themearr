using Themearr.API.Data;

namespace Themearr.API.Services;

/// <summary>
/// Syncs TV shows from the operator's selected Plex show libraries into the `shows`
/// table. Opt-in: when no show libraries are selected it fetches nothing and prunes
/// nothing. Mirrors <see cref="SyncService"/>'s fetch → upsert → prune-except shape,
/// with the same "only prune after a non-empty, fully-resolved sync" safety.
/// </summary>
public class ShowSyncService(Database db, PlexService plex, ILogger<ShowSyncService> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (db.GetSelectedShowLibraries().Values.Sum(v => v.Count) == 0)
            return 0;   // opt-in: nothing selected

        var shows = await plex.FetchShowsAsync(msg => log.LogInformation("{Msg}", msg));
        db.UpsertShows(shows);

        var unresolved = int.TryParse(db.GetSetting("last_show_sync_unresolved_count", "0"), out var n) ? n : 0;
        if (shows.Count > 0 && unresolved == 0)
        {
            var removed = db.PruneShowsExcept(shows.Select(s => s.Folder));
            if (removed > 0) log.LogInformation("Removed {N} shows no longer in the library", removed);
        }
        return shows.Count;
    }
}
