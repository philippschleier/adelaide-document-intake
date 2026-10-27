using Adelaide.Core;

namespace Adelaide.Web;

/// <summary>Deployment policy for the stalled-document gauge — the threshold and the /api/stats shaping. The STORE
/// answers "which machine-owed documents are older than X"; how old X is, is an operational choice that differs
/// between a localhost box processing a handful of faxes and a cloud instance draining a backlog, so it lives here
/// beside the endpoint (the same split as <see cref="SweepHealth.PeriodMinutes"/>).
///
/// See <see cref="StalledDocuments"/> for what the population is, why human-pending documents are excluded, and
/// — the part worth reading before trusting a zero — what this gauge cannot see.</summary>
public static class StallGauge
{
    /// <summary>How long a document BEING WORKED ON may go without an advancing event before it counts as stalled.
    /// DEFAULT 60 minutes (ADELAIDE_STALL_MINUTES).
    ///
    /// Sixty is enormous for this population and that is the point — it is ~85× the worst case ever observed.
    /// Measuring every dwell in the live ledger (the gap between an event leaving a document machine-owed and its
    /// next advancing event, i.e. exactly what this gauge counts) over 65 days, per state:
    ///
    ///     Received     n=2393  max 806.1 min      Matched    n=1637  max 0.3 min
    ///     Transcribed  n=1637  max   0.7 min      Extracted  n=1637  max 0.1 min
    ///     Filing       n= 999  max   0.5 min      Rotated/Reviewed   max 0.0 min
    ///
    /// Outside Received, not one dwell in 6,871 exceeded a single minute. So an hour of silence in Transcribed,
    /// Matched, Extracted, Filing, Reviewed or Rotated is not slowness — it is a document that left the pipeline.
    ///
    /// THIS NUMBER WAS 1440 FOR ONE COMMIT, and the mistake is worth recording because it looked like rigour. The
    /// histogram showed "dwells over 1440: 0" and that was read as "no false alarms". It also means NO ALARMS: a
    /// threshold above the largest dwell ever observed cannot fire on any pattern the ledger has seen. Zero false
    /// positives is trivially achievable by never firing; the question a threshold has to answer is whether it
    /// still catches the true ones.</summary>
    public static int Minutes =>
        int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_STALL_MINUTES"), out var m) && m is > 0 and <= 10080 ? m : 60;

    /// <summary>How long a QUEUED document (see <c>AuditQueryPredicates.Queued</c> — "Received") may wait.
    /// DEFAULT 1440 minutes (ADELAIDE_STALL_QUEUED_MINUTES).
    ///
    /// Intake is an in-memory channel drained by ADELAIDE_CONCURRENCY workers, so a document here is waiting its
    /// turn, and that wait is bounded by the batch, not by the document. The measured worst legitimate wait is 806
    /// minutes — and the ten longest all begin at the SAME instant (2026-06-16 22:10 UTC) and all end in a
    /// reconcile-authored event, i.e. one batch ingested at night on a box that was then off until morning, not 806
    /// independent stalls. A 240-minute cutoff here would have fired 841 times, every one benign.
    ///
    /// TIGHTEN THIS ON AN ALWAYS-ON DEPLOYMENT. The 806-minute tail is a development box that stops overnight; a
    /// cloud instance that never sleeps cannot produce it, and a few hours there is both safe and far more useful.
    /// The default is sized for the deployment that bursts, because that is where a false alarm arrives during the
    /// operation you most want to watch.
    ///
    /// Both bounds are a week — headroom, not a recommendation. Past roughly a day a threshold stops being a signal
    /// and becomes a second dashboard.</summary>
    public static int QueuedMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES"), out var m) && m is > 0 and <= 10080 ? m : 1440;

    /// <summary>The /api/stats block: counts, state names, one timestamp and a capped id sample. No PHI.
    ///
    /// HOW TO READ A NON-ZERO. Resist the urge to judge it from one reading — a single snapshot cannot tell a
    /// draining backlog from a dead pipeline, and the shape does not help. Both look the same at rest: a bulk drain
    /// lands its documents at roughly one instant, so the ones still queued all carry roughly one LastUpdated,
    /// exactly like a cohort frozen by a crash. What separates them is the TREND across polls — a backlog's count
    /// falls while its oldest age plateaus, a stall's count holds while the age climbs minute for minute. An earlier
    /// version of this comment claimed a clustered age meant "draining" and a growing one meant "stuck"; that was
    /// wrong in the first half and is left recorded here so it does not get re-derived.</summary>
    public static async Task<object> SnapshotAsync(IAuditStore store, DateTimeOffset now, CancellationToken ct)
    {
        // Read ONCE and use each for both its cutoff and its published value: a count measured against one number
        // and reported beside another is unreadable, and env vars can change under a running process.
        var (active, queued) = (Minutes, QueuedMinutes);
        var stalled = await store.CountStalledAsync(now.AddMinutes(-active), now.AddMinutes(-queued), ct);
        return new
        {
            // BOTH echoed. The count is meaningless without them, and doubly so here: the same age means "stalled"
            // in one lane and "still queued" in another.
            activeThresholdMinutes = active,
            queuedThresholdMinutes = queued,
            count = stalled.Count,
            byState = stalled.ByState,
            oldestUtc = stalled.Oldest,
            oldestState = stalled.OldestState,
            // Pre-computed so answering "how bad?" is not a timestamp subtraction. Whole minutes: the thresholds are
            // measured in hours, so finer precision would be noise dressed as detail.
            //
            // NOT clamped, and it does not need to be — a review asked for Math.Max(0, …) against clock skew. A row
            // only enters this result if LastUpdated < now - threshold, so `now - Oldest` always exceeds the
            // threshold and cannot be negative. The clamp would be unreachable code implying a hazard that is not
            // there, which is the same disease as a comment naming a backstop that never fires.
            oldestAgeMinutes = stalled.Oldest is { } o ? (int)(now - o).TotalMinutes : (int?)null,
            // Oldest first, capped. The route from this number to the actual document — see StalledDocuments.Sample
            // for why the dashboard's own filters are not that route.
            sample = stalled.Sample,
        };
    }
}
