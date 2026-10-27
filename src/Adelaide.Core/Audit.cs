namespace Adelaide.Core;

/// <summary>What the application records about a document. The store stamps sequence, time, and chain hash.</summary>
public sealed record AuditEntry(
    string DocumentId,
    string Type,
    string Actor,
    string? UnitId = null,
    string? CorrelationId = null,
    string? Details = null,               // JSON; STRUCTURAL ONLY — opaque ids, content hashes, reason codes. Never filenames or prose (C6): the ledger is append-only, so whatever lands here can never be erased.
    ProjectionOverrides? Projection = null); // display values for the UPDATABLE projection only — never hashed, never stored in events

/// <summary>Display values routed to the updatable <c>documents</c> projection WITHOUT entering the append-only
/// event payload (migration plan C6). A store applies each non-null member exactly as if the corresponding key had
/// been parsed from <see cref="AuditEntry.Details"/> — same COALESCE keep-prior-on-null semantics — but the value
/// never reaches the hash chain. Consequence, by design: regenerating a projection from events alone yields a
/// name-free, prose-free projection; the projection row is the sole (erasable) holder of display text.
/// <see cref="Reasons"/> is the pre-joined display string (the projection column is one string, not an array); an
/// empty string deliberately CLEARS the prior value (re-entry events reset stale review reasons that way).</summary>
public sealed record ProjectionOverrides(
    string? OriginalName = null,
    string? FileName = null,
    string? SourceRecordId = null,
    string? Reasons = null);

/// <summary>A stored, immutable event in the append-only, hash-chained audit log.</summary>
public sealed record AuditEvent(
    long Seq,
    string DocumentId,
    string? UnitId,
    string Type,
    DateTimeOffset At,
    string Actor,
    string? CorrelationId,
    string Details,
    string Hash);

/// <summary>One row per document — the readable projection over its event stream.</summary>
public sealed record DocumentAudit(
    string DocumentId,
    string State,
    string? OriginalName,
    string? FileName,
    string? ContentType,
    int? PatientListItemId,
    string? Disposition,
    string? Reasons,
    string? Sha256,        // content hash of the captured original (exact-duplicate detection / idempotency key)
    string? DuplicateOf,   // documentId this one is suspected to duplicate (set by the dedup layers)
    DateTimeOffset FirstSeen,
    DateTimeOffset LastUpdated,
    int EventCount,
    string? SourceRecordId = null,  // origin's immutable file identity (at-least-once drain channel idempotency key); null for interactive uploads
    string? ReasonCodes = null,     // raw JSON array of the latest NeedsReview event's B2 gate codes (PHI-safe structural tags); null for events that predate persistence
    double? Confidence = null,      // gate identity/classification confidence 0..1 from the NeedsReview event; null if absent
    string? DriveItemId = null,     // SharePoint driveItem id from the Filed event — the durable filed-PDF locator; null for docs filed before it was recorded
    string? CorrectedFields = null, // Tier A: JSON array of review-corrected field NAMES (PHI-free) from the Reviewed event. null = not reviewed; "[]" = reviewed, no corrections; ["documentDate",…] = those fields edited
    string? ContentTypeChange = null); // Tier A: JSON {from,to} of a review type-correction (non-PHI category labels); null unless the type was corrected — the classification confusion matrix

/// <summary>Result of recomputing the audit hash chain end-to-end: intact + the first broken Seq (0 if none) +
/// the head {seq, count, hash}. The head triple is the anchor checkpointed against an external reference (see
/// AUDIT-INTEGRITY-SPEC.md); count is the truncation defense — seq alone can't catch a renumbered truncation.</summary>
public sealed record ChainVerification(bool Intact, long EventCount, long HeadSeq, string HeadHash, long FirstBrokenSeq);

/// <summary>
/// Append-only, hash-chained audit log (event sourcing + a CQRS projection). Events are immutable —
/// corrections are recorded as new events (GoBD / §630f BGB alignment). Implementations swap behind this
/// seam: SQLite for dev/small scale, Azure SQL ledger tables for production tamper-evidence.
/// </summary>
public interface IAuditLog
{
    /// <summary>Append one immutable event; the store assigns Seq, timestamp, and the chain hash.</summary>
    Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>The one-row-per-document projection (current state + filing facts), or null if unseen.</summary>
    Task<DocumentAudit?> GetDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>Recent documents (most-recently-updated first) for the tracking dashboard.</summary>
    Task<IReadOnlyList<DocumentAudit>> ListDocumentsAsync(int limit = 5000, CancellationToken ct = default);

    /// <summary>The full event stream for a document, in order.</summary>
    Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string documentId, CancellationToken ct = default);

    /// <summary>Recompute the hash chain end-to-end. Reports intact + the first broken Seq (0 if none) + the head
    /// {seq, count, hash}. A pass proves INTERNAL CONSISTENCY (no accidental corruption / casual edit), NOT that the
    /// chain is untampered — that needs an external anchor (see AUDIT-INTEGRITY-SPEC.md).</summary>
    Task<ChainVerification> VerifyChainAsync(CancellationToken ct = default);
}

/// <summary>
/// The full audit-store surface the application consumes: the <see cref="IAuditLog"/> append+read seam plus the
/// projection queries (dashboard lists, counts) and the dedup/idempotency lookups. Implementations swap behind this one
/// interface — <c>SqliteAuditStore</c> for dev/small scale, <c>AzureSqlAuditStore</c> (ledger tables) for production —
/// so DI and every endpoint depend on the seam, never a concrete store. Append semantics (Seq/hash chain) are identical
/// across implementations, so the chain verifies the same way and the SQLite history migrates byte-for-byte.
/// </summary>
public interface IAuditStore : IAuditLog
{
    /// <summary>Actionable + in-flight documents (not terminal Filed/Discarded), human-actionable states clustered first.</summary>
    Task<IReadOnlyList<DocumentAudit>> ListActiveAsync(int limit = 300, CancellationToken ct = default);

    /// <summary>In-flight documents still owned by the queue (startup reconciliation), oldest first; uncapped by default.</summary>
    Task<IReadOnlyList<DocumentAudit>> ListReconcilableAsync(int limit = 100_000, CancellationToken ct = default);

    /// <summary>Recent documents in a single state — the dashboard's per-state filter.</summary>
    Task<IReadOnlyList<DocumentAudit>> ListByStateAsync(string state, int limit = 5000, CancellationToken ct = default);

    /// <summary>Open possible-duplicates for the dashboard's "Duplikat?" filter: exact-byte suspects (DuplicateSuspect)
    /// AND business-key flags (any non-terminal doc carrying a DuplicateOf), most-recent first — the two dedup layers land
    /// in different states (exact → DuplicateSuspect; business-key → NeedsReview with DuplicateOf), so this spans them.</summary>
    Task<IReadOnlyList<DocumentAudit>> ListDuplicatesAsync(int limit = 5000, CancellationToken ct = default);

    /// <summary>Count of open possible-duplicates (the "Duplikat?" chip badge) — matches <see cref="ListDuplicatesAsync"/>.</summary>
    Task<int> CountDuplicatesAsync(CancellationToken ct = default);

    /// <summary>Document counts grouped by state — the dashboard summary.</summary>
    Task<IReadOnlyDictionary<string, int>> CountsByStateAsync(CancellationToken ct = default);

    /// <summary>Machine-owed documents whose last ADVANCING event predates their cutoff — the stalled-document
    /// gauge. TWO cutoffs, because the populations differ by three orders of magnitude: a document being WORKED ON
    /// (<paramref name="activeOlderThan"/>) has never taken more than a minute, while one QUEUED behind the intake
    /// channel legitimately waits hours. One number would either blind the first or cry over the second — see
    /// <c>AuditQueryPredicates.Queued</c> for the measurement.
    ///
    /// The caller supplies both, so the thresholds stay deployment policy and the store stays a query. See
    /// <see cref="StalledDocuments"/> for what the population is and, more importantly, what a zero does not
    /// prove.</summary>
    Task<StalledDocuments> CountStalledAsync(DateTimeOffset activeOlderThan, DateTimeOffset queuedOlderThan,
        CancellationToken ct = default);

    /// <summary>Most recent non-discarded document with the same content hash (exact-duplicate detection).</summary>
    Task<DocumentAudit?> FindBySha256Async(string sha256, string excludeDocumentId, CancellationToken ct = default);

    /// <summary>Most recent ALREADY-FILED document with the same content hash (filing-time exact-duplicate guard).</summary>
    Task<DocumentAudit?> FindFiledBySha256Async(string sha256, string excludeDocumentId, CancellationToken ct = default);

    /// <summary>Most recent non-discarded document with the same origin SourceRecordId (at-least-once drain idempotency).</summary>
    Task<DocumentAudit?> FindBySourceRecordIdAsync(string sourceRecordId, string excludeDocumentId, CancellationToken ct = default);

    /// <summary>A FILED document whose canonical filename starts with the business-key prefix (same patient/type/date).</summary>
    Task<DocumentAudit?> FindFiledByFileNamePrefixAsync(string prefix, string excludeDocumentId, CancellationToken ct = default);

    /// <summary>Most recent document whose events mention the needle in Details (e.g. an original filename).
    /// Post-C6 events carry no names — for name/path lookups prefer <see cref="FindDocumentIdByNameAsync"/> and use
    /// this only as the pre-C6-history fallback.</summary>
    Task<string?> FindDocumentIdByDetailAsync(string needle, CancellationToken ct = default);

    /// <summary>Most recent document whose PROJECTION display columns (OriginalName / FileName / SourceRecordId)
    /// contain the needle — the authoritative name lookup since C6 moved names out of event payloads. Unbounded
    /// (SQL-side LIKE), unlike an in-memory scan of the recency-capped document list.</summary>
    Task<string?> FindDocumentIdByNameAsync(string needle, CancellationToken ct = default);

    /// <summary>Distinct documents with at least one event of <paramref name="type"/> after <paramref name="since"/> —
    /// the capture-health denominator ("processed since the capture store's epoch"). Distinct by document, because a
    /// re-processed document still yields one capture row.</summary>
    Task<int> CountDocumentsWithEventSinceAsync(string type, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Distinct documents whose Filed event carries disposition='ReviewedThenFiled' after
    /// <paramref name="since"/> — the review-lane label EXPECTATION: each of these attempted a corpus label, so
    /// (this − labels landed) is the capture-health gap. Auto-filed documents are excluded by definition: their rows
    /// are unlabeled BY DESIGN (Outcome=AUTOFILED) and must never count against label health.</summary>
    Task<int> CountReviewedThenFiledSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}
