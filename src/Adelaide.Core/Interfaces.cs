namespace Adelaide.Core;

// The pipeline seams. Stub today; Azure DI / GPT-4o / Graph adapters drop in behind these later
// without touching the orchestrator or the verify-or-review gate.

public interface ITranscriber { Task<Transcript> TranscribeAsync(IncomingDocument doc, CancellationToken ct = default); }

public interface ISeparator { Task<IReadOnlyList<DocumentUnit>> SeparateAsync(IncomingDocument doc, Transcript whole, CancellationToken ct = default); }

public interface IExtractor { Task<ExtractedData> ExtractAsync(DocumentUnit unit, CancellationToken ct = default); }

public interface IPatientDirectory { Task<PatientMatch> MatchAsync(ExtractedData data, CancellationToken ct = default); }

/// <summary>Free-text patient lookup for the review screen's patient picker (a full KVNR matches exactly, otherwise a
/// surname prefix). Separate from <see cref="IPatientDirectory"/> because matching is automatic and search is a human
/// tool — the SharePoint list implements both; the demo directory does too.</summary>
public interface IPatientSearch { Task<IReadOnlyList<PatientRef>> SearchAsync(string query, int max = 25, CancellationToken ct = default); }

public interface IDocumentSink { Task FileAsync(IncomingDocument doc, DocumentUnit unit, ProcessingResult result, CancellationToken ct = default); }

/// <summary>Turns a scanned (image-only) PDF into a searchable one by adding an invisible OCR text layer.
/// Returns null when unavailable or on failure, so callers fall back to the original — OCR must never block filing.
/// <paramref name="ctx"/> keys the captured model-call record to the document (null = no capture, e.g. stubs/probes);
/// it never changes what the operation returns.</summary>
public interface IPdfTextLayer
{
    Task<byte[]?> MakeSearchableAsync(byte[] pdf, CallContext? ctx = null, CancellationToken ct = default);

    /// <summary>The OCR-recognized text of <paramref name="pdf"/> — the SAME text <see cref="MakeSearchableAsync"/>
    /// burns into the searchable layer, i.e. exactly what SharePoint would index. Used as the Volltext fallback when
    /// the vision transcript is empty (a blank/illegible/mis-oriented page, or a split child that inherited an empty
    /// parent slice). Returns null when unavailable or on failure, so a missing text layer never blocks filing.
    /// Default: null — test/stub layers opt out and the caller simply files without Volltext.</summary>
    Task<string?> ExtractTextAsync(byte[] pdf, CallContext? ctx = null, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

public interface ILedger { Task SetStateAsync(string id, ProcessingState state, CancellationToken ct = default); }
