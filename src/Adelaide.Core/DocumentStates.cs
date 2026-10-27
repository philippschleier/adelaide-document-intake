namespace Adelaide.Core;

/// <summary>The TERMINAL / resolved document states — ONE source of truth for two consumers that must never
/// disagree: the store query predicates (<c>AuditQueryPredicates</c>, which renders these into the active /
/// reconcile / duplicate WHERE clauses) and the endpoint RE-ENTRY guards (<c>/api/review</c>, <c>/api/split</c>).
///
/// A terminal document is settled. Driving one back into a live state does not merely duplicate work: the append
/// lands OVER the terminal event, and because the projection sets State from the newest event unconditionally, the
/// row flips to a self-consistent-looking live state whose only trace of the truth is the older terminal event
/// (plus, for a supersede, the successor still pointing <c>supersedes</c> at it). That is why the guards use this
/// set rather than each endpoint's own historical one-state check — the review/split guards previously tested only
/// their own state (Filed, Segmented), leaving a re-file/re-split path open on Superseded and Discarded documents
/// wherever the review sidecar survives (the whole purge-OFF posture).
///
/// Add a new terminal state HERE and the store queries, the endpoint re-entry guards (review/split/rotation PUT),
/// reconcile's re-read gate, the sidecar reaper, and the rotate moot guard all inherit it (the failure-composition
/// round made the last four true — they were hand-written copies that had already drifted). Two consumers
/// deliberately do NOT inherit: the C5 aged-purge switch maps each terminal state to its own RELEASE CONDITION and
/// defaults an unmapped state to keep (fail-safe — add a branch only when the new state's inputs may actually be
/// released), and tools/Adelaide.Migrate holds a copy it declares must stay in sync.</summary>
public static class DocumentStates
{
    public static readonly string[] Settled = { "Filed", "Discarded", "Superseded", "Segmented", "SeededHistory", "Purged" };

    /// <summary>The SEALED states — the strongest form of settled: a sealed document accepts no further
    /// events, no exceptions, no carve-outs. Sole member today: <c>Purged</c>, the post-recovery-window end
    /// state of a discarded document (its artifacts are released; only ledger history and the projection row
    /// remain). Membership here is the contract the store-level append guard enforces (discard-recovery
    /// Phase 2). A sealed state MUST also be in <see cref="Settled"/> — every query and guard that ignores
    /// settled documents (the startup reconcile above all) must ignore sealed ones, or a sealed row would be
    /// re-driven as in-flight work; the test suite pins the subset relation.</summary>
    public static readonly string[] Sealed = { "Purged" };

    /// <summary>Ordinal, like every other state comparison in the codebase (states are compile-time constants,
    /// never user input, and the SQL predicates compare them exactly).</summary>
    public static bool IsSettled(string? state) =>
        state is not null && Array.Exists(Settled, s => string.Equals(s, state, StringComparison.Ordinal));

    /// <summary>Ordinal, like <see cref="IsSettled"/>.</summary>
    public static bool IsSealed(string? state) =>
        state is not null && Array.Exists(Sealed, s => string.Equals(s, state, StringComparison.Ordinal));
}
