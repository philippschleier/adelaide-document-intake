namespace Adelaide.Core;

/// <summary>
/// The stalled-document gauge: documents THE MACHINE OWES A MOVE that have not advanced in an unusual interval.
///
/// WHY THIS EXISTS. <see cref="AuditEventTypes"/> made "no advancing event" a reachable state — an annotation
/// chains, counts toward EventCount and leaves <c>State</c> and <c>LastUpdated</c> alone, exactly as intended, but
/// a type wrongly listed as non-advancing therefore makes documents of that type stop advancing SILENTLY: no
/// error, no dead-letter, the projection simply keeps the previous state forever. That hazard is stated in
/// AuditEventTypes' own docstring and nothing detected it. This is the detector.
///
/// It turned out to cover MORE than the annotation hazard, which is the better reason to have it. The periodic
/// sweep RE-ENQUEUES only documents in <c>Filing</c> (<c>filingOnly</c> gates the re-enqueue arm alone — the sweep
/// still completes any candidate carrying a filing sidecar and runs every outbox recovery pass). A document
/// stranded in any other live state (Received, Transcribed, Matched, Extracted, Reviewed, Rotated) is re-enqueued
/// ONLY by the startup reconcile, because re-enqueuing live pipeline work mid-run would race the active workers. So
/// those states have no periodic recovery at all: a document that stops there sits until someone restarts the app,
/// and before this gauge the only way to notice was a human missing it.
///
/// WHAT COUNTS AS STALLED. The population is the STATE ARM of reconcile — <c>AuditQueryPredicates.ReconcileWhere</c>,
/// non-terminal AND not awaiting a human. That is deliberately NOT everything reconcile drives: its orphan-sidecar
/// arm also completes a <c>NeedsReview</c> document carrying a <c>.filing.json</c>, and that population is invisible
/// here by construction (human-pending, any age). A permanently failing orphan filing therefore never ages into this
/// gauge — since the D-2 fix it is counted by <c>SweepHealth.reconcileFailuresSinceStart</c> and alerted on by the
/// reconcile-doc-failures log-query rule instead; read those beside this. Two exclusions carry the weight:
///
///   • TERMINAL states (Filed, Discarded, Superseded, Segmented, SeededHistory) are settled by definition.
///     <c>SeededHistory</c> is listed for a reason that has not arrived yet: the C9 migrator transmutes every
///     non-terminal source row to it at seed time, so a future seed can mint rows that are old on arrival and have
///     no events at all. The live ledger currently holds NONE (measured 2026-08-11) — an earlier draft of this
///     docstring claimed "1,000+ of them, LastUpdated years old", which was invented, contradicted by a probe run
///     in the same session, and is recorded here so the exclusion is not later justified by a number nobody checked.
///
///   • HUMAN-PENDING states (NeedsReview, DeadLetter, DuplicateSuspect) are a QUEUE, not a stall. A document
///     waiting on a reviewer has not failed; the machine is not late. This exclusion is load-bearing and measured,
///     not aesthetic: on the live ledger (2026-08-11) 100 documents were human-pending, 87 of them older than a
///     week, against ZERO machine-owed documents older than fifteen minutes. Folding the two together would have
///     produced a gauge reading 100 on the day it shipped — permanently non-zero, therefore permanently ignored,
///     and the one real stall would have arrived as 101. Do not "simplify" the two populations back together.
///
/// WHAT IT DOES NOT DETECT, stated plainly so nobody mistakes a zero for an all-clear — a zero means "nothing that
/// exists is stuck", which is a much smaller claim than "the pipeline works":
///   • A DEAD PIPELINE WITH NOTHING IN FLIGHT reads zero, indefinitely. Nothing is stalled because nothing is
///     there. The liveness half of that question is <c>SweepHealth</c> (is the timer ticking) — read the two
///     together or you are reading half a signal.
///   • A DOCUMENT BEING RE-DRIVEN FOREVER. The reconcile arm for a "Filing" document whose intent sidecar is lost
///     calls ReEnqueueAsync, which appends "Received" — an ADVANCING event — and that arm is NOT gated on the
///     startup sweep, so such a document gets a fresh LastUpdated every couple of minutes and reads as healthy
///     while achieving nothing. The redrive bound meant to stop it is deliberately fail-open (an absent capture
///     store leaves attempts at 0), so this loop is unbounded. This gauge measures silence, not futility.
///     Note the CONTRAST, and its two exceptions, because an earlier draft had the dichotomy backwards and a later
///     one overstated it: a "Filing" document whose upload keeps FAILING with its sidecar intact appends nothing,
///     so its LastUpdated freezes and the gauge does catch it — silence is the detectable case there. But two
///     siblings in the same recovery block escape both halves: a crash between the Reviewed and Filed appends
///     re-enters recovery next tick and appends ANOTHER Reviewed (advancing — LastUpdated moves, so it reads as
///     noise and the gauge is blind), and the same failing filing on a NeedsReview document is silent AND
///     state-excluded — the third quadrant the old dichotomy never named, and where D-2 lived. Those two belong to
///     SweepHealth's failure counter, not to this gauge.
///   • ANY unrelated advancing append clears the age. A human rotating or re-reviewing a genuinely stuck document
///     silences this gauge without unsticking anything.
///   • A freeze inside a HUMAN-PENDING state, by construction (see the exclusion above). If an annotation-shaped
///     bug ever froze documents in NeedsReview, this would not see it.
///   • A state wrongly added to <see cref="DocumentStates"/>.Terminal — reconcile stops driving those documents
///     and this gauge stops watching them in the SAME edit. That is the cost of inheriting the reconcile
///     population, paid knowingly: the alternative is two lists that disagree about what "settled" means.
///   • Anything younger than its lane's threshold — two lanes since the dwell measurement split them: machine-owed
///     ACTIVE states age against StallGauge.Minutes (default 60), while Received, the one state that legitimately
///     queues, ages against StallGauge.QueuedMinutes (default 1440 — a full day of blindness there, the price of
///     not crying during a bulk intake). An earlier version of this bullet kept the 24-hour figure for ALL states
///     after the split had moved it to the queued lane only, overstating the blindness 24× for six of the seven.
///   • Anything about a TERMINAL document, including one filed to the wrong place. Settled is settled here.
///   • A document that never got a projection row at all (an append that failed before the store saw it). There is
///     no row to age.
///   • Correctness of any kind. A document advancing normally through a broken pipeline is invisible to this.
///
/// TWO KNOWN WAYS TO GET A NON-ZERO THAT IS NOT AN INCIDENT, both worth recognising before chasing one:
///   • A DEPLOYMENT THAT STOPS. Only the STARTUP reconcile re-enqueues live pipeline work, so everything in flight
///     when the process stopped stays frozen until it comes back. On a localhost box shut down overnight that is
///     normal; on the cloud instance it is not.
///   • AN ORPHANED INPUT — the one case that will sit here forever. <c>ReEnqueueAsync</c> returns false when the
///     document's inbox PDF is missing, with no dead-letter, so a machine-owed document that lost its input can
///     never be re-driven by any sweep. This gauge is the first thing that makes that visible; it does not fix it,
///     and until it is fixed such a document is a permanent +1.
/// </summary>
/// <param name="Count">Total machine-owed documents past the threshold. Zero is the expected steady state.</param>
/// <param name="ByState">Count per state, so the operator knows WHICH lane is stuck before opening the dashboard.
/// Non-PHI: these are the same compile-time state names already published in <c>byState</c>.</param>
/// <param name="Oldest">LastUpdated of the oldest stalled document, or null when Count is 0.</param>
/// <param name="OldestState">The state <see cref="Oldest"/> is sitting in, or null when Count is 0. Free (the
/// per-state groups are already read) and it changes the reading entirely: an age of two days in
/// <c>Received</c> is a queue that has not drained, the same age in <c>Extracted</c> is a document that fell out
/// of the pipeline — different cutoffs applied to each (a <c>Received</c> row cannot even appear here under 1440
/// minutes), and different things to go and do.</param>
/// <param name="Sample">Up to <see cref="SampleSize"/> stalled document ids, OLDEST FIRST — a pointer, not a
/// listing. It exists because there is otherwise no route from this number to the actual document: every projection
/// list endpoint orders <c>LastUpdated DESC</c>, which sorts the stalled ones to the very END of their state's page.
/// Telling an operator to "filter the dashboard by state" names a backstop that does not reach the case. Paste one
/// of these into <c>/api/documents/{id}/history</c> instead. Ids are opaque tokens, not PHI, and this endpoint is
/// visible to exactly the audience that can already list documents.</param>
public sealed record StalledDocuments(
    int Count,
    IReadOnlyDictionary<string, int> ByState,
    DateTimeOffset? Oldest,
    string? OldestState,
    IReadOnlyList<string> Sample)
{
    /// <summary>Hard cap on <see cref="Sample"/>. Small on purpose: enough to start an investigation, far too few to
    /// turn an operational endpoint into a second document-listing API.</summary>
    public const int SampleSize = 10;

    /// <summary>The all-clear, for stores/tests that need to express "nothing stalled" without a query.</summary>
    public static StalledDocuments None { get; } = new(0, new Dictionary<string, int>(), null, null, []);
}
