namespace Adelaide.Web;

/// <summary>Liveness gauge for the periodic background sweep (reconcile + capture-window purge + the C5 aged
/// filed-input purge), surfaced on <c>/api/stats</c> beside the capture-health block.
///
/// WHY THIS EXISTS. The sweep only logged when it had WORK to report — the summary line is gated on a non-zero
/// counter — and only logged failures when it threw. On an idle instance that makes a HEALTHY sweep and a DEAD
/// timer produce byte-identical output: silence. That is exactly the ambiguity that cost real time on 2026-08-09,
/// when the reconcile stopped erroring and there was no way to tell from the logs whether it had recovered or
/// simply stopped running (it had recovered — but proving that took reading the source to discover the log was
/// conditional). The sweep is the OUTBOX RECOVERY mechanism: if its timer ever faults, the first symptom is
/// documents stuck in "Filing" indefinitely — which used to be discovered by a human noticing, and is now counted
/// by the <c>stalled</c> block beside this one. The two are complements, not duplicates: this one says the timer is
/// ALIVE, that one says documents are actually LEAVING. A dead pipeline with nothing in flight reads healthy on
/// stalled (nothing to be stuck) and dead here; a live timer failing to move a document reads healthy here and
/// non-zero there. Read them together.
///
/// So the signal is POSITIVE: every completed pass stamps a timestamp and increments a counter, whether or not it
/// found anything to do. "Ticked 412 times, last 90 seconds ago, 0 failures" is a healthy idle sweep; a
/// <c>lastTickUtc</c> that stops advancing is a dead one, visible without reading a log. And since the D-2 fix the
/// failure counter covers PER-DOCUMENT recovery failures too, so "ticked 412 times, 400 failures" now reads as what
/// it is — a live timer failing the same work every tick — instead of 412 healthy ticks.
///
/// Process-local and reset on restart, like <see cref="CaptureHealth"/> — the actionable operator question is "is
/// the sweep alive IN THIS PROCESS", and a restart answers it by construction.</summary>
public sealed class SweepHealth
{
    private readonly object _lock = new();
    private long _ticks, _reconcileFailures;
    private DateTimeOffset? _lastTickUtc, _lastReconcileFailureUtc;

    /// <summary>A full periodic pass completed (reconcile + both purges). The heartbeat — stamped even when the
    /// pass found nothing to do, which is the entire point.</summary>
    public void MarkTick(DateTimeOffset now)
    {
        lock (_lock) { _ticks++; _lastTickUtc = now; }
    }

    /// <summary>Reconcile failed to complete a piece of work and swallowed the failure — EITHER the whole pass threw
    /// (the outer catch) OR one document's recovery did (the per-document catches: filing-sidecar re-drive,
    /// supersede/reclassify/rotate/split re-drives). The per-document class was the D-2 gap: those catches keep the
    /// sidecar and retry next tick BY DESIGN, so a document failing the same way every tick produced ~720 WARN lines
    /// a day while this counter read zero and MarkTick called every one of those ticks healthy. Counting them here
    /// makes <c>reconcileFailuresSinceStart</c> mean "reconcile attempts that did not complete their work", not "the
    /// pass blew up" — a persistently failing document now reads as a steadily climbing counter on /api/stats, which
    /// is precisely what the paired log-query alert watches. The tick still completes (the purges run, the timer
    /// survives), so this must stay a separate counter from the heartbeat.</summary>
    public void MarkReconcileFailure(DateTimeOffset now)
    {
        lock (_lock) { _reconcileFailures++; _lastReconcileFailureUtc = now; }
    }

    /// <summary>Non-PHI snapshot for /api/stats: counts and timestamps only.</summary>
    public object Snapshot()
    {
        lock (_lock)
            return new
            {
                intervalMinutes = PeriodMinutes,
                ticksSinceStart = _ticks,
                lastTickUtc = _lastTickUtc,
                reconcileFailuresSinceStart = _reconcileFailures,
                lastReconcileFailureUtc = _lastReconcileFailureUtc,
            };
    }

    /// <summary>The configured tick interval, so a reader can judge "is lastTickUtc overdue?" without knowing the
    /// deployment's env vars. Same parse as the timer itself.</summary>
    public static int PeriodMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_RECONCILE_MINUTES"), out var m) && m is > 0 and <= 1440 ? m : 2;
}
