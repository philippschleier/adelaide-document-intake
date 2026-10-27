using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Adelaide.Core;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Adelaide.SharePoint;

/// <summary>WHERE a unit files, plus the destination policy — the Phase-1 seam that Phase 2's
/// <c>IRoutingRules.Resolve(unit)</c> will populate per document unit (design §5/§7). <paramref name="SiteId"/> +
/// <paramref name="Library"/> select the Graph drive; <paramref name="ContentType"/> is the SharePoint content type to
/// apply at the destination — NULL means "use the spec's own per-unit ContentType", so the medical default is
/// byte-identical; <paramref name="RequiresPatient"/> is the lane policy (medical true, business false). Carried ON the
/// <see cref="FilingSpec"/> so the outbox sidecar round-trips it and reconcile re-drives to the SAME destination after a crash.</summary>
public sealed record FilingTarget(string SiteId, string Library, string? ContentType, bool RequiresPatient);

/// <summary>Everything needed to file a document — and to re-drive the filing idempotently on crash recovery — WITHOUT
/// re-running extraction: the canonical per-document filename, content type, metadata fields, the Volltext search text,
/// and the captured PDF's on-disk path. Persisted as the deletable filing sidecar (alongside the captured PDF); the
/// immutable audit records only the "Filing" fact + the filename, never this payload (so the transcript stays out of the
/// un-erasable hash chain). The filename is computed once (in BuildSpec) so the recorded intent and the artifact bind.
/// <paramref name="Target"/> is the destination (design §7); NULL ⇒ the medical default (Patient Records) resolved in
/// <see cref="SharePointSink.FileAsync(FilingSpec, CancellationToken)"/>, so existing specs/sidecars are unchanged.</summary>
public sealed record FilingSpec(string FileName, string ContentType, IReadOnlyDictionary<string, object?> Fields, string Volltext, string PdfPath, FilingTarget? Target = null);

/// <summary>What one filing produced (C10 — verify-and-pin): the durable driveItem locator, whether the UPLOADED
/// artifact declares PDF/A in its XMP (the record-of-file assertion; the DI rendition declares PDF/A-1a, so false
/// normally means the DI-fallback original went up — marked on the Filed event, reprocessable via /api/reprocess),
/// and whether the artifact IS the DI rendition (false = original fallback, OCR text layer missing too).</summary>
public sealed record FilingResult(string DriveItemId, bool PdfADeclared, bool DiRendition);

/// <summary>Testability seam (P6): the SharePoint operations the outbox path (ProcessAsync/ReconcileAsync) needs --
/// file a self-contained spec idempotently, plus the verified-purge's existence check. Lets those methods depend on an
/// interface so a fake can be injected in tests; at runtime DI resolves IFiler to the SAME SharePointSink instance, so
/// production behaviour is unchanged. NOT the forward-pipeline IDocumentSink (3-arg) overload.</summary>
public interface IFiler
{
    /// <summary>Files the spec and returns the durable locator + the C10 record-of-file facts (see
    /// <see cref="FilingResult"/>). The driveItem id is rename-proof, unlike the canonical filename; the filename
    /// stays recorded as the human-readable label.</summary>
    Task<FilingResult> FileAsync(FilingSpec spec, CancellationToken ct = default);

    /// <summary>Verified-purge's existence check — is the filed driveItem still present? BY ID ONLY (Stage III): name-based
    /// existence has the same rename-fragility as name-based fetch. 404 → false; other errors THROW (never delete on doubt).
    /// <paramref name="target"/> is the drive the locator was RECORDED in (C-2): a driveItemId is only meaningful within
    /// a drive, so a business-filed item probed through the medical default 404s and reads as absent. Null → the medical
    /// default, which is correct for every caller that predates the business lane.</summary>
    Task<bool> ExistsByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default);

    /// <summary>Supersede's delete half — delete a filed item by its durable driveItemId ONLY. There is deliberately NO
    /// name parameter: supersede deletes a specific filed artifact by its id or nothing at all (a legacy no-id target is
    /// rejected upstream, never name-deleted). IDEMPOTENT: a 404 (already deleted — e.g. a reconcile re-drive after a
    /// crash mid-delete) is treated as success. Returns true on delete-or-already-gone.
    /// <paramref name="target"/> is the drive the locator was RECORDED in (C-2). This matters MORE for a delete than
    /// for any read: a business item addressed through the medical default 404s, the 404 folds to success, and the
    /// ledger then records a removal that never happened while the real item stays live. Null → the medical default.</summary>
    /// CENSUS OF EVERY SHAREPOINT DELETE (checked 2026-08-11; drive column added with the C-2 fix) — a delete that
    /// destroys a FILED ARTIFACT must be covered by an outbox member, because the ledger asserts that artifact exists,
    /// and must name the DRIVE it addresses, because a 404 on the wrong drive folds to success. Adding a caller means
    /// adding a row here; if you cannot name the member and the drive, you are about to create the gap this census
    /// exists to prevent.
    ///
    ///   Program.cs  /api/reprocess (inline)   → RECLASSIFY outbox. Intent sidecar written AFTER the write-ahead
    ///                                           "Received" and BEFORE this call; an inline failure is swallowed and
    ///                                           reconcile finishes the removal, so a failure is pending, not lost.
    ///                                           DRIVE: the document's recorded Filed destination, resolved at the
    ///                                           endpoint and carried on the intent.
    ///   Program.cs  DriveReclassifyAsync      → RECLASSIFY outbox, recovery arm. Differs from supersede below: it
    ///                                           appends an ANNOTATION (non-advancing) rather than a terminal event,
    ///                                           is at-most-once keyed on the INTENT rather than on State (an
    ///                                           annotation cannot be state-guarded — not moving State is the point),
    ///                                           and refuses to delete while the ledger still has the document Filed
    ///                                           AT THIS VERY LOCATOR, which would mean the write-ahead is missing.
    ///                                           DRIVE: the intent's recorded target (absent on pre-C-2 sidecars →
    ///                                           medical, which is correct for every document they can name).
    ///   Program.cs  DriveSupersedeAsync       → SUPERSEDE outbox. Intent sidecar before the call; STATE-GUARDED
    ///                                           terminal "Superseded" append, so a re-drive cannot double-append;
    ///                                           TWO tripwires refuse a stale intent (locator: the document re-filed
    ///                                           at a new id; state: the document left Filed — mid-reprocess,
    ///                                           discarded, split — and is not simply already Superseded).
    ///                                           DRIVE: the intent's recorded target, as above.
    ///   SharePointSink.SelfTestAsync          → NOT covered, and correctly so: it deletes the throwaway PDF it
    ///                                           uploaded moments earlier, in a finally, with no ledger fact naming
    ///                                           it. NOTE it calls GraphDeleteAsync DIRECTLY and so does not appear
    ///                                           in this method's call sites — which is why "sole delete path" below
    ///                                           is scoped to filed artifacts rather than stated absolutely. Worst
    ///                                           case is a leaked `_adelaide_writetest_*.pdf`, visible by name.
    ///                                           DRIVE: medical, same drive it uploaded to moments earlier.
    ///
    /// The rotate outbox is the family's fourth member but performs no delete (an in-place PATCH), so it has no row.
    Task<bool> TryDeleteByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default);
}

/// <summary>The filer plus the READ-BACK half the review/viewer endpoints need (filed-PDF download + streaming, by the
/// durable driveItem id). At runtime this is the same <see cref="SharePointSink"/>; demo mode binds a local-folder
/// store. Kept separate from <see cref="IFiler"/> so the outbox's narrow test seam stays narrow.</summary>
public interface IFiledDocumentStore : IFiler
{
    /// <summary>The filed PDF's bytes BY driveItemId ONLY; null on a confirmed 404 (deleted original).</summary>
    Task<byte[]?> DownloadFiledPdfAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default);

    /// <summary>Streaming variant for the viewer: only headers are awaited; null on a confirmed 404. Caller disposes.</summary>
    Task<HttpResponseMessage?> OpenFiledPdfStreamAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default);
}

/// <summary>
/// Files documents into the SharePoint "Patient Records" library via Microsoft Graph (app-only):
/// upload PDF → set Content Type → set the Patient lookup + the type's metadata columns → canonical filename.
/// Flat, metadata-driven (no folders). Only AutoFile results are filed; review/dead-letter stay in the durable
/// intake store + ledger (the library has no review/Status column yet).
/// </summary>
public sealed class SharePointSink(HttpClient http, SharePointOptions opt, TokenCredential cred, IPdfTextLayer textLayer, ILogger<SharePointSink> log) : IDocumentSink, IFiledDocumentStore
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private static readonly string[] GraphScope = { "https://graph.microsoft.com/.default" };
    private const string LibraryName = "Patient Records";

    private readonly Dictionary<string, string> _driveIds = new(StringComparer.OrdinalIgnoreCase); // (site|library) → driveId
    private readonly Dictionary<string, string> _ctIds = new(StringComparer.OrdinalIgnoreCase);   // (site|listId|contentTypeName) → id

    public async Task FileAsync(IncomingDocument doc, DocumentUnit unit, ProcessingResult result, CancellationToken ct = default)
    {
        if (result.Disposition != Disposition.AutoFile)
        {
            log.LogInformation("Unit {Unit} → review ({ReasonCount} reason(s)); not filed.", unit.UnitId, result.Reasons.Count); // reasons can embed a filename (KVNR) — count only
            return; // review/dead-letter handled by the durable store + ledger
        }
        await FileAsync(BuildSpec(doc, unit, result), ct);
    }

    /// <summary>The SINGLE filing core, driven by a self-contained spec. PUT (replace, idempotent) the searchable PDF at
    /// the spec's canonical path, then set content type + fields + Volltext. Used by BOTH the forward path and crash
    /// recovery (reconcile re-drives the recorded spec from its sidecar) — there is exactly one place that writes to
    /// SharePoint. Every step is idempotent on the doc-unique path, so re-running it (recovery, retry, even two racers)
    /// converges to one fully-provisioned item — no probe, no "is it complete?" inference.</summary>
    public async Task<FilingResult> FileAsync(FilingSpec spec, CancellationToken ct = default)
    {
        // Resolve the destination: an explicit target (business, or a Phase-2 routed unit) or the medical default
        // (Patient Records on the configured site). The applied content type is the target's when set, else the spec's
        // per-unit type — so a null-target medical filing behaves exactly as before.
        var target = spec.Target ?? MedicalTarget();
        var contentType = target.ContentType ?? spec.ContentType;

        var original = await File.ReadAllBytesAsync(spec.PdfPath, ct);
        // Capture identity for the DI calls below: inbox files are always {docId}.pdf (the convention every endpoint
        // builds paths from), so the path stem IS the document id — no spec change, and recovery re-drives from an
        // old sidecar still key their DI calls correctly.
        var captureDocId = Path.GetFileNameWithoutExtension(spec.PdfPath);
        // Make the archived PDF searchable (OCR text layer via Document Intelligence); fall back to the original if DI
        // is unavailable — a missing text layer must never block or lose a document.
        var searchable = await textLayer.MakeSearchableAsync(original, new CallContext(captureDocId, ModelCallSite.DiReadPdf), ct);

        // C10 (verify-and-pin, not convert): assert the RECORD OF FILE declares PDF/A. The DI rendition declares
        // PDF/A-1a in its XMP; a non-declaring artifact (the DI-fallback original, or a DI output regression) files
        // anyway — never block on this — but the fact rides the FilingResult onto the Filed event, so the exception
        // is durable, queryable (pdfa-census), and reprocessable (/api/reprocess re-runs DI).
        var artifact = searchable ?? original;
        var pdfaDeclared = PdfAId.Declares(artifact);
        if (!pdfaDeclared)
            log.LogWarning("Filed artifact for {DocId} does NOT declare PDF/A ({Provenance}); marked on the Filed event — reprocess once DI is available to replace it with a conforming rendition.",
                captureDocId, searchable is not null ? "DI rendition — investigate, DI normally declares" : "original fallback, DI unavailable");

        var driveId = await DriveIdAsync(target.SiteId, target.Library, ct);
        var item = await UploadAsync(driveId, spec.FileName, artifact, ct);
        var driveItemId = item.GetProperty("id").GetString()!; // the durable locator, returned for the Filed event
        var (listId, listItemId) = SharePointIds(item);

        await SetContentTypeAsync(target.SiteId, listId, listItemId, contentType, ct);
        // Medical (RequiresPatient) writes its required columns STRICTLY — a failed PATCH throws, so the doc stays in
        // "Filing" for reconcile (a required patient/field column MUST land). A BUSINESS filing writes minimal metadata to
        // the DISPOSABLE triage library, where the audit ledger + original PDF are the system of record (the Zielbereich
        // label lives on the Reviewed event): a disposable-column failure must NOT strand the filing, so write it
        // best-effort (log + continue), exactly like Volltext.
        if (target.RequiresPatient)
            await SetFieldsAsync(target.SiteId, listId, listItemId, spec.Fields, ct);
        else
            await SetFieldsBestEffortAsync(target.SiteId, listId, listItemId, spec.Fields, ct);
        // Volltext = the document's searchable text. Normally the vision transcript (spec.Volltext). When that's empty —
        // a blank/illegible/mis-oriented page, or a split child that inherited an empty parent slice — fall back to the DI
        // OCR of the filed PDF (the SAME text baked into the searchable layer above, so it's exactly what SharePoint
        // indexes). The fallback is a second, cheap prebuilt-read taken ONLY in the rare empty case; a null result leaves
        // Volltext unset (SetVolltextAsync logs the skip). DI reads orientation-robustly, and for reviewed docs the
        // rotation is already baked into `original`, so the OCR reads upright.
        var volltext = spec.Volltext;
        if (string.IsNullOrWhiteSpace(volltext))
        {
            volltext = await textLayer.ExtractTextAsync(original, new CallContext(captureDocId, ModelCallSite.DiOcr), ct);
            if (!string.IsNullOrWhiteSpace(volltext))
                log.LogInformation("Volltext transcript empty for item {Item} — recovered {Chars} chars via DI OCR fallback.", listItemId, volltext.Length);
        }
        await SetVolltextAsync(target.SiteId, listId, listItemId, volltext, ct);

        // No filename: the canonical filename can embed the patient KVNR (PHI). Log the content type, destination library,
        // and the (opaque) list-item id — all non-PHI.
        log.LogInformation("Filed as {Type} (item {Item}) in {Library} — {Pdf}.", contentType, listItemId, target.Library,
            searchable is not null ? "searchable PDF" : "original PDF (OCR unavailable)");
        return new FilingResult(driveItemId, pdfaDeclared, DiRendition: searchable is not null);
    }

    /// <summary>The medical default destination (Patient Records on the configured site): the target used when a spec
    /// carries none — so the medical path and every existing on-disk filing sidecar (no recorded target) are unchanged.</summary>
    private FilingTarget MedicalTarget() => new(opt.SiteId, LibraryName, ContentType: null, RequiresPatient: true);

    /// <summary>Build the complete, self-contained filing spec from the pipeline objects — everything needed to file AND
    /// to re-drive on recovery without re-extraction. The filename is computed here ONCE, so the recorded intent and the
    /// uploaded artifact are the identical string by construction (not two BuildFilename calls that merely agree).</summary>
    public static FilingSpec BuildSpec(IncomingDocument doc, DocumentUnit unit, ProcessingResult result)
    {
        var contentType = result.Data.Value("ContentType") ?? DocumentCatalog.Fallback;
        return new FilingSpec(
            BuildFilename(doc, result, contentType),
            contentType,
            BuildFields(result, contentType),
            BarcodeSummary.LeadWith(unit.Transcript.Markdown, unit.Transcript.Barcodes),
            doc.SourcePath);
    }

    /// <summary>Build the Phase-1 BUSINESS filing spec for a document unit: minimal holding-library metadata
    /// (DocumentDate + the reviewer's Zielbereich label), the configured business content type, and the business
    /// <see cref="FilingTarget"/> — NEVER a patient (design §7 "minimal"). Mirrors <see cref="BuildSpec"/> so the same
    /// idempotent outbox/reconcile path drives it; the target on the spec makes crash recovery re-drive to the business
    /// destination, not Patient Records.</summary>
    public static FilingSpec BuildBusinessSpec(IncomingDocument doc, DocumentUnit unit, FilingTarget target, string contentType, string? documentDate, string? zielbereich = null)
    {
        return new FilingSpec(
            BuildBusinessFilename(doc, contentType, documentDate),
            contentType,
            BuildBusinessFields(documentDate, zielbereich),
            BarcodeSummary.LeadWith(unit.Transcript.Markdown, unit.Transcript.Barcodes),
            doc.SourcePath,
            target);
    }

    /// <summary>Business metadata: the document/received date + the reviewer's Zielbereich label (HR/Finance/unklar) —
    /// and NEVER PatientLookupId (a business unit has no patient nexus). Kept intentionally minimal for the Phase-1
    /// holding library; per-type business fields are Phase 3. Zielbereich is a CONVENIENCE copy for the library — its
    /// system of record is the audit ledger's Reviewed event (see ReviewDiff), so <see cref="FileAsync(FilingSpec,
    /// CancellationToken)"/> writes the business fields best-effort.</summary>
    public static Dictionary<string, object?> BuildBusinessFields(string? documentDate, string? zielbereich = null)
    {
        var fields = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(documentDate)) fields[DocumentDateColumn] = DateIso(documentDate);
        if (!string.IsNullOrWhiteSpace(zielbereich)) fields[ZielbereichColumn] = zielbereich;
        return fields; // no PatientLookupId, ever
    }

    public const string DocumentDateColumn = "DocumentDate";
    public const string ZielbereichColumn = "Zielbereich";

    /// <summary>The business lane's column names, single-sourced. <see cref="BuildBusinessFields"/> writes exactly
    /// these, and the review endpoint's field-name vocabulary reads them (ReviewGuard.BusinessLedgerSafeFieldNames)
    /// — the same shape as the medical side, which derives its vocabulary from <c>DocumentCatalog</c>. A Phase-3
    /// business column added here is therefore admitted by the gate automatically and cannot drift out of it.</summary>
    public static readonly IReadOnlySet<string> BusinessFieldNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DocumentDateColumn, ZielbereichColumn };

    // The business canonical filename: date + business type + FULL DocumentId — NO KVNR (there is no patient). The full
    // docId keeps the path per-document, so the replace-PUT stays idempotent on crash recovery exactly like the medical path.
    private static string BuildBusinessFilename(IncomingDocument doc, string contentType, string? documentDate)
    {
        var datePart = !string.IsNullOrWhiteSpace(documentDate) && DateOnly.TryParseExact(documentDate, ExactDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); // received-date fallback when no DocumentDate
        return $"{datePart}_{Sanitize(contentType)}_{Sanitize(doc.DocumentId)}.pdf";
    }

    /// <summary>Proves write access + the full mechanism with a throwaway item, then deletes it. No PHI, no real patient.</summary>
    public async Task SelfTestAsync(CancellationToken ct = default)
    {
        var name = $"_adelaide_writetest_{Guid.NewGuid():N}.pdf";
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4\nAdelaide write self-test — safe to delete.\n");
        log.LogInformation("Write self-test: uploading throwaway {Name} to {Library}", name, LibraryName);

        var driveId = await DriveIdAsync(ct);
        var item = await UploadAsync(driveId, name, bytes, ct);
        var driveItemId = item.GetProperty("id").GetString()!;
        var (listId, listItemId) = SharePointIds(item);
        log.LogInformation("Write self-test: uploaded (driveItem {DriveItemId}, listItem {ListItemId})", driveItemId, listItemId);

        try
        {
            await SetContentTypeAsync(opt.SiteId, listId, listItemId, "Verordnung", ct);
            log.LogInformation("Write self-test: content type set to Verordnung");
            await SetFieldsAsync(opt.SiteId, listId, listItemId, new Dictionary<string, object?>
            {
                ["DocumentDate"] = "2026-01-01T00:00:00Z",
                ["PrescriptionOrder"] = "Erstverordnung",
            }, ct);
            log.LogInformation("Write self-test: fields set (DocumentDate, PrescriptionOrder)");

            var read = await GraphAsync(HttpMethod.Get,
                $"{GraphBase}/sites/{opt.SiteId}/lists/{listId}/items/{listItemId}?$expand=fields", null, ct);
            var ctName = read.TryGetProperty("contentType", out var ctEl) && ctEl.TryGetProperty("name", out var ctn) ? ctn.GetString() : "(default)";
            var po = read.GetProperty("fields").TryGetProperty("PrescriptionOrder", out var p) ? p.GetString() : "(none)";
            log.LogInformation("Write self-test: readback contentType={ContentType} PrescriptionOrder={PrescriptionOrder}", ctName, po);
        }
        finally
        {
            await GraphDeleteAsync($"{GraphBase}/drives/{driveId}/items/{driveItemId}", ct);
            log.LogInformation("Write self-test: throwaway deleted — write access confirmed");
        }
    }

    /// <summary>One-time setup: ensure 'Patient Records' has a 'Volltext' multi-line text column for the transcript.
    /// Idempotent — returns false if it already exists, true if it was created.</summary>
    public async Task<bool> EnsureVolltextColumnAsync(CancellationToken ct = default)
    {
        var listId = await ListIdAsync(LibraryName, ct);
        var cols = await GraphAsync(HttpMethod.Get, $"{GraphBase}/sites/{opt.SiteId}/lists/{listId}/columns?$select=name", null, ct);
        foreach (var c in cols.GetProperty("value").EnumerateArray())
            if (string.Equals(c.GetProperty("name").GetString(), "Volltext", StringComparison.OrdinalIgnoreCase))
                return false;
        await GraphAsync(HttpMethod.Post, $"{GraphBase}/sites/{opt.SiteId}/lists/{listId}/columns", new Dictionary<string, object?>
        {
            ["name"] = "Volltext",
            ["displayName"] = "Volltext",
            ["text"] = new Dictionary<string, object?>
            {
                ["allowMultipleLines"] = true,
                ["appendChangesToExistingText"] = false,
                ["linesForEditing"] = 6,
            },
        }, ct);
        return true;
    }

    /// <summary>Delete a filed item by its durable driveItemId (Stage III: the sole delete path FOR FILED ARTIFACTS —
    /// reclassify + supersede both use it; the name-keyed delete is retired. The write self-test deletes its own
    /// throwaway via GraphDeleteAsync directly, which is why that qualifier is not dropped — see the census on
    /// IFiler.TryDeleteByIdAsync). GraphDeleteAsync folds a 404 into success, so re-driving an
    /// already-deleted item (reconcile recovery, or reclassify after supersede) is a no-op that still returns true.</summary>
    public async Task<bool> TryDeleteByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
    {
        var driveId = await DriveIdAsync(target, ct);
        await GraphDeleteAsync($"{GraphBase}/drives/{driveId}/items/{driveItemId}", ct);
        return true;
    }

    /// <summary>True if the filed driveItem still exists — the delete-on-verified check, BY ID ONLY (Stage III). 404 →
    /// false; other errors THROW, so the only local copy is never deleted on uncertainty.</summary>
    public async Task<bool> ExistsByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
    {
        var driveId = await DriveIdAsync(target, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/drives/{driveId}/items/{driveItemId}?$select=id");
        var token = await cred.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        if (!resp.IsSuccessStatusCode) throw Redact.HttpFail((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), "Graph lookup", GraphPatientDirectory.GraphReqId(resp));
        return true;
    }

    /// <summary>THE shared filed-PDF resolution — BY driveItemId ONLY (Stage III retires the name-fallback now that every
    /// filed doc carries an id; the backfill drove class-(c) to 0). A 404 → null → the caller's deleted-original handling.
    /// The id-recording invariant (a filed doc always has an id) is enforced UPSTREAM at the caller's tripwire.</summary>
    public async Task<byte[]?> DownloadFiledPdfAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
    {
        var driveId = await DriveIdAsync(target, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/drives/{driveId}/items/{driveItemId}/content");
        var token = await cred.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var resp = await http.SendAsync(req, ct); // /content 302-redirects to a pre-signed URL; the handler follows it
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw Redact.HttpFail((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), "Graph download", GraphPatientDirectory.GraphReqId(resp));
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>STREAMING variant of <see cref="DownloadFiledPdfAsync"/> for the viewer path: only the response
    /// HEADERS are awaited (ResponseHeadersRead), so /api/file can pipe bytes through as they arrive and forward
    /// Graph's Content-Length for determinate client progress — no full-buffer-then-send. BY driveItemId ONLY (Stage III);
    /// null on a CONFIRMED 404 → the caller's deleted-original handling. The caller owns (disposes) the returned response
    /// after copying the stream.</summary>
    public async Task<HttpResponseMessage?> OpenFiledPdfStreamAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
    {
        var driveId = await DriveIdAsync(target, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/drives/{driveId}/items/{driveItemId}/content");
        var token = await cred.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); // stream, don't buffer
        if (resp.StatusCode == HttpStatusCode.NotFound) { resp.Dispose(); return null; }
        if (!resp.IsSuccessStatusCode)
        {
            var reqId = GraphPatientDirectory.GraphReqId(resp);
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw Redact.HttpFail((int)resp.StatusCode, body, "Graph download", reqId);
        }
        return resp;
    }

    // ---- Backfill support (one-off migrations of already-filed items) ----
    public sealed record BackfillTarget(string ListItemId, string DriveItemId, string FileName);

    /// <summary>The work-list for a backfill: items of <paramref name="contentType"/> whose <paramref name="column"/>
    /// is empty. Reads the whole library (paginated) and filters client-side on the item's ContentType + column —
    /// reliable across Graph versions, where a server-side $filter on content type is not.</summary>
    public async Task<IReadOnlyList<BackfillTarget>> FindMissingFieldAsync(string contentType, string column, CancellationToken ct = default)
    {
        var listId = await ListIdAsync(LibraryName, ct);
        var targets = new List<BackfillTarget>();
        var url = $"{GraphBase}/sites/{opt.SiteId}/lists/{listId}/items?$expand=fields,driveItem&$top=200";
        while (url is not null)
        {
            var page = await GraphAsync(HttpMethod.Get, url, null, ct);
            foreach (var item in page.GetProperty("value").EnumerateArray())
            {
                if (!item.TryGetProperty("fields", out var fields)) continue;
                var ctName = fields.TryGetProperty("ContentType", out var cn) ? cn.GetString() : null;
                if (!string.Equals(ctName, contentType, StringComparison.OrdinalIgnoreCase)) continue;
                if (fields.TryGetProperty(column, out var col) && !string.IsNullOrWhiteSpace(col.GetString())) continue; // already filled
                if (!item.TryGetProperty("driveItem", out var di)) continue;
                var driveItemId = di.TryGetProperty("id", out var did) ? did.GetString() : null;
                var listItemId = item.GetProperty("id").GetString();
                if (driveItemId is null || listItemId is null) continue;
                var fileName = di.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                targets.Add(new BackfillTarget(listItemId, driveItemId, fileName ?? listItemId));
            }
            url = page.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return targets;
    }

    /// <summary>One row per library item: the list-item id, the driveItem identity (id + CURRENT name — the name may
    /// have been changed in SharePoint since filing), and selected metadata columns in raw Graph string form.</summary>
    public sealed record LibraryEntry(string ListItemId, string? DriveItemId, string? FileName, Dictionary<string, string?> Fields);

    /// <summary>READ-ONLY enumeration of EVERY item in 'Patient Records' with driveItem identity + selected columns —
    /// the Stage II ledger↔library reconciliation input. Pages politely (100/page + a small delay); 429/5xx are
    /// absorbed by the standard resilience handler. Values (incl. Volltext) can be PHI — callers keep them local.</summary>
    public async Task<IReadOnlyList<LibraryEntry>> ListAllItemsAsync(string[] columns, CancellationToken ct = default)
    {
        var listId = await ListIdAsync(LibraryName, ct);
        var rows = new List<LibraryEntry>();
        var url = $"{GraphBase}/sites/{opt.SiteId}/lists/{listId}/items?$expand=fields,driveItem&$top=100";
        while (url is not null)
        {
            var page = await GraphAsync(HttpMethod.Get, url, null, ct);
            foreach (var item in page.GetProperty("value").EnumerateArray())
            {
                var listItemId = item.GetProperty("id").GetString();
                if (listItemId is null) continue;
                string? driveItemId = null, name = null;
                if (item.TryGetProperty("driveItem", out var di))
                {
                    driveItemId = di.TryGetProperty("id", out var did) ? did.GetString() : null;
                    name = di.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                }
                var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("fields", out var fields))
                    foreach (var c in columns)
                        dict[c] = fields.TryGetProperty(c, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                rows.Add(new LibraryEntry(listItemId, driveItemId, name, dict));
            }
            url = page.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            if (url is not null) await Task.Delay(250, ct); // Graph etiquette: pace the pages
        }
        return rows;
    }

    /// <summary>Read selected column values for every item of a content type (paginated). Values come back in their raw
    /// Graph string form (dates as ISO 8601); non-string/absent columns are null. Used by one-off field migrations.</summary>
    public async Task<IReadOnlyList<(string ListItemId, Dictionary<string, string?> Fields)>> ReadItemsByTypeAsync(
        string contentType, string[] columns, CancellationToken ct = default)
    {
        var listId = await ListIdAsync(LibraryName, ct);
        var rows = new List<(string, Dictionary<string, string?>)>();
        var url = $"{GraphBase}/sites/{opt.SiteId}/lists/{listId}/items?$expand=fields&$top=200";
        while (url is not null)
        {
            var page = await GraphAsync(HttpMethod.Get, url, null, ct);
            foreach (var item in page.GetProperty("value").EnumerateArray())
            {
                if (!item.TryGetProperty("fields", out var fields)) continue;
                var ctName = fields.TryGetProperty("ContentType", out var cn) ? cn.GetString() : null;
                if (!string.Equals(ctName, contentType, StringComparison.OrdinalIgnoreCase)) continue;
                var listItemId = item.GetProperty("id").GetString();
                if (listItemId is null) continue;
                var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in columns)
                    dict[c] = fields.TryGetProperty(c, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                rows.Add((listItemId, dict));
            }
            url = page.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
        return rows;
    }

    /// <summary>Download a library item's file (PDF bytes) by its driveItem id.</summary>
    public async Task<byte[]> DownloadItemPdfAsync(string driveItemId, CancellationToken ct = default)
    {
        var driveId = await DriveIdAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/drives/{driveId}/items/{driveItemId}/content");
        var token = await cred.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var resp = await http.SendAsync(req, ct); // /content 302-redirects to a pre-signed URL; the handler follows it
        if (!resp.IsSuccessStatusCode) throw Redact.HttpFail((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), "Graph download", GraphPatientDirectory.GraphReqId(resp));
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Set a single column on an existing item (used by backfills).</summary>
    public async Task WriteFieldAsync(string listItemId, string column, string value, CancellationToken ct = default)
    {
        var listId = await ListIdAsync(LibraryName, ct);
        await SetFieldsAsync(opt.SiteId, listId, listItemId, new Dictionary<string, object?> { [column] = value }, ct);
    }

    private async Task<string> ListIdAsync(string name, CancellationToken ct)
    {
        var root = await GraphAsync(HttpMethod.Get, $"{GraphBase}/sites/{opt.SiteId}/lists?$select=id,name,displayName", null, ct);
        foreach (var l in root.GetProperty("value").EnumerateArray())
        {
            var n = l.GetProperty("name").GetString();
            var dn = l.TryGetProperty("displayName", out var d) ? d.GetString() : null;
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase) || string.Equals(dn, name, StringComparison.OrdinalIgnoreCase))
                return l.GetProperty("id").GetString()!;
        }
        throw new InvalidOperationException($"List '{name}' not found on site.");
    }

    // ---- filename + fields ----
    public static string BuildFilename(IncomingDocument doc, ProcessingResult result, string contentType)
    {
        var date = result.Data.Value("DocumentDate");
        var datePart = !string.IsNullOrWhiteSpace(date) && DateOnly.TryParseExact(date, ExactDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); // received-date fallback when no DocumentDate
        var kvnr = result.Match.Patient?.Kvnr ?? result.Data.Value("KVNR") ?? "unknown";
        // FULL DocumentId, not a prefix: an 8-char prefix of a 12-char id can collide for two DISTINCT documents that
        // share date+type+kvnr+period, and the replace-PUT would then silently overwrite one with the other — permanent
        // PHI loss. The full id makes the canonical path genuinely per-document, so the upsert-PUT only ever replaces
        // THIS document's own bytes — which is exactly what idempotent crash-recovery re-filing relies on (replace, not fail).
        return $"{datePart}_{Sanitize(contentType)}_{Sanitize(kvnr)}_{PeriodPart(result)}{Sanitize(doc.DocumentId)}.pdf";
    }

    // The Bewilligungs-/Verordnungszeitraum start distinguishes distinct same-day documents for one patient and type
    // (e.g. two Genehmigungen issued the same day for different periods / Genehmigungsnummern). It is part of the
    // BuildDuplicateKeyPrefix prefix too, so two documents with different periods are not flagged as duplicates.
    private static string PeriodPart(ProcessingResult result)
    {
        var from = result.Data.Value("ValidFrom");
        return !string.IsNullOrWhiteSpace(from) && DateOnly.TryParseExact(from, ExactDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? $"{d:yyyy-MM-dd}_" : "";
    }

    private static string Sanitize(string s) => Regex.Replace(s, "[\\\\/:*?\"<>|#%§\\s]+", "-").Trim('-');

    /// <summary>The business duplicate key as a canonical-filename prefix: "{date}_{type}_{kvnr}_". Null when the
    /// defining parts (extracted DocumentDate + a patient KVNR) aren't available — no key, no duplicate claim.</summary>
    public static string? BuildDuplicateKeyPrefix(ProcessingResult result, string contentType)
    {
        var date = result.Data.Value("DocumentDate");
        var kvnr = result.Match.Patient?.Kvnr ?? result.Data.Value("KVNR");
        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(kvnr)) return null;
        if (!DateOnly.TryParseExact(date, ExactDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        return $"{d:yyyy-MM-dd}_{Sanitize(contentType)}_{Sanitize(kvnr!)}_{PeriodPart(result)}";
    }

    public static Dictionary<string, object?> BuildFields(ProcessingResult result, string contentType)
    {
        var fields = new Dictionary<string, object?>();
        if (result.Match.Outcome == MatchOutcome.Single && result.Match.Patient is { } p)
            fields["PatientLookupId"] = p.ListItemId;

        var def = DocumentCatalog.Find(contentType);
        if (def is not null)
            foreach (var f in def.Fields)
            {
                if (f.Kind == FieldKind.Lookup) continue; // Patient handled above
                var val = result.Data.Value(f.Column);
                if (string.IsNullOrWhiteSpace(val)) continue;
                fields[f.Column] = f.Kind switch
                {
                    FieldKind.Date => DateIso(val),
                    FieldKind.Bool => IsTrue(val),
                    _ => val,
                };
            }
        return fields;
    }

    // EXACT day-first formats only (the pipeline/review emit ISO; dotted German kept defensively). NEVER a lenient
    // parse — invariant+lenient reads month-first and would swap day/month on any dotted/slashed value reaching filing.
    private static readonly string[] ExactDateFormats = { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy" };

    private static object DateIso(string v) =>
        DateTime.TryParseExact(v, ExactDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : v;

    private static object IsTrue(string v) => v.Trim().ToLowerInvariant() is "true" or "ja" or "yes" or "x" or "1";

    // ---- Graph plumbing ----
    private static (string listId, string listItemId) SharePointIds(JsonElement driveItem)
    {
        var sp = driveItem.GetProperty("sharepointIds");
        return (sp.GetProperty("listId").GetString()!, sp.GetProperty("listItemId").GetString()!);
    }

    // Target-aware drive resolution for the by-id members (C-2): the RECORDED destination when the caller has one,
    // the medical default (Patient Records on the configured site) when it passes null — which is correct for every
    // document filed before the business lane existed, because none of them carries a domain marker. The write path
    // (FileAsync) resolves the same way from spec.Target. The remaining no-target resolutions: SelfTestAsync (medical
    // by intent), DownloadItemPdfAsync (backfill tooling over a medical-library enumeration), and the Worker's
    // PdfACensus, which passes no target DELIBERATELY and documents its business-lane mis-count at its own call site.
    private Task<string> DriveIdAsync(FilingTarget? target, CancellationToken ct)
    {
        var t = target ?? MedicalTarget();
        return DriveIdAsync(t.SiteId, t.Library, ct);
    }

    private Task<string> DriveIdAsync(CancellationToken ct) => DriveIdAsync(opt.SiteId, LibraryName, ct);

    private async Task<string> DriveIdAsync(string siteId, string library, CancellationToken ct)
    {
        var key = siteId + "|" + library;
        if (_driveIds.TryGetValue(key, out var cached)) return cached;
        var root = await GraphAsync(HttpMethod.Get, $"{GraphBase}/sites/{siteId}/drives?$select=id,name", null, ct);
        foreach (var d in root.GetProperty("value").EnumerateArray())
            if (string.Equals(d.GetProperty("name").GetString(), library, StringComparison.OrdinalIgnoreCase))
                return _driveIds[key] = d.GetProperty("id").GetString()!;
        throw new InvalidOperationException($"Library '{library}' not found on site.");
    }

    private async Task<JsonElement> UploadAsync(string driveId, string filename, byte[] bytes, CancellationToken ct)
    {
        var url = $"{GraphBase}/drives/{driveId}/root:/{Uri.EscapeDataString(filename)}:/content";
        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var token = await cred.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw Redact.HttpFail((int)resp.StatusCode, body, "Graph upload", GraphPatientDirectory.GraphReqId(resp));
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        // re-fetch to obtain sharepointIds (listId/listItemId) for metadata
        return await GraphAsync(HttpMethod.Get, $"{GraphBase}/drives/{driveId}/items/{id}?$select=id,name,sharepointIds", null, ct);
    }

    private async Task SetContentTypeAsync(string siteId, string listId, string listItemId, string name, CancellationToken ct)
    {
        var ctId = await ContentTypeIdAsync(siteId, listId, name, ct);
        if (ctId is null) { log.LogWarning("Content type '{Name}' not found; leaving default.", name); return; }
        await GraphAsync(HttpMethod.Patch, $"{GraphBase}/sites/{siteId}/lists/{listId}/items/{listItemId}",
            new Dictionary<string, object?> { ["contentType"] = new Dictionary<string, object?> { ["id"] = ctId } }, ct);
    }

    // Cache keyed by (site|listId|name): content-type ids are per-list, so a bare name-key would return the wrong id when
    // the same content-type name exists on two libraries (e.g. the built-in "Document" on both Patient Records and the
    // business holding library).
    private async Task<string?> ContentTypeIdAsync(string siteId, string listId, string name, CancellationToken ct)
    {
        if (_ctIds.TryGetValue(siteId + "|" + listId + "|" + name, out var cached)) return cached;
        var root = await GraphAsync(HttpMethod.Get, $"{GraphBase}/sites/{siteId}/lists/{listId}/contentTypes?$select=id,name", null, ct);
        string? found = null;
        foreach (var c in root.GetProperty("value").EnumerateArray())
        {
            var n = c.GetProperty("name").GetString();
            var id = c.GetProperty("id").GetString();
            if (n is null || id is null) continue;
            _ctIds[siteId + "|" + listId + "|" + n] = id;
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) found = id;
        }
        return found;
    }

    private async Task SetFieldsAsync(string siteId, string listId, string listItemId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        if (fields.Count == 0) return;
        await GraphAsync(HttpMethod.Patch, $"{GraphBase}/sites/{siteId}/lists/{listId}/items/{listItemId}/fields", fields, ct);
    }

    // Best-effort sibling of SetFieldsAsync for a DISPOSABLE business holding library: a failed metadata PATCH is logged
    // and swallowed rather than thrown, so a missing/renamed convenience column (DocumentDate, Zielbereich) can never
    // strand a business filing in "Filing". The medical path never uses this — its required columns must land (strict).
    // Nothing is lost by degrading: the Zielbereich label's system of record is the audit ledger, not this column.
    private async Task SetFieldsBestEffortAsync(string siteId, string listId, string listItemId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        if (fields.Count == 0) return;
        try { await SetFieldsAsync(siteId, listId, listItemId, fields, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Business holding-library metadata write skipped (best-effort) — ensure the destination columns (e.g. DocumentDate, Zielbereich) exist; the filing still completes and the label remains in the audit ledger.");
        }
    }

    // Best-effort: store the full transcript in a 'Volltext' multi-line column so SharePoint search indexes the
    // document's text even when the PDF's own OCR layer isn't enough. Its own PATCH so a missing/oversize column
    // degrades gracefully — the document still files with all its real metadata.
    private const int VolltextMax = 100_000;
    private async Task SetVolltextAsync(string siteId, string listId, string listItemId, string? markdown, CancellationToken ct)
    {
        var text = markdown?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            // Loud skip: the item files WITHOUT searchable text. Reaching here means BOTH the vision transcript AND the DI
            // OCR fallback (in FileAsync) were empty — typically a genuinely blank/illegible page, or a split child isolated
            // onto one. Non-PHI: only the opaque list-item id. If this fires for a page that plainly has text, both the
            // transcript and OCR failed and the item is silently unfindable — that's the case this line makes visible.
            log.LogWarning("Volltext EMPTY for item {Item} — filed without searchable text (transcript and DI OCR fallback both empty; e.g. a blank/illegible/mis-oriented page).", listItemId);
            return;
        }
        if (text.Length > VolltextMax) text = text[..VolltextMax];
        try
        {
            await SetFieldsAsync(siteId, listId, listItemId, new Dictionary<string, object?> { ["Volltext"] = text }, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Volltext write skipped — ensure a 'Multiple lines of text' column named 'Volltext' exists on the destination library.");
        }
    }

    // Volltext leads with the barcode-decoded prescription summary (BarcodeSummary.LeadWith at the call site) so the
    // measures are findable in search even where the OCR checkbox reading is wrong; the raw payload is never written.

    private async Task<JsonElement> GraphAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        var token = await cred.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var s = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw Redact.HttpFail((int)resp.StatusCode, s, $"Graph {method}", GraphPatientDirectory.GraphReqId(resp));
        return string.IsNullOrWhiteSpace(s) ? default : JsonDocument.Parse(s).RootElement.Clone();
    }

    private async Task GraphDeleteAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        var token = await cred.GetTokenAsync(new TokenRequestContext(GraphScope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
            throw Redact.HttpFail((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), "Graph DELETE", GraphPatientDirectory.GraphReqId(resp));
    }
}
