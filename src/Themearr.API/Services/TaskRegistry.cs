using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Themearr.API.Services;

/// <summary>A scheduled task's state as shown on the System → Tasks tab.</summary>
public sealed record TaskState(
    string    Id,
    string    Name,
    TimeSpan  Interval,
    DateTime? LastRunUtc,
    long?     LastDurationMs,
    string?   LastResult,
    DateTime? NextRunUtc,
    bool      IsRunning);

/// <summary>
/// Decouples the System controller from the background workers. Workers push run
/// state in via <see cref="RecordRun"/> and pull wake-ups out via
/// <see cref="WaitForTriggerAsync"/>; the controller does the mirror image. Neither
/// side holds a reference to the other, so "Run now wakes the task" is testable
/// without a host or a timer.
/// </summary>
public sealed class TaskRegistry
{
    // Bundles the four run-state fields so RecordRun and MarkRunning publish them
    // as a single atomic swap. Without this, a reader on another thread could see
    // a torn mix of old and new values (e.g. a fresh LastRunUtc paired with a
    // stale LastResult) since there's no fence ordering four separate field writes.
    private sealed record RunState(DateTime? LastRunUtc, long? LastDurationMs, string? LastResult, bool IsRunning)
    {
        public static readonly RunState Initial = new(null, null, null, false);
    }

    private sealed class Entry
    {
        public required string   Name     { get; init; }
        public required TimeSpan Interval { get; init; }

        private RunState _state = RunState.Initial;

        public RunState State
        {
            get => Volatile.Read(ref _state);
            set => Volatile.Write(ref _state, value);
        }

        // Capacity 1 + Wait is the whole debounce: an impatient user clicking
        // "Run now" five times queues one run, not five library syncs. Wait mode
        // makes TryWrite return false (instead of blocking) once the single slot
        // is occupied, which is what lets Trigger report "already pending" to the
        // caller; DropWrite would silently discard the same way but TryWrite would
        // still report success, losing that signal.
        public readonly Channel<byte> Trigger = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
    }

    private readonly ConcurrentDictionary<string, Entry> _tasks = new();

    public void Register(string id, string name, TimeSpan interval) =>
        _tasks[id] = new Entry { Name = name, Interval = interval };

    public bool Exists(string id) => _tasks.ContainsKey(id);

    /// <summary>True if a wake-up was queued; false for an unknown id or when one is already pending.</summary>
    public bool Trigger(string id) =>
        _tasks.TryGetValue(id, out var e) && e.Trigger.Writer.TryWrite(0);

    /// <summary>Completes when someone triggers this task. An unknown id waits forever (until cancelled).</summary>
    public async Task WaitForTriggerAsync(string id, CancellationToken ct)
    {
        if (!_tasks.TryGetValue(id, out var e))
        {
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }
        await e.Trigger.Reader.ReadAsync(ct);
    }

    public void MarkRunning(string id, bool running)
    {
        if (_tasks.TryGetValue(id, out var e)) e.State = e.State with { IsRunning = running };
    }

    public void RecordRun(string id, DateTime startedUtc, TimeSpan duration, string result)
    {
        if (!_tasks.TryGetValue(id, out var e)) return;
        e.State = new RunState(startedUtc, (long)duration.TotalMilliseconds, result, false);
    }

    public IReadOnlyList<TaskState> Snapshot() =>
        _tasks
            .Select(kv =>
            {
                var state = kv.Value.State;
                return new TaskState(
                    kv.Key,
                    kv.Value.Name,
                    kv.Value.Interval,
                    state.LastRunUtc,
                    state.LastDurationMs,
                    state.LastResult,
                    state.LastRunUtc is { } last ? last + kv.Value.Interval : null,
                    state.IsRunning);
            })
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
}
