using Adelaide.Core;

namespace Adelaide.Audit;

/// <summary>One captured model call, read back. PHI (bodies) — for tests, erasure verification, and replay tooling.</summary>
public sealed record ModelCallReadback(long Id, string DocumentId, string CallSite, int Attempt, DateTimeOffset At,
    string? Deployment, string? ServedModel, string? SystemFingerprint, string? PromptHash, string? ReasoningEffort,
    string? RequestBody, string? RenderParams, string? SourcePdfSha256, string? ResponseBody,
    string? OutputSha256, long? OutputBytes, string? OutputRef,
    int? PromptTokens, int? CompletionTokens, int? ReasoningTokens, long LatencyMs, string? RequestId, int? HttpStatus, string? Error);

/// <summary>One extraction-time measurement row per field (non-PHI: name/flags/confidence, never values).</summary>
public sealed record CorpusFieldRow(string FieldName, bool Proposed, double? Confidence, string? Source);

/// <summary>One measurement row per field, read back (non-PHI).</summary>
public sealed record CorpusFieldReadback(string FieldName, bool Proposed, double? Confidence, string? Source, string? Outcome, DateTimeOffset? OutcomeAt);

/// <summary>One adjudicated value pair (PHI side: the proposed and final VALUES).</summary>
public sealed record FieldAdjudicationRow(string FieldName, string? ProposedValue, string? FinalValue, string Signal, string? Provenance);

/// <summary>
/// The capture-store seam (docs/CAPTURE.md; migration plan C3). Two implementations with identical semantics:
/// <see cref="TranscriptStore"/> (SQLite — localhost and the explicit escape hatch) and
/// <see cref="AzureSqlTranscriptStore"/> (the cloud substrate: <c>capture.*</c> content schema + <c>measure.*</c>
/// measurement schema inside the audit database, keyless Active Directory Default, schema provisioned once by an
/// admin via infra/sql/capture-init.sql — the app has data read/write only, never DDL).
///
/// Contract invariants both implementations MUST keep (pinned by TranscriptStoreContractTests):
///   • upserts by DocumentId; corpus upserts never clear Label/LabelDomain/LabelAt (the label-preserving pattern);
///   • corpus_fields upserts never clear Outcome/OutcomeAt; adjudication upserts never clear a recorded Provenance;
///   • model-call rows are append-only, attempt-numbered per (DocumentId, CallSite) when the caller passes 0;
///   • DeleteAsync erases CONTENT only (transcripts, artifacts, model calls, adjudications) and deliberately KEEPS
///     the attempt marks — those are the redrive bound's safety counter, not content; ForgetAsync additionally
///     removes the measurement rows AND the attempt marks (document-terminal); NEITHER touches the erasure log;
///   • PurgeOlderThanAsync ages out content + attempt marks only — never corpus/corpus_fields (plan C2), never the
///     erasure log.
/// </summary>
public interface ITranscriptStore
{
    Task PutAsync(string documentId, string transcript, string sha256, DateTimeOffset at, CancellationToken ct = default);
    Task<(string Transcript, string Sha256, DateTimeOffset At)?> GetAsync(string documentId, CancellationToken ct = default);
    Task PutArtifactsAsync(string documentId, string? pageMarkdownJson, string? barcodePayloadsJson, string? checkboxEvidence, DateTimeOffset at, CancellationToken ct = default);

    Task PutModelCallAsync(ModelCallRecord r, CancellationToken ct = default);
    Task<IReadOnlyList<ModelCallReadback>> GetModelCallsAsync(string documentId, CancellationToken ct = default);
    Task<int> ModelCallCountAsync(CancellationToken ct = default);

    Task MarkAttemptStartAsync(string documentId, DateTimeOffset at, CancellationToken ct = default);
    Task<int> AttemptStartsSinceAsync(string documentId, DateTimeOffset since, CancellationToken ct = default);

    Task PutCorpusAsync(CorpusRow row, CancellationToken ct = default);
    Task PutCorpusFieldsAsync(string documentId, IEnumerable<CorpusFieldRow> rows, DateTimeOffset at, CancellationToken ct = default);
    Task SetFieldOutcomesAsync(string documentId, IReadOnlyList<(string FieldName, string Outcome)> outcomes, string? remainingOutcome, DateTimeOffset at, CancellationToken ct = default);
    Task<IReadOnlyList<CorpusFieldReadback>> GetCorpusFieldsAsync(string documentId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> CorpusFieldOutcomeCountsAsync(CancellationToken ct = default);
    Task PutAdjudicationsAsync(string documentId, IEnumerable<FieldAdjudicationRow> rows, DateTimeOffset at, CancellationToken ct = default);
    Task<IReadOnlyList<FieldAdjudicationRow>> GetAdjudicationsAsync(string documentId, CancellationToken ct = default);

    Task<bool> SetLabelAsync(string documentId, string? label, string? labelDomain, string? driveItemId, DateTimeOffset labelAt, CancellationToken ct = default);
    Task<IReadOnlyList<(string DocumentId, string Label, string? DriveItemId)>> ScoreableSinceAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<IReadOnlyList<(string Label, int Count)>> LabelCountsAsync(CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>When capture began, by the store's own account — SQLite: the store file's creation instant (the
    /// CorpusGuardTests convention); Azure SQL: the earliest corpus row's At. Null when there is no history yet;
    /// the caller denominates from now in that case.</summary>
    Task<DateTimeOffset?> EpochAsync(CancellationToken ct = default);
    Task<int> CorpusRowCountAsync(CancellationToken ct = default);
    Task<int> LabeledRowCountAsync(CancellationToken ct = default);
    Task<int> AdjudicationCountAsync(CancellationToken ct = default);

    /// <summary>Erase one document's CONTENT: transcript, artifacts, model calls, adjudicated values. The measurement
    /// rows survive (plan C2) and so do the attempt marks — those are the redrive bound's safety counter, not capture
    /// content, and clearing them would restore an unbounded crash budget to a document that is still processable.</summary>
    Task<bool> DeleteAsync(string documentId, CancellationToken ct = default);

    /// <summary>Remove the document entirely: everything <see cref="DeleteAsync"/> removes, plus the measurement rows
    /// and the attempt marks. Document-terminal, which is why clearing the bound is safe here and nowhere else.</summary>
    Task<bool> ForgetAsync(string documentId, CancellationToken ct = default);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Record an erasure act (scope 'content' | 'full') in the measurement-side erasure log. Written by the
    /// erasure surface AFTER the store operation; never removed by any erasure or purge path — it is the record OF them.</summary>
    Task LogErasureAsync(string documentId, string scope, string actor, DateTimeOffset at, CancellationToken ct = default);
}
