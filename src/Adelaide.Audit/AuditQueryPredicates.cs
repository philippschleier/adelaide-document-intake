using Adelaide.Core;

namespace Adelaide.Audit;

/// <summary>
/// Single source of truth for the state-exclusion sets the audit-store queue predicates are built from. BOTH stores
/// (<see cref="SqliteAuditStore"/> + <see cref="AzureSqlAuditStore"/>) compose their <c>WHERE</c> clauses from these
/// constants, so (a) the three predicates within a store cannot disagree about which states are terminal, and (b) the
/// two stores cannot drift from each other. This closes the gap that let a split parent (<c>Segmented</c>) linger in the
/// "Duplikat?" filter: when <c>Segmented</c> was introduced as a terminal state it was added to the active/reconcile
/// exclusions but MISSED on the duplicate one, so a terminal parent that still carried a <c>DuplicateOf</c> flag kept
/// satisfying the duplicate query. The state names are compile-time constants (never user input), so the rendered
/// <c>IN</c>-lists carry no injection surface.
/// </summary>
internal static class AuditQueryPredicates
{
    /// <summary>Terminal / resolved states: the document is done (<c>Filed</c>) or dispositioned — <c>Discarded</c>,
    /// <c>Superseded</c> (replaced by a newer doc; the old item is deleted), <c>Segmented</c> (the file fanned out
    /// into units, so its content now lives in the children), or <c>SeededHistory</c> (a C9 dedup-seed row imported
    /// from the localhost projection: it exists so the sha256/sourceRecordId dedup lookups can match history — it
    /// has no events, no inbox PDF, and often no driveItemId, so it must never surface as actionable work; the
    /// Migrator transmutes every non-terminal source state to it at seed time). None is active work: excluded from
    /// the active list AND from the open-duplicates filter.
    ///
    /// The set itself now lives in <see cref="DocumentStates.Settled"/> (Adelaide.Core) because the ENDPOINT
    /// re-entry guards need the identical list — add a new terminal state there and both the predicates below and
    /// the guards inherit it.</summary>
    internal static readonly string[] Settled = DocumentStates.Settled;

    /// <summary>Awaiting a human — review, a duplicate decision, or dead-letter triage. Owned by a reviewer, not the
    /// queue, so RECONCILE excludes these ON TOP of <see cref="Terminal"/> and never auto-re-enqueues them.</summary>
    internal static readonly string[] HumanPending = { "NeedsReview", "DeadLetter", "DuplicateSuspect" };

    /// <summary>Active list / "In Bearbeitung" (the caller appends ordering + limit). Excludes the terminal states.</summary>
    internal static readonly string ActiveWhere =
        " WHERE State NOT IN (" + InList(Settled) + ")";

    /// <summary>Startup reconcile: in-flight documents still owned by the queue — terminal AND human-pending excluded
    /// (a doc awaiting a human must never be auto-re-enqueued: a blacklist trap).</summary>
    internal static readonly string ReconcileWhere =
        " WHERE State NOT IN (" + InList(Settled.Concat(HumanPending)) + ")";

    /// <summary>States where a document is legitimately WAITING FOR A WORKER rather than being worked on. Intake is
    /// an in-memory channel drained by ADELAIDE_CONCURRENCY workers, so a bulk load parks its tail here for as long
    /// as the batch takes — or, on a box that stops overnight, until it comes back.
    ///
    /// This is one state because the data says it is one state. Measured over 65 days of the live ledger (9,264
    /// dwells: the gap between an event leaving a document machine-owed and its next advancing event):
    ///
    ///     Received     n=2393  max 806.1 min   1,394 dwells over 15 min
    ///     Transcribed  n=1637  max   0.7 min       0 dwells over 15 min
    ///     Filing       n= 999  max   0.5 min       0
    ///     Matched      n=1637  max   0.3 min       0
    ///     Extracted    n=1637  max   0.1 min       0
    ///     Rotated/Reviewed     max   0.0 min       0
    ///
    /// The whole tail belongs to one state and every other tops out under a minute, so a single cutoff sized to
    /// clear the queue would blind the rest by three orders of magnitude. Add a state here only with the same kind
    /// of evidence — a state wrongly listed gets the loose cutoff and hides real breakage for a day.</summary>
    internal static readonly string[] Queued = { "Received" };

    /// <summary>The stalled-document gauge: the RECONCILE population, aged, with a cutoff that depends on whether
    /// the document is queued or being worked on. Deliberately the same state set as <see cref="ReconcileWhere"/>
    /// and composed from it, because the gauge's question IS "has the queue moved the documents it owns?" — a
    /// document awaiting a human is a queue, not a stall, and folding the two together makes the gauge permanently
    /// non-zero (see <see cref="Adelaide.Core.StalledDocuments"/> for that measurement).
    ///
    /// A state not named in <see cref="Queued"/> takes the TIGHT cutoff, including one nobody has heard of. That is
    /// the fail-safe direction: an unrecognised state is far more likely to be new pipeline work than a new queue.
    ///
    /// The caller binds both cutoffs formatted the same way LastUpdated is written
    /// (<c>DateTimeOffset.ToString("o")</c>): fixed-width, always <c>+00:00</c>, so the range comparison is a plain
    /// lexicographic one on identically-shaped strings under both substrates' collations — the same assumption every
    /// <c>ORDER BY LastUpdated</c> in this file's callers already rests on.</summary>
    internal static readonly string StalledWhere = ReconcileWhere +
        " AND LastUpdated < CASE WHEN State IN (" + InList(Queued) + ") THEN @queuedBefore ELSE @activeBefore END";

    /// <summary>The "Duplikat?" filter (list + badge): an exact-byte suspect (<c>DuplicateSuspect</c>) OR any doc still
    /// carrying a <c>DuplicateOf</c> flag — minus the terminal/resolved states, because a doc that carried
    /// <c>DuplicateOf</c> and was then filed/discarded/superseded/segmented is RESOLVED, not open. Shares the exact
    /// terminal set with <see cref="ActiveWhere"/> so the two cannot disagree. A live <c>NeedsReview</c> doc carrying a
    /// <c>DuplicateOf</c> is INTENTIONALLY matched here (it also shows in Prüfung) — the reviewer's compare overlay.</summary>
    internal static readonly string DuplicateWhere =
        " WHERE (State='DuplicateSuspect' OR DuplicateOf IS NOT NULL) AND State NOT IN (" + InList(Settled) + ")";

    /// <summary>Render a state set as a SQL <c>IN</c>-list literal (<c>'A','B'</c>) — single-quoted, valid for both
    /// SQLite and SQL Server. Inputs are fixed constants, so there is no injection surface.</summary>
    private static string InList(IEnumerable<string> states) => string.Join(",", states.Select(s => $"'{s}'"));
}
