namespace Adelaide.Core;

/// <summary>The reason-code vocabulary: the structural, PHI-safe tags that say WHY a document did not auto-file.
/// (The reviewer-facing German prose lives alongside them in <c>reasons</c>; these are the machine-readable half.)
///
/// NOTE ON ADOPTION: the codes below are emitted as inline string literals at their call sites — mostly
/// <see cref="IntakePipeline.Decide"/>, plus a few in the web host. This class is the registry of that vocabulary,
/// not yet its single source of truth; only <see cref="RateLimited"/> is referenced by its constant today. Treat a
/// mismatch between a literal and the constant here as a bug in the literal, and prefer these constants at new sites.
/// </summary>
public static class ReasonCodes
{
    // ── Quality / transcription ──
    public const string LowQuality = "low_quality";                       // transcript legibility below PipelineOptions.MinQuality

    /// <summary>The model call was THROTTLED (HTTP 429) rather than producing a bad answer.
    ///
    /// Distinguishing this matters most across a model migration: a new deployment carries its own TPM quota, so
    /// throttling is the expected first symptom of an under-provisioned target. Untagged, those failures are
    /// indistinguishable from a genuine quality regression in exactly the window where that distinction decides
    /// whether to roll back. Emitted on the DeadLetter event; it does not by itself change routing.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>A REQUIRED numeric field was missing from the model's JSON response, or was present but not a number
    /// (e.g. the string "0.9"), so the parser substituted its fallback rather than reading a real value.
    ///
    /// Why this is its own code: the fallback is 0.5, which sits below the 0.60 legibility floor AND below both 0.80
    /// gates. So an unparseable response routes to review and escalates classification — exactly the same footprint as
    /// a genuine quality collapse, and previously indistinguishable from one. Across a model migration that is the
    /// difference between "the new model is worse" and "the new model renamed a field": the first justifies a rollback,
    /// the second is a one-line parser fix. The document still routes to review (fail-safe is unchanged); this code
    /// only makes the CAUSE legible. The accompanying log line names the field that failed to parse.</summary>
    public const string ModelFieldUnparsed = "model_field_unparsed";

    /// <summary>The model returned HTTP 200 with EMPTY content because its completion budget ran out before an answer
    /// was produced — on a reasoning model, because reasoning tokens consumed it.
    ///
    /// Distinct from <see cref="ModelFieldUnparsed"/>, which means a response arrived and one FIELD could not be read.
    /// Here there is no response to parse at all, and the cause is a BUDGET that is too small for the configured
    /// reasoning effort — a configuration error, not a document problem and not a quality regression. Retrying
    /// identically will fail identically, so it must be distinguishable at a glance.
    ///
    /// Measured 2026-08-06: at the pre-fix 200-token classify budget, gpt-5-mini spent all 200 tokens reasoning and
    /// returned an empty string. Unguarded, that reaches ParseJson and throws on every single document.</summary>
    public const string ModelEmptyResponse = "model_empty_response";

    /// <summary>The transcript carries too little text to act on, and no barcode supplies the content independently
    /// (see <see cref="TranscriptQuality"/>). Its own code because legibility does NOT catch this: document
    /// 117203e499e9 reported 0.9 legibility across six pages with an empty transcript, cleared the quality floor, and
    /// reached a human only indirectly via classification failing on empty text — with a different type guess it would
    /// have filed an empty document. Escalates to review; never auto-files.</summary>
    public const string EmptyTranscript = "empty_transcript";

    // ── Patient identity / matching ──
    public const string KvnrCheckDigit = "kvnr_checkdigit";
    public const string NoMatchNoKvnr = "no_match_no_kvnr";
    public const string NoMatchInvalidKvnr = "no_match_invalid_kvnr";
    public const string NoMatchUnmatchedKvnr = "no_match_unmatched_kvnr";
    public const string AmbiguousKvnr = "ambiguous_kvnr";
    public const string AmbiguousNameDob = "ambiguous_name_dob";
    public const string LowConfidence = "low_confidence";

    // ── Domain (medical vs business, see DocumentDomain) ──
    /// <summary>The domain classifier proposed the BUSINESS lane. Always routes to review: a business document has no
    /// patient and no catalog type, and the reviewer still has to supply the Zielbereich (HR/Finance/unklar) that the
    /// filing target depends on. The proposal is a routing hint the human confirms — never an auto-file decision.</summary>
    public const string BusinessDomain = "business_domain";

    // ── Classification ──
    public const string UnclassifiedType = "unclassified_type";
    public const string ClassifySelfCorrected = "classify_self_corrected";
    public const string ClassifyUnverified = "classify_unverified";
    public const string NotAllowlisted = "not_allowlisted";

    // ── Field-level ──
    public const string PrescriptionOrderConflict = "prescription_order_conflict";
    public const string PrescriptionOrderUnverified = "prescription_order_unverified";
    public const string DateImplausible = "date_implausible";
    /// <summary>Prefix — the emitted code is <c>missing_required:{Column}</c>, so match with StartsWith, not equality.</summary>
    public const string MissingRequiredPrefix = "missing_required:";

    // ── Host-level (emitted outside IntakePipeline) ──
    public const string ReclassifyReview = "reclassify_review";
    public const string DuplicateSuspect = "duplicate_suspect";
    public const string MalwareBlocked = "malware_blocked";

    // ── Intake validation (drain/upload rejection classes — the DeadLetter event's errorClass, C6) ──
    public const string EmptyFile = "empty_file";
    public const string FileTooLarge = "file_too_large";
    public const string InvalidPdf = "invalid_pdf";

    /// <summary>Reconcile re-drove this document ADELAIDE_REDRIVE_LIMIT times after unclean stops without one
    /// completed processing run, and parked it as DeadLetter instead of re-driving again.
    ///
    /// Why a bound exists at this layer: a managed failure dead-letters on its FIRST throw (the worker's catch), so
    /// a document can only accumulate re-drives when processing kills the process before any catch runs — a
    /// renderer-crashing PDF, an OOM kill. Unbounded, that is a restart loop: every startup re-enqueues the same
    /// document into the same crash. The park converts the loop into the normal DeadLetter affordance (visible in
    /// the dashboard, human-retryable); a manual retry appends a Received event under a different actor, which
    /// resets the budget by construction (see UploadProcessor.RedrivesSinceLastIngest).</summary>
    public const string RedriveLimit = "redrive_limit";

    /// <summary>Every fixed code (excludes the <see cref="MissingRequiredPrefix"/> family, which is open-ended).</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        LowQuality, RateLimited, ModelFieldUnparsed, ModelEmptyResponse, EmptyTranscript,
        KvnrCheckDigit, NoMatchNoKvnr, NoMatchInvalidKvnr, NoMatchUnmatchedKvnr, AmbiguousKvnr, AmbiguousNameDob, LowConfidence,
        BusinessDomain,
        UnclassifiedType, ClassifySelfCorrected, ClassifyUnverified, NotAllowlisted,
        PrescriptionOrderConflict, PrescriptionOrderUnverified, DateImplausible,
        ReclassifyReview, DuplicateSuspect, MalwareBlocked, RedriveLimit,
        EmptyFile, FileTooLarge, InvalidPdf,
    };
}
