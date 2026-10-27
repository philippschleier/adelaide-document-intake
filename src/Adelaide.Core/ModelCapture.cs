using System.Text.RegularExpressions;

namespace Adelaide.Core;

/// <summary>Where in the pipeline a model call originates. One value per call SITE, not per model: the same
/// physical classify prompt runs as <see cref="Classify"/> on the cheap deployment and as
/// <see cref="ClassifyEscalation"/> when re-asked on the strong one — previously indistinguishable in any record
/// (INVESTIGATION.md §7.4 item 7), which is why the escalation is its own site.</summary>
public enum ModelCallSite
{
    Transcription,      // L1 — vision transcription (VisionTranscriber)
    Domain,             // L2 — medical/business lane (AzureOpenAiExtractor.ClassifyDomainAsync)
    Classify,           // L3 — content-type classification, first pass on the mini deployment
    ClassifyEscalation, // L3b — the same classification re-asked on the strong deployment
    SelfCheck,          // L4 — confusable-type self-verification
    Extraction,         // L5 — field extraction
    DiReadPdf,          // D1 — Document Intelligence prebuilt-read with output=pdf (searchable PDF)
    DiOcr,              // D2 — Document Intelligence prebuilt-read, text only (Volltext fallback / probes)
    DiLayout,           // D3 — Document Intelligence prebuilt-layout (selection marks)
}

public static class ModelCallSites
{
    /// <summary>The stable string stored in the capture table — kebab-case, matching the site census in
    /// INVESTIGATION.md §7.2 / docs/CAPTURE.md. Never rename a value: rows already written under it would orphan.</summary>
    public static string Wire(this ModelCallSite site) => site switch
    {
        ModelCallSite.Transcription => "transcription",
        ModelCallSite.Domain => "domain",
        ModelCallSite.Classify => "classify",
        ModelCallSite.ClassifyEscalation => "classify-escalation",
        ModelCallSite.SelfCheck => "self-check",
        ModelCallSite.Extraction => "extraction",
        ModelCallSite.DiReadPdf => "di-read-pdf",
        ModelCallSite.DiOcr => "di-ocr",
        ModelCallSite.DiLayout => "di-layout",
        _ => "unknown",
    };
}

/// <summary>Identity of one model call, threaded from the call site into the wrapper so the captured record is
/// keyed to the document at the moment the call happens. <paramref name="Attempt"/> 0 means "assign the next
/// per-(document, site) ordinal at the store" — the caller usually cannot know it. <paramref name="PromptHash"/>
/// is the SITE's template hash (the same value stamped on ledger events); null for the DI sites, which have no
/// prompt. <paramref name="RenderParamsJson"/> / <paramref name="SourcePdfSha256"/> carry the L1 render inputs
/// (decision: images are captured by hash + parameters, never by bytes).</summary>
public sealed record CallContext(
    string DocumentId,
    ModelCallSite Site,
    int Attempt = 0,
    string? PromptHash = null,
    string? RenderParamsJson = null,
    string? SourcePdfSha256 = null);

/// <summary>The complete record of one model call — request as sent (image payloads elided to hash+size markers),
/// response verbatim, token accounting, latency and the gateway request id. PHI by construction (bodies embed
/// document text), so the only implementation writes it to the PHI side of the transcript store, covered by
/// erasure. Building this record must never change what the call does: capture observes, it never decides.</summary>
public sealed record ModelCallRecord(
    CallContext Context,
    DateTimeOffset At,
    string? Deployment,
    string? ServedModel,
    string? SystemFingerprint,
    string? ReasoningEffort,
    string? RequestBody,
    string? ResponseBody,
    string? OutputSha256 = null,   // D1: the binary output, captured by hash …
    long? OutputBytes = null,      // … and size; the bytes themselves persist as the filed SharePoint artifact
    string? OutputRef = null,      // reserved: a durable reference to where the output bytes live
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? ReasoningTokens = null,
    long LatencyMs = 0,
    string? RequestId = null,
    int? HttpStatus = null,
    string? Error = null);

/// <summary>The wrapper-side seam: OpenAiClient / DocIntelligencePdf call this with the finished record; the host
/// wires it to the capture store. Implementations MUST be fail-open — log + count, never throw — because a capture
/// failure must never fail the model call it observed (the wrappers add a belt-and-braces catch of their own).
/// Null recorder (tools, probes, tests that don't care) simply means no capture.</summary>
public interface IModelCallRecorder
{
    Task RecordAsync(ModelCallRecord record, CancellationToken ct = default);
}

/// <summary>Replaces base64 payloads in a request body with <c>elided:{mime};sha256={hex};bytes={n}</c> markers, so
/// the captured request stays byte-faithful about everything EXCEPT bulk binary content — which is captured by
/// content hash + size instead (decision 6). The hash is over the DECODED bytes, so an image can be matched against
/// a re-render without storing it.</summary>
public static class ModelCallElision
{
    // data: URLs as they appear inside the serialized chat payload. 200+ base64 chars so nothing but real payloads
    // matches (a transcript would have to quote a literal data: URL of that size to be touched — and eliding such a
    // quoted payload would be correct anyway).
    private static readonly Regex DataUrl = new(
        @"data:(?<mime>[a-z]+/[a-zA-Z0-9.+-]+);base64,(?<b64>[A-Za-z0-9+/]{200,}={0,2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Elide every embedded <c>data:…;base64,…</c> payload in <paramref name="json"/>. Anything that fails
    /// to decode is left untouched (better an oversized capture than a corrupted one).</summary>
    public static string ElideDataUrls(string json) => DataUrl.Replace(json, m =>
    {
        try
        {
            var bytes = Convert.FromBase64String(m.Groups["b64"].Value);
            return Marker(m.Groups["mime"].Value, bytes);
        }
        catch (FormatException) { return m.Value; }
    });

    /// <summary>The elision marker for a payload the caller already holds as bytes (e.g. DI's base64Source).</summary>
    public static string Marker(string mime, byte[] bytes) =>
        $"elided:{mime};sha256={Sha256Hex(bytes)};bytes={bytes.Length}";

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
