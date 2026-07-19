using System.Collections.Concurrent;
using Themearr.API.Data;

namespace Themearr.API.Services;

public class SyncService(Database db, PlexService plex, ILogger<SyncService> log)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentQueue<string> _logs = new();
    private volatile bool _inProgress;
    private volatile bool _finished;
    private volatile int  _synced;
    private volatile string _error = "";

    // Handle on the running sync so a caller can await the outcome instead of only
    // learning that one was launched. Completed by default, so awaiting before the
    // first sync returns immediately rather than hanging.
    private volatile Task _current = Task.CompletedTask;

    public bool   InProgress => _inProgress;
    public Task   Current    => _current;
    public string Error      => _error;
    public int    Synced     => _synced;

    public async Task<bool> StartAsync()
    {
        if (_inProgress) return false;
        if (!await _lock.WaitAsync(0)) return false;

        _inProgress = true;
        _finished   = false;
        _error      = "";
        _synced     = 0;
        while (_logs.TryDequeue(out _)) { }

        _current = Task.Run(RunAsync).ContinueWith(_ => _lock.Release());
        return true;
    }

    public object GetStatus() => new
    {
        inProgress = _inProgress,
        finished   = _finished,
        error      = _error,
        synced     = _synced,
        logs       = _logs.ToArray(),
    };

    private async Task RunAsync()
    {
        try
        {
            AddLog("Starting Plex sync...");
            var movies = await plex.FetchMoviesAsync(AddLog);
            AddLog($"Upserting {movies.Count} matched movies into the local database");
            db.UpsertMovies(movies);
            _synced = movies.Count;
            AddLog($"Sync complete. {movies.Count} movies available locally.");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            AddLog($"Sync failed: {ex.Message}");
            log.LogError(ex, "Plex sync failed");
        }
        finally
        {
            _finished   = true;
            _inProgress = false;
        }
    }

    private void AddLog(string msg) => _logs.Enqueue(msg.TrimEnd());
}
