namespace Adelaide.Core;

public enum SourceChannel { Email, Sftp, Fax, Api, Drain, Unknown }

// Ledger state machine (see ARCHITECTURE.md §8) — every document is always in exactly one state.
// "Filing" is the outbox intent: recorded (atomically — the event IS the state) right before the SharePoint upload, so a
// crash between the upload and "Filed" leaves the doc here, and reconcile verifies against SharePoint to resolve it.
// Separated is the legacy nominal name; Segmented is the spec-authoritative FILE-terminal state (the file fans out into
// units — see the splitting spec §3.6). Both are container/file states; a UNIT then carries Extracted…Filed/NeedsReview.
public enum ProcessingState { Received, Stored, Transcribed, Separated, Segmented, Extracted, Matched, Filing, Filed, NeedsReview, DeadLetter }

public enum Disposition { AutoFile, Review, DeadLetter }

public enum MatchOutcome { Single, None, Multiple }

/// <summary>One physical inbound file (already persisted to durable storage). <see cref="SourceRecordId"/> is the
/// origin's immutable identity for the file (e.g. the drain's share-relative path) — the idempotency key for an
/// at-least-once channel; null for channels that dedup on content hash alone (the interactive browser upload).</summary>
public record IncomingDocument(string DocumentId, string SourcePath, SourceChannel Channel, string? OriginalFileName, string? SourceRecordId = null);

/// <summary>A decoded barcode. <see cref="RawText"/> is the FULL payload — for a Muster 12 BFB barcode that is the
/// whole prescription (name, DOB, KVNR, diagnoses, measures), i.e. PHI. Never serialise it to console, HTTP responses,
/// or audit details; consume it only via KVNR extraction / BfbBarcode parsing.</summary>
public record DecodedBarcode(string Symbology, string RawText, bool ChecksumValid);

/// <summary>Transcription output: Markdown + provenance + a quality score for the review gate.
/// <see cref="PageMarkdown"/> is the SAME transcript split one-entry-per-page (in order, length == <see cref="PageCount"/>)
/// when the transcriber could page-delimit reliably; <see cref="Markdown"/> is its join. It is the slice primitive for
/// segmentation: a split/guard hands each unit ONLY its own pages' text (skipping a re-transcription vision call). Null
/// when unavailable (older transcripts, or a page-count mismatch) — callers MUST fall back to re-transcribing.</summary>
/// <para><see cref="ModelDeployment"/> is the deployment we ASKED for; <see cref="ModelServed"/> is the model id the API
/// says actually ran (name + version, e.g. <c>gpt-4o-2024-11-20</c>) — a deployment can be repointed at a new version
/// without any code change, so only the served id answers "which model version produced this". All four provenance
/// fields are null on transcripts that did not come from a live model call (a reused parent slice, a test double).</para>
/// <para><see cref="UnparsedFields"/> names the response fields the parser could NOT read (absent, or present with the
/// wrong JSON type) and therefore substituted a fallback for — so <see cref="QualityScore"/> is a guess, not a reading.
/// PHI-safe: field NAMES only. Null/empty is the normal case. See <see cref="ReasonCodes.ModelFieldUnparsed"/>.</para>
/// <para><see cref="ReasoningEffort"/> is the effort actually SENT — null on a model with no such parameter. It is a
/// behavioural setting: moving it from minimal to medium changes what the model does and what it costs, so a later
/// shift in results is only attributable if the value that produced them was recorded alongside the model id.</para>
public record Transcript(string Markdown, int PageCount, double QualityScore, IReadOnlyList<DecodedBarcode> Barcodes,
    IReadOnlyList<string>? PageMarkdown = null,
    string? ModelDeployment = null, string? ModelServed = null, string? ModelFingerprint = null, string? PromptHash = null,
    IReadOnlyList<string>? UnparsedFields = null,
    string? ReasoningEffort = null);

/// <summary>An inclusive 1-based page range within the source file.</summary>
public record PageRange(int First, int Last)
{
    public override string ToString() => First == Last ? First.ToString() : $"{First}-{Last}";
}

/// <summary>How a unit was formed — recorded so any boundary is explainable WITHOUT replaying AI, and replay is
/// deterministic (splitting spec §3.5). BoundaryEvidence holds PHI-safe machine codes (which signals/constraints fired).</summary>
public record UnitProvenance(
    string SourceFileHash,
    PageRange PageRange,
    string SeparatorVersion,
    string DecisionPolicyVersion,
    IReadOnlyList<string> BoundaryEvidence,
    string? VisionDecisionId = null);

/// <summary>Lineage relations (splitting spec §3.5). SplitFrom = the parent file's unit id for a page-split child
/// (manual or automatic); PartOf/DerivedFrom = embedded sub-doc (scenario A); AssembledFrom = stitched fragments (C).
/// Null until the relevant phase materializes them.</summary>
public record UnitRelations(
    string? SplitFrom = null,
    string? PartOf = null,
    string? DerivedFrom = null,
    IReadOnlyList<string>? AssembledFrom = null);

/// <summary>A logical sub-document after separation (1..N per IncomingDocument). <see cref="SourcePath"/> points
/// back to the original file so checkbox-heavy types can be re-analysed for selection marks during extraction.
///
/// The three keys (splitting spec §3.4, each guarding a different property — never collapse them):
///   • <see cref="DerivationKey"/> = hash(sourceFileHash, pageRange, separatorVersion) — REPLAY CONVERGENCE (a replayed
///     job reproduces the same key and reuses the existing UnitId, never double-creating).
///   • <see cref="UnitId"/> — STABLE identity, assigned once at materialization; a human boundary correction never changes it.
///   • <see cref="BusinessContentHash"/> = hash(business fields, e.g. KVNR+type+date) — CROSS-ARRIVAL DEDUP.
/// All new fields are OPTIONAL: the N=1 zero-churn path builds <c>new DocumentUnit(fileDocId, 1, N, transcript)</c>
/// unchanged (UnitId = fileDocId); the segmenter populates the keys/provenance at materialization, Relations per phase.</summary>
public record DocumentUnit(
    string UnitId,
    int FirstPage,
    int LastPage,
    Transcript Transcript,
    string? SourcePath = null,
    string? ContentSha = null,
    string? DerivationKey = null,
    string? BusinessContentHash = null,
    UnitProvenance? Provenance = null,
    UnitRelations? Relations = null);

/// <summary>Deterministic derivation of the two structural keys (splitting spec §3.4 + build-auth D1). Identity is the
/// DerivationKey, NOT the ordinal: on replay you look up by DerivationKey and REUSE the existing UnitId — never recompute
/// ordinals on reprocess.</summary>
public static class UnitKeys
{
    /// <summary>hash(sourceFileHash, pageRange, separatorVersion) — the same (file, range, version) always yields the
    /// same key, so a replayed job converges and does not double-create.</summary>
    public static string DerivationKey(string sourceFileHash, PageRange range, string separatorVersion)
        => Sha256Hex($"{sourceFileHash}|{range.First}-{range.Last}|{separatorVersion}");

    /// <summary>UnitId for the n-th unit (1-based, stable page order). N=1 → the file docId itself (zero-churn for the
    /// 99%); a split → <c>{fileDocId}-u{n}</c>. The ordinal is a cosmetic display suffix; identity is the DerivationKey.</summary>
    public static string UnitId(string fileDocId, int unitOrdinal, int unitCount)
        => unitCount <= 1 ? fileDocId : $"{fileDocId}-u{unitOrdinal}";

    private static string Sha256Hex(string s)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>An extracted field with per-field confidence and provenance (barcode|markdown|vision).</summary>
public record ExtractedField(string Name, string? Value, double Confidence, string Source);

/// <summary>Extracted fields plus the provenance needed to attribute a later change in extraction quality.
/// <see cref="ModelDeployment"/>/<see cref="ModelServed"/> mirror <see cref="Transcript"/>; <see cref="PromptHash"/>
/// identifies the prompt TEMPLATE (see <see cref="PromptHash"/>), so a metric shift can be pinned on the model or the
/// prompt rather than being un-attributable when both change in the same window.</summary>
/// <para><see cref="UnparsedFields"/> mirrors <see cref="Transcript.UnparsedFields"/>: response fields whose value could
/// not be read, so the confidence attached to them is a fallback rather than the model's own number. Field NAMES only.</para>
/// <para><see cref="CheckboxEvidence"/> is the "[X] label" block that was fed to the field extractor — from the BFB
/// barcode when one decoded, else from Document Intelligence selection marks. Surfaced so it can be RETAINED: it is a
/// model INPUT, and without it a DI or decoder upgrade has nothing to compare against. PHI (the labels are document
/// text), so it is stored beside the transcript, never on the ledger.</para>
public sealed record ExtractedData(IReadOnlyList<ExtractedField> Fields, int? ModelSeed = null, string? ModelFingerprint = null,
    string? ModelDeployment = null, string? ModelServed = null, string? PromptHash = null,
    IReadOnlyList<string>? UnparsedFields = null,
    string? CheckboxEvidence = null,
    string? ReasoningEffort = null)
{
    public ExtractedField? Get(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    public string? Value(string name) => Get(name)?.Value;
    public double MinConfidence() => Fields.Count == 0 ? 0d : Fields.Min(f => f.Confidence);
}

// Identity from the "Patients" list. ListItemId is the SharePoint item id used to set the document's Patient lookup.
public record PatientRef(int ListItemId, string Kvnr, string LastName, string FirstName, DateOnly? BirthDate, string Status, string? KlientId = null, string? PatientNr = null);

/// <summary>Candidates carries the collision set on <see cref="MatchOutcome.Multiple"/> so the review screen can
/// present the contenders directly instead of making the reviewer re-search what the matcher already found.</summary>
public record PatientMatch(MatchOutcome Outcome, PatientRef? Patient, string Method, IReadOnlyList<PatientRef>? Candidates = null);

public record ProcessingResult(
    string UnitId,
    Disposition Disposition,
    IReadOnlyList<string> Reasons,
    ExtractedData Data,
    PatientMatch Match,
    double OverallConfidence,
    // B2: structural condition tags PARALLEL to Reasons — PHI-safe machine codes (e.g. "not_allowlisted",
    // "missing_required:PrescriptionOrder", "duplicate_suspect"). The human prose stays in Reasons; codes are for logs/metrics.
    IReadOnlyList<string>? ReasonCodes = null);
