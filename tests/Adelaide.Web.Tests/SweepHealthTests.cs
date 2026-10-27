using System.Text.Json;
using Xunit;

namespace Adelaide.Web.Tests;

// The heartbeat exists to make ONE distinction the logs could not: a healthy idle sweep vs a dead timer. Both were
// silent before, because the sweep's summary line is gated on having found work. These pin the properties that
// distinction depends on — a tick is recorded even with nothing to do, and a swallowed reconcile failure is
// counted separately so a persistently failing sweep cannot hide behind an advancing tick counter.
public class SweepHealthTests
{
    private static JsonElement Snap(SweepHealth h) =>
        JsonSerializer.SerializeToElement(h.Snapshot());

    [Fact]
    public void Fresh_health_has_no_tick_yet()
    {
        var s = Snap(new SweepHealth());
        Assert.Equal(0, s.GetProperty("ticksSinceStart").GetInt64());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("lastTickUtc").ValueKind); // never ticked ≠ ticked long ago
        Assert.Equal(0, s.GetProperty("reconcileFailuresSinceStart").GetInt64());
    }

    [Fact]
    public void A_pass_that_found_nothing_still_stamps_the_heartbeat()
    {
        // The whole point: an idle sweep must be visibly alive. MarkTick takes no work counters by design.
        var h = new SweepHealth();
        var t = DateTimeOffset.UtcNow;
        h.MarkTick(t);
        var s = Snap(h);
        Assert.Equal(1, s.GetProperty("ticksSinceStart").GetInt64());
        Assert.Equal(t, s.GetProperty("lastTickUtc").GetDateTimeOffset());
    }

    [Fact]
    public void Ticks_accumulate_and_the_timestamp_advances()
    {
        var h = new SweepHealth();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddMinutes(2);
        h.MarkTick(t1);
        h.MarkTick(t2);
        var s = Snap(h);
        Assert.Equal(2, s.GetProperty("ticksSinceStart").GetInt64());
        Assert.Equal(t2, s.GetProperty("lastTickUtc").GetDateTimeOffset()); // newest wins — this is the staleness signal
    }

    [Fact]
    public void A_failing_reconcile_is_counted_even_though_its_tick_completes()
    {
        // The 2026-08-09 shape: reconcile threw every 2 minutes while the timer stayed perfectly healthy. The tick
        // counter alone would have read as fine, so the failure must be its own signal.
        var h = new SweepHealth();
        var t = DateTimeOffset.UtcNow;
        h.MarkReconcileFailure(t);
        h.MarkTick(t);
        var s = Snap(h);
        Assert.Equal(1, s.GetProperty("ticksSinceStart").GetInt64());
        Assert.Equal(1, s.GetProperty("reconcileFailuresSinceStart").GetInt64());
        Assert.Equal(t, s.GetProperty("lastReconcileFailureUtc").GetDateTimeOffset());
    }

    [Fact]
    public void Snapshot_publishes_the_interval_so_overdue_is_computable()
    {
        // A reader must be able to judge "is lastTickUtc overdue?" without knowing the deployment's env vars.
        var s = Snap(new SweepHealth());
        Assert.Equal(SweepHealth.PeriodMinutes, s.GetProperty("intervalMinutes").GetInt32());
        Assert.InRange(s.GetProperty("intervalMinutes").GetInt32(), 1, 1440);
    }

    [Fact]
    public void Snapshot_carries_no_free_text()
    {
        // Counts and timestamps only — this rides /api/stats, which must stay non-PHI. Timestamps DO serialise as
        // JSON strings, so the check is not "no strings" but "every string is a parseable timestamp": that admits
        // the two instants and still fails the moment anyone adds an error message, a path, or a document name.
        var h = new SweepHealth();
        h.MarkTick(DateTimeOffset.UtcNow);
        h.MarkReconcileFailure(DateTimeOffset.UtcNow);
        var timestampProps = new[] { "lastTickUtc", "lastReconcileFailureUtc" };
        foreach (var p in Snap(h).EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
        {
            Assert.Contains(p.Name, timestampProps);
            Assert.True(DateTimeOffset.TryParse(p.Value.GetString(), out _), $"{p.Name} is a string but not a timestamp");
        }
    }
}
