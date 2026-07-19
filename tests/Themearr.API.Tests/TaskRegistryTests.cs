using Themearr.API.Services;

namespace Themearr.API.Tests;

public class TaskRegistryTests
{
    private static TaskRegistry WithSync()
    {
        var r = new TaskRegistry();
        r.Register("syncLibrary", "Sync Library", TimeSpan.FromHours(24));
        return r;
    }

    [Fact]
    public void Exists_is_true_only_for_registered_ids()
    {
        var r = WithSync();
        Assert.True(r.Exists("syncLibrary"));
        Assert.False(r.Exists("nope"));
    }

    [Fact]
    public void Trigger_returns_false_for_unknown_id()
    {
        Assert.False(WithSync().Trigger("nope"));
    }

    [Fact]
    public async Task Trigger_wakes_a_waiter()
    {
        var r = WithSync();
        var waiter = r.WaitForTriggerAsync("syncLibrary", CancellationToken.None);

        Assert.True(r.Trigger("syncLibrary"));

        await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(waiter.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Five_rapid_triggers_coalesce_into_one_run()
    {
        var r = WithSync();

        // Capacity is 1 with DropWrite: the first write lands, the rest are dropped.
        Assert.True(r.Trigger("syncLibrary"));
        for (var i = 0; i < 4; i++) Assert.False(r.Trigger("syncLibrary"));

        // Exactly one wake is available.
        await r.WaitForTriggerAsync("syncLibrary", CancellationToken.None)
               .WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => r.WaitForTriggerAsync("syncLibrary", cts.Token));
    }

    [Fact]
    public void Snapshot_derives_nextRun_from_lastRun_plus_interval()
    {
        var r = WithSync();
        var started = new DateTime(2026, 7, 19, 2, 0, 0, DateTimeKind.Utc);

        r.RecordRun("syncLibrary", started, TimeSpan.FromMilliseconds(4210), "1451 movies");

        var t = Assert.Single(r.Snapshot());
        Assert.Equal(started, t.LastRunUtc);
        Assert.Equal(4210, t.LastDurationMs);
        Assert.Equal("1451 movies", t.LastResult);
        Assert.Equal(started.AddHours(24), t.NextRunUtc);
        Assert.False(t.IsRunning);
    }

    [Fact]
    public void Snapshot_has_no_nextRun_before_the_first_run()
    {
        var t = Assert.Single(WithSync().Snapshot());
        Assert.Null(t.LastRunUtc);
        Assert.Null(t.NextRunUtc);
    }

    [Fact]
    public void RecordRun_clears_the_running_flag()
    {
        var r = WithSync();
        r.MarkRunning("syncLibrary", true);
        Assert.True(Assert.Single(r.Snapshot()).IsRunning);

        r.RecordRun("syncLibrary", DateTime.UtcNow, TimeSpan.Zero, "done");
        Assert.False(Assert.Single(r.Snapshot()).IsRunning);
    }

    [Fact]
    public void RecordFailure_updates_the_result_but_does_not_move_lastRunUtc_or_nextRunUtc()
    {
        var r = WithSync();
        var started = new DateTime(2026, 7, 19, 2, 0, 0, DateTimeKind.Utc);
        r.RecordRun("syncLibrary", started, TimeSpan.FromMilliseconds(4210), "1451 movies");
        r.MarkRunning("syncLibrary", true);

        r.RecordFailure("syncLibrary", "failed to start — see the application log");

        var t = Assert.Single(r.Snapshot());
        Assert.Equal("failed to start — see the application log", t.LastResult);
        Assert.Equal(started, t.LastRunUtc);
        Assert.Equal(started.AddHours(24), t.NextRunUtc);
        Assert.False(t.IsRunning);
    }

    [Fact]
    public void A_task_registered_with_a_probe_reports_the_probes_value()
    {
        var r = new TaskRegistry();
        var running = false;
        r.Register("syncLibrary", "Sync Library", TimeSpan.FromHours(24), isRunning: () => running);

        Assert.False(Assert.Single(r.Snapshot()).IsRunning);

        running = true;
        Assert.True(Assert.Single(r.Snapshot()).IsRunning);

        running = false;
        Assert.False(Assert.Single(r.Snapshot()).IsRunning);
    }

    [Fact]
    public void The_probe_takes_precedence_over_MarkRunning()
    {
        var r = new TaskRegistry();
        var probeSaysRunning = true;
        r.Register("syncLibrary", "Sync Library", TimeSpan.FromHours(24), isRunning: () => probeSaysRunning);

        // MarkRunning(false) must not be able to override a probe that says "running".
        r.MarkRunning("syncLibrary", false);
        Assert.True(Assert.Single(r.Snapshot()).IsRunning);

        // And vice versa: MarkRunning(true) must not override a probe that says "idle".
        probeSaysRunning = false;
        r.MarkRunning("syncLibrary", true);
        Assert.False(Assert.Single(r.Snapshot()).IsRunning);
    }
}
