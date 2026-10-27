namespace Adelaide.Core;

/// <summary>
/// Which audit event types ADVANCE a document's lifecycle state, and which are ANNOTATIONS.
///
/// The projection derives <c>documents.State</c> from the latest event's <c>Type</c>. That makes every append a state
/// transition, which in turn makes some true facts unrecordable: a deferred side-effect completing (a reclassify or
/// supersede delete that only lands on a later reconcile sweep) is a fact about a document, not a change to where it
/// is in its lifecycle. Recording it as an ordinary event would reset the document's state — a late "Received" landing
/// after it had already progressed to Transcribed or Filed would silently re-run the pipeline.
///
/// An ANNOTATION is appended to the hash chain exactly like any other event — same Seq, same PrevHash, same
/// tamper-evidence, and it counts toward EventCount — but the projection leaves <c>State</c> alone. Every other
/// projection column already has this keep-prior-on-null discipline; State was the one that could not abstain.
///
/// HAZARD, stated plainly: a type wrongly listed here makes documents of that type STOP ADVANCING — with no error and
/// no dead-letter, because the projection simply keeps the previous state forever. That is the mirror of the bug this
/// mechanism was built to fix. So the set is tiny, explicit and enumerated — never a prefix convention, never a
/// default, never inferred. Add to it only for events that genuinely record something ABOUT a document rather than
/// something that HAPPENED TO its lifecycle, and add a store test alongside.
///
/// This docstring used to end that sentence with "silently". It is no longer silent for QUEUE-OWNED documents:
/// <see cref="StalledDocuments"/> counts non-terminal, non-human-pending documents whose LastUpdated has stopped
/// moving, and an annotation deliberately does not move it — so a wrongly-listed type surfaces there within the
/// stall threshold. The word still applies to documents parked in a HUMAN-PENDING state (NeedsReview, DeadLetter,
/// DuplicateSuspect): the gauge excludes those on purpose, so a freeze that happens there is still invisible.
/// </summary>
public static class AuditEventTypes
{
    /// <summary>A deferred SharePoint item removal that completed on a later sweep. The inline path records its
    /// outcome on the event that carries it (`sharePointItemRemoved`); this exists for the case where the delete
    /// failed inline and the outbox re-drove it afterwards, which no event could otherwise state.</summary>
    public const string SharePointItemRemoved = "SharePointItemRemoved";

    /// <summary>The complete, closed set of non-advancing types. Deliberately enumerated one by one.</summary>
    private static readonly HashSet<string> NonAdvancing = new(StringComparer.Ordinal)
    {
        SharePointItemRemoved,
    };

    /// <summary>True when this event type sets the document's projected State. False ONLY for annotations, whose
    /// append leaves State untouched. Unknown types advance — the safe default, since a type nobody listed here is
    /// far more likely to be a new lifecycle step than a new annotation.</summary>
    public static bool Advances(string type) => !NonAdvancing.Contains(type);
}
