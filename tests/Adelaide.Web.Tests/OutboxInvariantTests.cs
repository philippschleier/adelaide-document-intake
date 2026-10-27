using System.Threading.Channels;
using Adelaide.Audit;
using Adelaide.Core;
using Adelaide.SharePoint;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

// P6 — locks the OUTBOX INVARIANT of UploadProcessor (Adelaide.Web), via the testability seams:
//   * IFiler (the injectable filing seam) — a fake that throws/succeeds on FileAsync and records calls; NO Graph/HTTP.
//   * fake ITranscriber/IExtractor/IPatientDirectory — for the pre-filing failure.
//   * an IAuditLog decorator that throws on the "Filed" append — for the post-upload record failure.
//   * InternalsVisibleTo — call ConsumeAsync/ProcessAsync/ReconcileAsync directly (no reflection, no ExecuteAsync timer).
//
// THE INVARIANT: a PRE-filing failure ⇒ DeadLetter; once a doc reaches "Filing", any failure ⇒ it STAYS "Filing"
// (reconcile completes it from the sidecar) and is NEVER dead-lettered, because the bytes may already be uploaded.
[Collection("inbox-env")] // serialized with DrainPollerTests: both swap the process-wide ADELAIDE_INBOX env var
public sealed class OutboxInvariantTests : IDisposable
{
    // Isolated temp inbox + temp SQLite DB; ADELAIDE_INBOX points reconcile at our inbox. Restored/cleaned on Dispose.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"adelaide-outbox-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly string? _prevInbox;

    public OutboxInvariantTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "audit.db");
        _prevInbox = Environment.GetEnvironmentVariable("ADELAIDE_INBOX");
        Environment.SetEnvironmentVariable("ADELAIDE_INBOX", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ADELAIDE_INBOX", _prevInbox);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- T1: pre-filing failure ⇒ DeadLetter -------------------------------------------------------------------
    [Fact]
    public async Task T1_PreFilingFailure_GoesToDeadLetter_WithDeadLetterEvent()
    {
        using var store = NewStore();
        const string id = "t1";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);

        // Extraction (a PRE-filing step) throws → ProcessAsync throws → ConsumeAsync dead-letters it.
        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store, extractor: new FakeExtractor { Throw = true }, filer: new RecordingFiler());
        await queue.Writer.WriteAsync(new UploadJob(id, PdfPath(id), "t1.pdf"));
        queue.Writer.Complete();                         // so ConsumeAsync drains the one job and returns
        await proc.ConsumeAsync(CancellationToken.None);

        Assert.Equal("DeadLetter", await StateOf(store, id));
        Assert.True(await HasEvent(store, id, "DeadLetter"));
    }

    // ---- T2: FileAsync throws ⇒ stays Filing, NOT DeadLetter; reconcile re-drives -------------------------------
    [Fact]
    public async Task T2_PostFilingFailure_FileAsyncThrows_StaysFiling_NotDeadLettered_ReconcileRedrives()
    {
        using var store = NewStore();
        const string id = "t2";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);

        var filer = new RecordingFiler { ThrowOnFile = true };   // upload fails AFTER the "Filing" intent is recorded
        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store, filer: filer);

        await proc.ProcessAsync(new UploadJob(id, PdfPath(id), "t2.pdf"), CancellationToken.None);

        Assert.Equal("Filing", await StateOf(store, id));                              // stays Filing
        Assert.False(await HasEvent(store, id, "DeadLetter"));                         // NEVER dead-lettered
        Assert.False(await HasEvent(store, id, "Filed"));
        Assert.True(File.Exists(UploadProcessor.FilingSidecarPath(PdfPath(id))));      // recoverable: sidecar on disk
        Assert.Empty(filer.FileCalls);                                                 // the throwing upload recorded nothing

        // A subsequent reconcile re-drives the recorded spec; now the upload succeeds → completes to Filed.
        filer.ThrowOnFile = false;
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Equal("Filed", await StateOf(store, id));
        Assert.False(await HasEvent(store, id, "DeadLetter"));
        Assert.Single(filer.FileCalls);                                               // re-driven exactly once
    }

    // ---- T3: "Filed" append throws AFTER a successful upload ⇒ stays Filing; reconcile completes ----------------
    [Fact]
    public async Task T3_PostUploadFiledAppendThrows_StaysFiling_NotFiledNotDeadLettered_ReconcileCompletes()
    {
        using var store = NewStore();
        const string id = "t3";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);

        // Upload SUCCEEDS (bytes are live), but RECORDING the "Filed" event fails — the subtle dual-write in-doubt case.
        var audit = new FiledAppendFailingAudit(store) { FailFiledAppend = true };
        var filer = new RecordingFiler();
        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, audit, store, filer: filer);

        await proc.ProcessAsync(new UploadJob(id, PdfPath(id), "t3.pdf"), CancellationToken.None);

        Assert.Equal("Filing", await StateOf(store, id));        // NOT Filed (record failed), NOT DeadLetter (bytes uploaded)
        Assert.False(await HasEvent(store, id, "Filed"));
        Assert.False(await HasEvent(store, id, "DeadLetter"));
        Assert.Single(filer.FileCalls);                          // the upload DID happen — so dead-lettering would be wrong

        // Reconcile re-drives; now the "Filed" append succeeds → completes.
        audit.FailFiledAppend = false;
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Equal("Filed", await StateOf(store, id));
        Assert.False(await HasEvent(store, id, "DeadLetter"));
    }

    // ---- T4: reconcile re-drive is idempotent — no double-file, no DeadLetter -----------------------------------
    [Fact]
    public async Task T4_Reconcile_ReDrivingUploadedBytes_IsIdempotent_NoDoubleFile_NoDeadLetter()
    {
        using var store = NewStore();
        const string id = "t4";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);

        var filer = new RecordingFiler { ThrowOnFile = true };
        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store, filer: filer);

        await proc.ProcessAsync(new UploadJob(id, PdfPath(id), "t4.pdf"), CancellationToken.None);
        Assert.Equal("Filing", await StateOf(store, id));

        // First reconcile re-drives the recorded (idempotent) spec → Filed.
        filer.ThrowOnFile = false;
        await proc.ReconcileAsync(CancellationToken.None);
        Assert.Equal("Filed", await StateOf(store, id));
        Assert.Single(filer.FileCalls);                                      // re-driven once...
        Assert.Contains("Verordnung", filer.FileCalls[0].FileName);         // ...with the recorded canonical spec (replace-in-place)

        // Second reconcile: the doc is now Filed ⇒ NOT reconcilable ⇒ no second file, no dead-letter. Idempotent.
        await proc.ReconcileAsync(CancellationToken.None);
        Assert.Equal("Filed", await StateOf(store, id));
        Assert.Single(filer.FileCalls);                                      // STILL one — reconcile did not double-create
        Assert.False(await HasEvent(store, id, "DeadLetter"));
    }

    // ---- T5 (P4 FIX, formerly the gap): orphan-sidecar NeedsReview IS recovered --------------------------------
    // A doc in NeedsReview WITH an orphan Filing sidecar (the review-submit edge: sidecar written, then the "Filing"
    // append failed) is now RECOVERED by reconcile — keyed on the .filing.json file, re-driven to Filed idempotently.
    // (Was T5_GAP, which asserted the un-recovered gap; flipped by P4 to lock the FIX.)
    [Fact]
    public async Task T5_NeedsReviewWithOrphanFilingSidecar_IsRecoveredByReconcile_ReDrivenToFiled_Idempotent()
    {
        using var store = NewStore();
        const string id = "t5";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "NeedsReview", "test"));
        WriteCapturedPdf(id);

        // An orphan Filing sidecar (with a reviewer, as a failed review-submit would leave) next to the captured PDF.
        var spec = new FilingSpec($"2026-05-01_Verordnung_Z000000005_{id}.pdf", "Verordnung",
            new Dictionary<string, object?>(), "volltext", PdfPath(id));
        await UploadProcessor.WriteFilingSidecarAsync(PdfPath(id), spec, 1, CancellationToken.None, reviewer: "user:reviewer-1");
        Assert.True(File.Exists(UploadProcessor.FilingSidecarPath(PdfPath(id))));

        var filer = new RecordingFiler();
        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store, filer: filer);

        await proc.ReconcileAsync(CancellationToken.None);

        // RECOVERED: re-driven to Filed exactly once, with the recorded canonical spec; the orphan sidecar is cleared.
        Assert.Equal("Filed", await StateOf(store, id));
        Assert.Single(filer.FileCalls);
        Assert.Contains("Verordnung", filer.FileCalls[0].FileName);                    // the recorded canonical spec (replace-in-place)
        Assert.False(File.Exists(UploadProcessor.FilingSidecarPath(PdfPath(id))));     // orphan sidecar removed after recovery

        // Idempotent: a second reconcile — the doc is now Filed ⇒ not a candidate, no .filing.json ⇒ no second file.
        await proc.ReconcileAsync(CancellationToken.None);
        Assert.Equal("Filed", await StateOf(store, id));
        Assert.Single(filer.FileCalls);                                               // STILL one — not double-filed
        Assert.False(await HasEvent(store, id, "DeadLetter"));
    }

    // ---- T6 (P4 blast-radius): NeedsReview AWAITING A HUMAN is NOT touched --------------------------------------
    // The trigger keys on the .filing.json FILE, not NeedsReview state. A doc genuinely awaiting a human has a
    // .review.json (the review pre-fill) but NO .filing.json, so reconcile must leave it ALONE — re-driving it would
    // auto-file a document no human has approved. This proves T5's fix did not over-broaden to NeedsReview-in-general.
    [Fact]
    public async Task T6_NeedsReviewAwaitingHuman_NoFilingSidecar_IsNotTouchedByReconcile_NeverFiled()
    {
        using var store = NewStore();
        const string id = "t6";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "NeedsReview", "test"));
        WriteCapturedPdf(id);
        // the real await-human shape: a .review.json (review pre-fill), and crucially NO .filing.json
        var reviewSidecar = Path.Combine(_dir, $"{id}.review.json");
        File.WriteAllText(reviewSidecar, "{}");
        Assert.False(File.Exists(UploadProcessor.FilingSidecarPath(PdfPath(id))));     // no Filing sidecar present

        var filer = new RecordingFiler();
        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store, filer: filer);

        await proc.ReconcileAsync(CancellationToken.None);

        // UNTOUCHED: still awaiting a human, never filed.
        Assert.Equal("NeedsReview", await StateOf(store, id));
        Assert.Empty(filer.FileCalls);                                                // NEVER filed without human approval
        Assert.False(await HasEvent(store, id, "Filed"));
        Assert.True(File.Exists(reviewSidecar));                                      // review sidecar untouched
    }

    // ---- T7: supersede — interrupted AFTER the new doc is filed ⇒ reconcile completes the delete + Superseded event ---
    // The outbox invariant for the DELETE half. With the intent sidecar on disk (a crash between the new doc's Filed
    // event and the old doc's terminal Superseded event), reconcile re-drives idempotently: delete the OLD item by its
    // driveItemId, append the OLD doc's Superseded event, remove the sidecar.
    [Fact]
    public async Task T7_Supersede_InterruptedAfterFile_ReconcileCompletesDelete_SupersededEvent_SidecarRemoved()
    {
        using var store = NewStore();
        const string oldId = "t7old", newId = "t7new";
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(oldId, "Filed", "test", Details: Detail(new { driveItemId = "drv-old-7" })));
        // The NEW doc is already filed; the crash left only the supersede intent sidecar next to its (now-purged) PDF path.
        var intent = new UploadProcessor.SupersedeIntent(oldId, "drv-old-7", newId, "user:reviewer-7", "vom Nutzer ersetzt");
        await UploadProcessor.WriteSupersedeSidecarAsync(PdfPath(newId), intent, CancellationToken.None);
        Assert.True(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Equal("Superseded", await StateOf(store, oldId));                              // OLD doc marked terminal
        Assert.True(await HasEvent(store, oldId, "Superseded"));                              // with the terminal event
        Assert.Equal(new[] { "drv-old-7" }, filer.DeleteCalls);                               // deleted by driveItemId, exactly once
        Assert.False(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));      // intent sidecar cleared
    }

    // ---- T8: supersede — a still-failing delete leaves the intent for the NEXT sweep (never dropped) -----------------
    [Fact]
    public async Task T8_Supersede_DeleteStillFailing_KeepsSidecar_NoTerminalEvent_ThenRecovers()
    {
        using var store = NewStore();
        const string oldId = "t8old", newId = "t8new";
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(oldId, "Filed", "test", Details: Detail(new { driveItemId = "drv-old-8" })));
        var intent = new UploadProcessor.SupersedeIntent(oldId, "drv-old-8", newId, "user:reviewer-8", "vom Nutzer ersetzt");
        await UploadProcessor.WriteSupersedeSidecarAsync(PdfPath(newId), intent, CancellationToken.None);

        var filer = new RecordingFiler { ThrowOnDelete = true };                              // Graph delete transiently down
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.NotEqual("Superseded", await StateOf(store, oldId));                            // NOT marked (delete never succeeded)
        Assert.False(await HasEvent(store, oldId, "Superseded"));
        Assert.True(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));        // intent KEPT for the next sweep

        filer.ThrowOnDelete = false;                                                          // Graph recovers
        await proc.ReconcileAsync(CancellationToken.None);
        Assert.Equal("Superseded", await StateOf(store, oldId));                              // now completed
        Assert.False(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));
    }

    // ---- T9: supersede state-guard — a re-drive when the OLD doc is ALREADY Superseded never double-appends ----------
    // Crash AFTER the terminal append but BEFORE the sidecar was removed: reconcile finds the sidecar, re-drives the
    // delete (idempotent — a 404 is success at the sink), but the STATE-GUARD (old already Superseded) skips a second
    // terminal append. Proves recovery can never double-append the Superseded event.
    [Fact]
    public async Task T9_Supersede_ReDriveWhenAlreadySuperseded_DoesNotDoubleAppend_StateGuard()
    {
        using var store = NewStore();
        const string oldId = "t9old", newId = "t9new";
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(oldId, "Filed", "test", Details: Detail(new { driveItemId = "drv-old-9" })));
        await store.AppendAsync(new AuditEntry(oldId, "Superseded", "user:reviewer-9", Details: Detail(new { supersededBy = newId, driveItemIdDeleted = "drv-old-9" }))); // already terminal
        var intent = new UploadProcessor.SupersedeIntent(oldId, "drv-old-9", newId, "user:reviewer-9", "vom Nutzer ersetzt");
        await UploadProcessor.WriteSupersedeSidecarAsync(PdfPath(newId), intent, CancellationToken.None); // sidecar not yet removed

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Equal("Superseded", await StateOf(store, oldId));
        Assert.Equal(1, (await store.GetEventsAsync(oldId)).Count(e => e.Type == "Superseded")); // STILL exactly one — no double-append
        Assert.Equal(new[] { "drv-old-9" }, filer.DeleteCalls);                                 // idempotent re-delete still ran (404-safe)
        Assert.False(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));         // sidecar cleared
    }

    // ---- T24: supersede locator tripwire — a STALE intent (the predecessor re-filed underneath it) is REFUSED --------
    // B-1's stale-intent half, which needs no race at all: the inline delete failed (intent left on disk), the
    // predecessor was reclassified and re-filed at a NEW locator, and only then does reconcile re-drive the intent.
    // Without the tripwire the delete fires on the dead id (a real Graph 404 folds to success), the state guard passes
    // (the doc is Filed, not Superseded), and a terminal Superseded lands on a document whose REAL item is live.
    // With it — mirroring DriveReclassifyAsync's guard, comparison inverted — no delete, no append, sidecar kept.
    [Fact]
    public async Task T24_Supersede_StaleIntent_PredecessorReFiled_RefusesDelete_KeepsSidecar_NoTerminalEvent()
    {
        using var store = NewStore();
        const string oldId = "t24old", newId = "t24new";
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(oldId, "Filed", "test", Details: Detail(new { driveItemId = "drv-old-10" })));
        // The intent names drv-old-10; the document then re-files (reclassify) at a NEW locator underneath it.
        var intent = new UploadProcessor.SupersedeIntent(oldId, "drv-old-10", newId, "user:reviewer-10", "vom Nutzer ersetzt");
        await UploadProcessor.WriteSupersedeSidecarAsync(PdfPath(newId), intent, CancellationToken.None);
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(oldId, "Filed", "test", Details: Detail(new { driveItemId = "drv-new-10" })));

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Empty(filer.DeleteCalls);                                                     // the stale delete never fires
        Assert.False(await HasEvent(store, oldId, "Superseded"));                            // no terminal event on a live document
        Assert.Equal("Filed", await StateOf(store, oldId));
        Assert.True(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));      // kept, loudly, for a human
    }

    // ---- T25: the drive rides the intent (C-2) — a supersede re-drive deletes in the RECORDED drive -------------------
    // Identity vs address space: the tripwire above passes here (the ledger's locator still equals the intent's), so
    // what protects a business-filed predecessor is the recorded destination on the intent. Without it the delete is
    // addressed through the medical default, 404s on the business item, folds to success, and the terminal event lands
    // while the real item stays live. The fake records the target each delete was addressed through.
    [Fact]
    public async Task T25_Supersede_ReDrive_AddressesTheDelete_ThroughTheIntentsRecordedDrive()
    {
        using var store = NewStore();
        const string oldId = "t25old", newId = "t25new";
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(oldId, "Filed", "test", Details: Detail(new { domain = "Business", library = "Business – Eingang", driveItemId = "drv-biz-11" })));
        var intent = new UploadProcessor.SupersedeIntent(oldId, "drv-biz-11", newId, "user:reviewer-11", "vom Nutzer ersetzt",
            TargetSiteId: "site-biz", TargetLibrary: "Business – Eingang");
        await UploadProcessor.WriteSupersedeSidecarAsync(PdfPath(newId), intent, CancellationToken.None);

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Equal(new[] { "drv-biz-11" }, filer.DeleteCalls);
        var target = Assert.Single(filer.DeleteTargets);
        Assert.NotNull(target);                                                              // NOT the medical default
        Assert.Equal("site-biz", target!.SiteId);
        Assert.Equal("Business – Eingang", target.Library);
        Assert.Equal("Superseded", await StateOf(store, oldId));                             // the supersede itself completes normally
    }

    // ---- T26: supersede state tripwire — a predecessor that LEFT Filed (mid-reprocess) refuses the re-drive ----------
    // The ordering the locator tripwire alone cannot see, and the predecessor claim cannot cover: the inline delete
    // failed (intent on disk), /api/reprocess then ran to completion — the predecessor is now mid-pipeline
    // (Received/NeedsReview), NOT yet re-filed — and only then does the re-drive fire. State is not Filed, so the
    // locator tripwire's pattern never matches; without the state tripwire the delete fires and a terminal Superseded
    // lands over a LIVE document a worker is about to advance. The intent's premise (SupersedeGuard: predecessor
    // Filed) is gone, so the whole intent is stale: refuse, keep the sidecar, no terminal event.
    [Fact]
    public async Task T26_Supersede_StaleIntent_PredecessorMidReprocess_RefusesDelete_KeepsSidecar_NoTerminalEvent()
    {
        using var store = NewStore();
        const string oldId = "t26old", newId = "t26new";
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(oldId, "Filed", "test", Details: Detail(new { driveItemId = "drv-old-23" })));
        var intent = new UploadProcessor.SupersedeIntent(oldId, "drv-old-23", newId, "user:reviewer-23", "vom Nutzer ersetzt");
        await UploadProcessor.WriteSupersedeSidecarAsync(PdfPath(newId), intent, CancellationToken.None);
        // The predecessor is reprocessed before the re-drive fires: it leaves Filed and re-enters the pipeline.
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test", Details: Detail(new { channel = "Reclassify" })));
        await store.AppendAsync(new AuditEntry(oldId, "NeedsReview", "test"));

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Empty(filer.DeleteCalls);                                                     // the stale delete never fires
        Assert.False(await HasEvent(store, oldId, "Superseded"));                            // no terminal event over a live document
        Assert.Equal("NeedsReview", await StateOf(store, oldId));                            // the reprocess keeps its document
        Assert.True(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));      // kept, loudly, for a human
    }

    // ---- T27: RecordedFiledTargetAsync — the ledger's domain/library marker resolves the drive; absence = medical ----
    [Fact]
    public async Task T27_RecordedFiledTarget_BusinessMarkerResolvesBusinessDrive_MedicalAndUnfiledResolveNull()
    {
        using var store = NewStore();
        var spOpt = new Adelaide.SharePoint.SharePointOptions("tenant", "client", "secret", "site-med",
            BusinessLibrary: "Business – Eingang", BusinessSiteId: "site-biz", BusinessContentType: "Document");

        await store.AppendAsync(new AuditEntry("t12biz", "Filed", "sharepoint", Details: Detail(new { domain = "Business", library = "Alt-Bibliothek", driveItemId = "drv-b" })));
        await store.AppendAsync(new AuditEntry("t12med", "Filed", "sharepoint", Details: Detail(new { contentType = "Verordnung", driveItemId = "drv-m" })));

        var biz = await UploadProcessor.RecordedFiledTargetAsync(store, spOpt, "t12biz", CancellationToken.None);
        Assert.NotNull(biz);
        Assert.Equal("site-biz", biz!.SiteId);                       // a pre-siteId-key event: the site falls back to configuration
        Assert.Equal("Alt-Bibliothek", biz.Library);                 // the library PREFERS the recorded string over current config

        // BOTH recorded axes are preferred over current config: an event carrying siteId pins the site too, so a
        // later SP_BUSINESS_SITE_ID cutover cannot silently re-address history (review finding).
        await store.AppendAsync(new AuditEntry("t12site", "Filed", "sharepoint", Details: Detail(new { domain = "Business", siteId = "site-old", library = "Alt-Bibliothek", driveItemId = "drv-s" })));
        var pinned = await UploadProcessor.RecordedFiledTargetAsync(store, spOpt, "t12site", CancellationToken.None);
        Assert.Equal("site-old", pinned!.SiteId);                    // NOT the configured "site-biz"
        Assert.Equal("Alt-Bibliothek", pinned.Library);

        Assert.Null(await UploadProcessor.RecordedFiledTargetAsync(store, spOpt, "t12med", CancellationToken.None)); // medical → the sink's default
        Assert.Null(await UploadProcessor.RecordedFiledTargetAsync(store, spOpt, "t12nix", CancellationToken.None)); // never filed

        // A re-file wins: the LAST Filed event decides (a reclassify can move a document between lanes).
        await store.AppendAsync(new AuditEntry("t12med", "Received", "test"));
        await store.AppendAsync(new AuditEntry("t12med", "Filed", "sharepoint", Details: Detail(new { domain = "Business", library = "Business – Eingang", driveItemId = "drv-m2" })));
        Assert.Equal("Business – Eingang", (await UploadProcessor.RecordedFiledTargetAsync(store, spOpt, "t12med", CancellationToken.None))!.Library);
    }

    // ---- T28: rotate moot guard — inherits DocumentStates.Settled, and never destroys an un-audited intent ----------
    // Fix 2 (A2): the guard's hand-written list omitted Segmented/SeededHistory, so a lingering rotate intent on a
    // terminal parent appended Rotated — an ADVANCING event — flipping the projection live and letting reconcile
    // re-file already-filed content. Parameterised over the SHARED terminal set so a future terminal state inherits
    // the guard automatically (the DocumentStates.cs docstring's promise). Fix 1's third site (the same branch): a
    // moot intent whose bytes were changed with NO Rotated event in the chain is the LAST evidence of an un-audited
    // byte change — it must be kept, not dropped; an intent whose event is in the chain, or whose bytes never
    // changed, is genuinely spent and is dropped.
    [Fact]
    public async Task T28_RotateMootGuard_InheritsTerminal_NeverAppends_AndKeepsAnUnauditedIntent()
    {
        using var store = NewStore();
        var mootStates = DocumentStates.Settled.Concat(new[] { "DeadLetter" }).ToArray(); // DeadLetter: parked for a human — deliberately moot
        for (var i = 0; i < mootStates.Length; i++)
        {
            var id = $"t28x{i}";
            await store.AppendAsync(new AuditEntry(id, "Received", "test"));
            await store.AppendAsync(new AuditEntry(id, mootStates[i], "test"));
            var bytes = System.Text.Encoding.ASCII.GetBytes($"%PDF-fake-{id}"); // moot path never parses the PDF
            await File.WriteAllBytesAsync(PdfPath(id), bytes);
            // PreHash deliberately ≠ the on-disk hash and NO Rotated event in the chain: an un-audited byte change.
            var intent = new UploadProcessor.RotateIntent(id, new Dictionary<int, int> { [1] = 90 }, "0000aabbccdd", "user:t28");
            await UploadProcessor.WriteRotateSidecarAsync(PdfPath(id), intent, CancellationToken.None);

            await UploadProcessor.DriveRotateAsync(store, PdfPath(id), intent, NullLogger.Instance, CancellationToken.None);

            Assert.False(await HasEvent(store, id, "Rotated"));                                 // never re-mutates a settled/parked doc
            Assert.Equal(mootStates[i], await StateOf(store, id));                              // the resurrection is the whole finding
            Assert.Equal(bytes, await File.ReadAllBytesAsync(PdfPath(id)));                     // bytes untouched
            Assert.True(File.Exists(UploadProcessor.RotateSidecarPath(PdfPath(id))),            // the un-audited intent is KEPT for a human
                $"the moot branch destroyed the last evidence of an un-audited byte change (state {mootStates[i]})");
        }

        // Genuinely spent intents ARE dropped — (a) the chain has the event (matched on the intent's PreHash):
        const string done = "t28done";
        await store.AppendAsync(new AuditEntry(done, "Received", "test"));
        var doneBytes = System.Text.Encoding.ASCII.GetBytes("%PDF-fake-done");
        await File.WriteAllBytesAsync(PdfPath(done), doneBytes);
        var donePre = UploadProcessor.Sha256Hex(System.Text.Encoding.ASCII.GetBytes("the pre-swap bytes"));
        await store.AppendAsync(new AuditEntry(done, "Rotated", "test", Details: Detail(new { pages = new { }, preHash = donePre, postHash = "ffff" })));
        await store.AppendAsync(new AuditEntry(done, "Filed", "test", Details: Detail(new { driveItemId = "drv-t28" })));
        var doneIntent = new UploadProcessor.RotateIntent(done, new Dictionary<int, int> { [1] = 90 }, donePre, "user:t28");
        await UploadProcessor.WriteRotateSidecarAsync(PdfPath(done), doneIntent, CancellationToken.None);
        await UploadProcessor.DriveRotateAsync(store, PdfPath(done), doneIntent, NullLogger.Instance, CancellationToken.None);
        Assert.False(File.Exists(UploadProcessor.RotateSidecarPath(PdfPath(done))));            // recorded → spent → dropped
        Assert.Equal(1, (await store.GetEventsAsync(done)).Count(e => e.Type == "Rotated"));    // and never double-appended

        // ...and (b) the bytes never changed (the throw came before the swap):
        const string clean = "t28clean";
        await store.AppendAsync(new AuditEntry(clean, "Received", "test"));
        await store.AppendAsync(new AuditEntry(clean, "Filed", "test", Details: Detail(new { driveItemId = "drv-t28c" })));
        var cleanBytes = System.Text.Encoding.ASCII.GetBytes("%PDF-fake-clean");
        await File.WriteAllBytesAsync(PdfPath(clean), cleanBytes);
        var cleanIntent = new UploadProcessor.RotateIntent(clean, new Dictionary<int, int> { [1] = 90 }, UploadProcessor.Sha256Hex(cleanBytes), "user:t28");
        await UploadProcessor.WriteRotateSidecarAsync(PdfPath(clean), cleanIntent, CancellationToken.None);
        await UploadProcessor.DriveRotateAsync(store, PdfPath(clean), cleanIntent, NullLogger.Instance, CancellationToken.None);
        Assert.False(File.Exists(UploadProcessor.RotateSidecarPath(PdfPath(clean))));           // nothing was baked → nothing to audit → dropped
        Assert.False(await HasEvent(store, clean, "Rotated"));
    }

    // ---- T29: the D-1 re-read gate — a doc that turned TERMINAL between the list query and the claim is skipped -----
    // The re-read gate tested `== "Filed"`, one of five terminal states, while the list-time gate excluded all five.
    // A document disposed (Discarded/Superseded/Segmented/SeededHistory) in that window fell through to the upload
    // and got a terminal "Filed" appended OVER the disposition — reconcile filing a document a human just disposed
    // of. The store/audit seam split reproduces the window deterministically: the STORE (candidate admission via the
    // orphan-sidecar arm) sees the true NeedsReview, the AUDIT (the re-read under the claim) reports the document
    // already terminal. Parameterised over the SHARED terminal set so a future state inherits the gate.
    [Fact]
    public async Task T29_ReconcileReReadGate_SkipsEveryTerminalState_NoUploadNoFiledAppend()
    {
        for (var i = 0; i < DocumentStates.Settled.Length; i++)
        {
            var state = DocumentStates.Settled[i];
            using var store = NewStore();
            var id = $"t29x{i}";
            await store.AppendAsync(new AuditEntry(id, "Received", "test"));
            await store.AppendAsync(new AuditEntry(id, "NeedsReview", "test"));
            WriteCapturedPdf(id);
            var spec = new FilingSpec($"t29-{i}.pdf", "Document", new Dictionary<string, object?>(), "", PdfPath(id));
            await UploadProcessor.WriteFilingSidecarAsync(PdfPath(id), spec, patientLookupId: null, CancellationToken.None, reviewer: "user:reviewer-29");

            var filer = new RecordingFiler();
            var lyingAudit = new TerminalOnReReadAudit(store, state);
            var proc = Build(Channel.CreateUnbounded<UploadJob>(), lyingAudit, store, filer: filer);
            await proc.ReconcileAsync(CancellationToken.None);

            Assert.Empty(filer.FileCalls);                                                  // no upload for a disposed doc
            Assert.False(await HasEvent(store, id, "Filed"),                                // no terminal Filed over the disposition
                $"reconcile filed a document that had turned {state} under it (D-1)");
            Assert.True(File.Exists(UploadProcessor.FilingSidecarPath(PdfPath(id))));       // sidecar KEPT for the reaper
            File.Delete(UploadProcessor.FilingSidecarPath(PdfPath(id)));                    // isolate iterations
            File.Delete(PdfPath(id));
        }
    }

    // ---- T30: the startup reaper takes the full inherited settled set — Segmented/SeededHistory sidecars are reaped --
    // D-1's fix keeps a terminal doc's sidecar "for the reaper"; the reaper's hand-written four-state list omitted
    // Segmented and SeededHistory, which would have turned every such sidecar into an immortal PHI-bearing file
    // (enumerated on every sweep, skipped forever). Settled = DocumentStates.Settled + DeadLetter (parked docs are
    // revivable, but a retry reprocesses from scratch and never replays a stale filing intent).
    [Fact]
    public async Task T30_Reaper_ReapsSidecarsUnderEverySettledState_AndKeepsCompletableOnes()
    {
        using var store = NewStore();
        var settled = DocumentStates.Settled.Concat(new[] { "DeadLetter" }).ToArray();
        for (var i = 0; i < settled.Length; i++)
        {
            var id = $"t30x{i}";
            await store.AppendAsync(new AuditEntry(id, "Received", "test"));
            await store.AppendAsync(new AuditEntry(id, settled[i], "test"));
            File.WriteAllText(Path.Combine(_dir, $"{id}.filing.json"), "{}");
        }
        const string live = "t30live";
        await store.AppendAsync(new AuditEntry(live, "Received", "test"));
        await store.AppendAsync(new AuditEntry(live, "NeedsReview", "test"));
        File.WriteAllText(Path.Combine(_dir, $"{live}.filing.json"), "{}");

        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store);
        await proc.ReapOrphanSidecarsAsync(CancellationToken.None);

        for (var i = 0; i < settled.Length; i++)
            Assert.False(File.Exists(Path.Combine(_dir, $"t30x{i}.filing.json")),
                $"the reaper kept a settled ({settled[i]}) doc's sidecar — the immortal-PHI-sidecar shape (A8)");
        Assert.True(File.Exists(Path.Combine(_dir, $"{live}.filing.json")));                // still completable → kept
    }

    // ---- T31: the split-recovery pass — a COMMITTED fan-out is completed from the recorded ranges; an aborted one ----
    // is dropped. The Segmented event is the commit; the .split.json is the fan-out recovery payload, not the commit.
    [Fact]
    public async Task T31_SplitRecovery_CompletesACommittedFanOut_AndDropsAnAbortedIntent()
    {
        using var store = NewStore();

        // (a) COMMITTED: parent has the Segmented event, no children exist yet (crash right after the commit).
        const string parent = "t31par";
        await store.AppendAsync(new AuditEntry(parent, "Received", "test", Details: Detail(new { sha256 = "aa11" })));
        await store.AppendAsync(new AuditEntry(parent, "NeedsReview", "test"));
        await store.AppendAsync(new AuditEntry(parent, "Segmented", "user:reviewer-31", Details: Detail(new { unitCount = 2, evidence = "manual" })));
        using (var doc = new PdfSharp.Pdf.PdfDocument())                                     // a REAL 4-page PDF: recovery re-derives by ExtractRange
        {
            for (var p = 0; p < 4; p++) doc.AddPage();
            using var ms = new MemoryStream();
            doc.Save(ms);
            await File.WriteAllBytesAsync(PdfPath(parent), ms.ToArray());
        }
        var intent = new UploadProcessor.SplitIntent(parent, "split-manual-v1", "user:reviewer-31", new[]
        {
            new UploadProcessor.SplitUnit($"{parent}-u1", 1, 2, "Teil 1"),
            new UploadProcessor.SplitUnit($"{parent}-u2", 3, 4, "Teil 2"),
        });
        await UploadProcessor.WriteSplitSidecarAsync(PdfPath(parent), intent, CancellationToken.None);

        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.True(File.Exists(PdfPath($"{parent}-u1")));                                   // children derived from the recorded ranges
        Assert.True(File.Exists(PdfPath($"{parent}-u2")));
        Assert.Equal("Received", await StateOf(store, $"{parent}-u1"));                      // and recorded
        Assert.Equal("Teil 1", (await store.GetDocumentAsync($"{parent}-u1"))!.OriginalName);
        Assert.False(File.Exists(UploadProcessor.SplitSidecarPath(PdfPath(parent))));        // intent spent
        Assert.Equal("Segmented", await StateOf(store, parent));                             // the parent stays terminal
        var enqueued = new List<string>();
        while (queue.Reader.TryRead(out var job)) enqueued.Add(job.DocId);
        Assert.Contains($"{parent}-u1", enqueued);                                           // minted children re-enter the pipeline
        Assert.Contains($"{parent}-u2", enqueued);

        // (b) ABORTED: no Segmented event — the commit failed and the endpoint surfaced it. Nothing durable exists;
        // the intent is dropped so the next split attempt starts clean.
        const string aborted = "t31abo";
        await store.AppendAsync(new AuditEntry(aborted, "Received", "test"));
        await store.AppendAsync(new AuditEntry(aborted, "NeedsReview", "test"));
        WriteCapturedPdf(aborted);
        var abortedIntent = new UploadProcessor.SplitIntent(aborted, "split-manual-v1", "user:reviewer-31", new[]
        {
            new UploadProcessor.SplitUnit($"{aborted}-u1", 1, 1, "Teil 1"),
        });
        await UploadProcessor.WriteSplitSidecarAsync(PdfPath(aborted), abortedIntent, CancellationToken.None);

        await proc.ReconcileAsync(CancellationToken.None);

        Assert.False(File.Exists(UploadProcessor.SplitSidecarPath(PdfPath(aborted))));       // aborted commit → intent dropped
        Assert.False(File.Exists(PdfPath($"{aborted}-u1")));                                 // and no child was derived
        Assert.Null(await store.GetDocumentAsync($"{aborted}-u1"));
    }

    // ---- T32: the supersede re-drive claims the PREDECESSOR — contention skips the tick, the sidecar stays ----------
    // Fix 4 (B-2): supersede mutates TWO documents but claimed only the successor, so /api/reprocess could hold or
    // take doc:{predecessor} while a re-drive deleted its item and appended its terminal event. The re-drive now
    // claims the predecessor too; a live holder means "not this sweep's to touch".
    [Fact]
    public async Task T32_SupersedeReDrive_SkipsWhenThePredecessorIsClaimed_ThenCompletesOnceReleased()
    {
        using var store = NewStore();
        const string oldId = "t32old", newId = "t32new";
        await store.AppendAsync(new AuditEntry(oldId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(oldId, "Filed", "test", Details: Detail(new { driveItemId = "drv-old-32" })));
        var intent = new UploadProcessor.SupersedeIntent(oldId, "drv-old-32", newId, "user:reviewer-32", "vom Nutzer ersetzt");
        await UploadProcessor.WriteSupersedeSidecarAsync(PdfPath(newId), intent, CancellationToken.None);

        var claims = new FilingClaims();
        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer, claims: claims);

        using (var live = claims.Acquire("doc:" + oldId))          // a live path (e.g. /api/reprocess) owns the predecessor
        {
            Assert.True(live.Acquired);
            await proc.ReconcileAsync(CancellationToken.None);
            Assert.Empty(filer.DeleteCalls);                                                 // not this sweep's to touch
            Assert.False(await HasEvent(store, oldId, "Superseded"));
            Assert.True(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));  // stays for the next tick
        }

        await proc.ReconcileAsync(CancellationToken.None);          // predecessor released → the re-drive completes
        Assert.Equal(new[] { "drv-old-32" }, filer.DeleteCalls);
        Assert.Equal("Superseded", await StateOf(store, oldId));
        Assert.False(File.Exists(UploadProcessor.SupersedeSidecarPath(PdfPath(newId))));
    }

    // ---- T33: a per-document recovery failure COUNTS on SweepHealth (D-2) -------------------------------------------
    // The per-document catches retry by design, so before this fix a document failing the SAME way every tick was
    // ~720 healthy-looking ticks a day: MarkTick fired, reconcileFailuresSinceStart read zero (the counter sat in the
    // outer catch only), and the gauge could not see the document (NeedsReview is human-pending by construction).
    [Fact]
    public async Task T33_PerDocumentRecoveryFailure_IncrementsReconcileFailures()
    {
        using var store = NewStore();
        const string id = "t33";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "NeedsReview", "test"));
        WriteCapturedPdf(id);
        var spec = new FilingSpec("t33.pdf", "Document", new Dictionary<string, object?>(), "", PdfPath(id));
        await UploadProcessor.WriteFilingSidecarAsync(PdfPath(id), spec, patientLookupId: null, CancellationToken.None, reviewer: "user:reviewer-33");

        var filer = new RecordingFiler { ThrowOnFile = true };      // the permanently failing shape (e.g. a rejected required column)
        var sweepHealth = new SweepHealth();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer, sweepHealth: sweepHealth);

        await proc.ReconcileAsync(CancellationToken.None);
        await proc.ReconcileAsync(CancellationToken.None);          // the loop that used to read as healthy ticks

        var snap = System.Text.Json.JsonSerializer.SerializeToElement(sweepHealth.Snapshot());
        Assert.Equal(2, snap.GetProperty("reconcileFailuresSinceStart").GetInt64());
    }

    // ---- T9a–T9e: the RECLASSIFY delete outbox (fourth member of the supersede-outbox family) ------------------------
    // /api/reprocess deletes the previously-filed SharePoint item. Before this slice that delete had no intent record,
    // so a transient Graph failure left the old item live while the document re-filed — a duplicate. The intent is now
    // written AFTER the write-ahead "Received" (never before: a sidecar while the ledger still said Filed would let
    // recovery delete the item out from under a terminal record) and re-driven by reconcile until it lands.

    [Fact]
    public async Task T9a_ReclassifyIntent_DrivesTheDelete_DropsSidecar_AndRecordsTheDeferredCompletion()
    {
        using var store = NewStore();
        const string id = "t9a";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "Filed", "test", Details: Detail(new { driveItemId = "drv-r-a" })));
        // The write-ahead half already ran: the doc is out of Filed before any intent exists.
        await store.AppendAsync(new AuditEntry(id, "Received", "user:op", Details: Detail(new { channel = "Reclassify", driveItemIdPendingRemoval = "drv-r-a" })));
        var before = (await store.GetEventsAsync(id)).Count;

        var intent = new UploadProcessor.ReclassifyIntent(id, "drv-r-a", "user:op");
        await UploadProcessor.WriteReclassifySidecarAsync(PdfPath(id), intent, CancellationToken.None);

        var filer = new RecordingFiler();
        await UploadProcessor.DriveReclassifyAsync(filer, store, PdfPath(id), intent, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new[] { "drv-r-a" }, filer.DeleteCalls);
        Assert.False(File.Exists(UploadProcessor.ReclassifySidecarPath(PdfPath(id))));

        // The deferred completion IS recorded — that is the whole point; the chain previously could not say the item
        // had gone. It is an ANNOTATION, so it chains (EventCount grows) but does NOT move the lifecycle state.
        var events = await store.GetEventsAsync(id);
        Assert.Equal(before + 1, events.Count);
        Assert.Equal(AuditEventTypes.SharePointItemRemoved, events[^1].Type);
        Assert.Contains("drv-r-a", events[^1].Details);
        Assert.Equal("Received", await StateOf(store, id));   // state untouched by the annotation
    }

    [Fact]
    public async Task T9b_ReclassifyIntent_RefusesToDelete_WhileTheLedgerStillHasItFiledAtThatLocator()
    {
        using var store = NewStore();
        const string id = "t9b";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "Filed", "test", Details: Detail(new { driveItemId = "drv-r-b" })));
        // NO write-ahead event: the ledger still says Filed AT THIS LOCATOR. Deleting now would recreate the exact
        // silent loss the family exists to prevent — a live Filed record pointing at a destroyed item.
        var intent = new UploadProcessor.ReclassifyIntent(id, "drv-r-b", "user:op");
        await UploadProcessor.WriteReclassifySidecarAsync(PdfPath(id), intent, CancellationToken.None);

        var filer = new RecordingFiler();
        await UploadProcessor.DriveReclassifyAsync(filer, store, PdfPath(id), intent, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(filer.DeleteCalls);                                                        // refused
        Assert.True(File.Exists(UploadProcessor.ReclassifySidecarPath(PdfPath(id))));            // kept for a human
        Assert.Equal("Filed", await StateOf(store, id));
    }

    [Fact]
    public async Task T9c_ReclassifyReDrive_IsIdempotent_AfterTheDocumentHasRefiledElsewhere()
    {
        using var store = NewStore();
        const string id = "t9c";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "Filed", "test", Details: Detail(new { driveItemId = "drv-r-c-old" })));
        await store.AppendAsync(new AuditEntry(id, "Received", "user:op", Details: Detail(new { channel = "Reclassify", driveItemIdPendingRemoval = "drv-r-c-old" })));
        // It already re-filed to a NEW item. The guard must NOT fire — the intent names the OLD locator, which is
        // exactly what still has to go; only a Filed-at-the-SAME-locator means the write-ahead is missing.
        await store.AppendAsync(new AuditEntry(id, "Filed", "test", Details: Detail(new { driveItemId = "drv-r-c-new" })));

        var intent = new UploadProcessor.ReclassifyIntent(id, "drv-r-c-old", "user:op");
        var filer = new RecordingFiler();
        var before = (await store.GetEventsAsync(id)).Count;

        await UploadProcessor.WriteReclassifySidecarAsync(PdfPath(id), intent, CancellationToken.None);
        await UploadProcessor.DriveReclassifyAsync(filer, store, PdfPath(id), intent, NullLogger.Instance, CancellationToken.None);
        await UploadProcessor.DriveReclassifyAsync(filer, store, PdfPath(id), intent, NullLogger.Instance, CancellationToken.None); // re-drive

        Assert.Equal(new[] { "drv-r-c-old", "drv-r-c-old" }, filer.DeleteCalls); // 404 folds to success, so repeating is free
        // The DELETE repeats; the ANNOTATION does not. At-most-once is keyed on the intent, because an annotation
        // cannot use the state guard supersede relies on — not moving State is precisely what it is for. Without this,
        // an intent that could never be cleared would append one immutable row per sweep, forever.
        Assert.Equal(before + 1, (await store.GetEventsAsync(id)).Count);
        // And it landed on a document in a TERMINAL state without disturbing it. An ordinary event would have reset
        // State to its own type and re-run the pipeline for an already-filed document.
        Assert.Equal("Filed", await StateOf(store, id));
    }

    [Fact]
    public async Task T9d_ReconcileFindsAnOrphanReclassifyIntent_AndCompletesTheDelete()
    {
        using var store = NewStore();
        const string id = "t9d";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "Filed", "test", Details: Detail(new { driveItemId = "drv-r-d" })));
        await store.AppendAsync(new AuditEntry(id, "Received", "user:op", Details: Detail(new { channel = "Reclassify", driveItemIdPendingRemoval = "drv-r-d" })));
        WriteCapturedPdf(id);
        await UploadProcessor.WriteReclassifySidecarAsync(PdfPath(id),
            new UploadProcessor.ReclassifyIntent(id, "drv-r-d", "user:op"), CancellationToken.None);

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Contains("drv-r-d", filer.DeleteCalls);
        Assert.False(File.Exists(UploadProcessor.ReclassifySidecarPath(PdfPath(id))));
    }

    [Fact]
    public async Task T9e_CorruptReclassifyIntent_NeverTriggersADelete()
    {
        using var store = NewStore();
        const string id = "t9e";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);
        await File.WriteAllTextAsync(UploadProcessor.ReclassifySidecarPath(PdfPath(id)), "{ not json");

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Null(UploadProcessor.ReadReclassifySidecar(PdfPath(id)));
        Assert.Empty(filer.DeleteCalls);                                            // a bad intent must NEVER delete
        Assert.True(File.Exists(UploadProcessor.ReclassifySidecarPath(PdfPath(id)))); // left for inspection
    }

    // ---- T10: BUSINESS filing — the outbox invariant holds for a PATIENT-LESS spec -----------------------------------
    // The top correctness risk: a business filing interrupted before "Filed" must be COMPLETED by reconcile, to the
    // BUSINESS library (from the FilingTarget round-tripped through the sidecar), with NO patient — never Patient Records.
    // Shaped like the review-submit edge (NeedsReview doc + an orphan .filing.json), exactly as T5 does for medical.
    [Fact]
    public async Task T10_BusinessFiling_OrphanSidecar_IsRecoveredToBusinessLibrary_NoPatient_Idempotent()
    {
        using var store = NewStore();
        const string id = "t10";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "NeedsReview", "test")); // a stuck no-patient doc, as in production
        WriteCapturedPdf(id);

        // A BUSINESS filing sidecar: business target, NO patient (patientLookupId null), review-origin (a reviewer).
        var target = new FilingTarget("site-b", "Business – Eingang", "Document", RequiresPatient: false);
        var spec = SharePointSink.BuildBusinessSpec(
            new IncomingDocument(id, PdfPath(id), SourceChannel.Api, "invoice.pdf"),
            new DocumentUnit(id, 1, 1, new Transcript("md", 1, 1.0, Array.Empty<DecodedBarcode>())),
            target, "Document", "2026-05-01");
        await UploadProcessor.WriteFilingSidecarAsync(PdfPath(id), spec, patientLookupId: null, CancellationToken.None, reviewer: "user:reviewer-b");
        Assert.True(File.Exists(UploadProcessor.FilingSidecarPath(PdfPath(id))));

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);

        await proc.ReconcileAsync(CancellationToken.None);

        // Recovered to Filed, re-driven exactly once, to the BUSINESS destination, with NO patient lookup.
        Assert.Equal("Filed", await StateOf(store, id));
        Assert.Single(filer.FileCalls);
        var filed = filer.FileCalls[0];
        Assert.Equal("Business – Eingang", filed.Target?.Library);         // the target round-tripped through the sidecar
        Assert.Equal("site-b", filed.Target?.SiteId);
        Assert.False(filed.Fields.ContainsKey("PatientLookupId"));         // never a patient lookup
        Assert.StartsWith("2026-05-01_Document_", filed.FileName);         // the recorded business canonical filename (no KVNR)
        Assert.Null((await store.GetDocumentAsync(id))!.PatientListItemId); // the projection carries no patient
        // The recovered Filed EVENT carries the destination markers (C-2): the resolver reads the LAST Filed event,
        // so a recovered business filing without domain/library would address every later by-id operation medically.
        // Parsed, not substring-matched: the serializer escapes the library name's en-dash.
        var recoveredFiled = (await store.GetEventsAsync(id)).Last(e => e.Type == "Filed");
        using (var filedDetails = System.Text.Json.JsonDocument.Parse(recoveredFiled.Details))
        {
            Assert.Equal("Business", filedDetails.RootElement.GetProperty("domain").GetString());
            Assert.Equal("site-b", filedDetails.RootElement.GetProperty("siteId").GetString());
            Assert.Equal("Business – Eingang", filedDetails.RootElement.GetProperty("library").GetString());
        }
        Assert.False(File.Exists(UploadProcessor.FilingSidecarPath(PdfPath(id)))); // orphan sidecar cleared after recovery

        // Idempotent: a second reconcile is a no-op (now Filed, sidecar gone) — no double-file, no dead-letter.
        await proc.ReconcileAsync(CancellationToken.None);
        Assert.Single(filer.FileCalls);
        Assert.False(await HasEvent(store, id, "DeadLetter"));
    }

    // ---- T11: the business FilingTarget round-trips write→read through the sidecar (the crux of crash-safe recovery) ----
    [Fact]
    public async Task T11_BusinessFilingSidecar_RoundTripsTargetAndNoPatient()
    {
        const string id = "t11";
        var target = new FilingTarget("site-b", "Business – Eingang", "Document", RequiresPatient: false);
        var spec = SharePointSink.BuildBusinessSpec(
            new IncomingDocument(id, PdfPath(id), SourceChannel.Api, "invoice.pdf"),
            new DocumentUnit(id, 1, 1, new Transcript("md", 1, 1.0, Array.Empty<DecodedBarcode>())),
            target, "Document", "2026-05-01");
        await UploadProcessor.WriteFilingSidecarAsync(PdfPath(id), spec, patientLookupId: null, CancellationToken.None, reviewer: "user:reviewer-b");

        var rec = UploadProcessor.ReadFilingSidecar(PdfPath(id));
        Assert.NotNull(rec);
        Assert.Equal("Business – Eingang", rec!.Spec.Target?.Library);
        Assert.Equal("site-b", rec.Spec.Target?.SiteId);
        Assert.Equal("Document", rec.Spec.Target?.ContentType);
        Assert.False(rec.Spec.Target!.RequiresPatient);
        Assert.Null(rec.PatientLookupId);
        Assert.False(rec.Spec.Fields.ContainsKey("PatientLookupId"));
        Assert.Equal("user:reviewer-b", rec.Reviewer);
    }

    // A MEDICAL spec (no target) round-trips to a NULL target → the sink's medical default on recovery (Patient Records).
    [Fact]
    public async Task T12_MedicalFilingSidecar_HasNoTarget_RecoversToMedicalDefault()
    {
        const string id = "t12";
        var spec = new FilingSpec($"2026-05-01_Verordnung_Z000000005_{id}.pdf", "Verordnung",
            new Dictionary<string, object?> { ["PatientLookupId"] = 7 }, "volltext", PdfPath(id));
        await UploadProcessor.WriteFilingSidecarAsync(PdfPath(id), spec, patientLookupId: 7, CancellationToken.None);
        var rec = UploadProcessor.ReadFilingSidecar(PdfPath(id));
        Assert.NotNull(rec);
        Assert.Null(rec!.Spec.Target);          // no target recorded → the sink resolves the medical default
        Assert.Equal(7, rec.PatientLookupId);
    }

    // ---- T13: the Zielbereich label survives a simulated reconcile on BOTH rails — the ledger (Reviewed event, the
    // system of record) AND the best-effort SharePoint column (spec.Fields). Mirrors how POST /api/review writes both:
    // the label in the reviewDiff (→ sidecar review.diff) and in BuildBusinessSpec's Fields (→ sidecar fields). ----------
    [Fact]
    public async Task T13_BusinessFiling_ZielbereichSurvivesReconcile_OnBothLedgerAndColumn()
    {
        using var store = NewStore();
        const string id = "t13";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        await store.AppendAsync(new AuditEntry(id, "NeedsReview", "test"));
        WriteCapturedPdf(id);

        var target = new FilingTarget("site-b", "Business – Eingang", "Document", RequiresPatient: false);
        var spec = SharePointSink.BuildBusinessSpec(
            new IncomingDocument(id, PdfPath(id), SourceChannel.Api, "invoice.pdf"),
            new DocumentUnit(id, 1, 1, new Transcript("md", 1, 1.0, Array.Empty<DecodedBarcode>())),
            target, "Document", "2026-05-01", "Finance");                    // Zielbereich rides spec.Fields (column rail)
        // The reviewDiff the endpoint records on the Reviewed event (ledger rail): domainChange + the label.
        var reviewDiff = new Dictionary<string, object?>
        {
            ["domain"] = "Business",
            ["domainChange"] = new { from = "Kassenkorrespondenz", to = "Business" },
            ["zielbereich"] = "Finance",
        };
        await UploadProcessor.WriteFilingSidecarAsync(PdfPath(id), spec, patientLookupId: null, CancellationToken.None,
            reviewer: "user:reviewer-b", reviewDiff: reviewDiff);

        var filer = new RecordingFiler();
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, filer: filer);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Equal("Filed", await StateOf(store, id));

        // Column rail: the recovered spec still carries the Zielbereich field → FileAsync writes it (best-effort).
        Assert.Single(filer.FileCalls);
        Assert.Equal("Finance", filer.FileCalls[0].Fields["Zielbereich"] as string);

        // Ledger rail (system of record): the recovered Reviewed event carries zielbereich=Finance — queryable back.
        var reviewed = (await store.GetEventsAsync(id)).Single(e => e.Type == "Reviewed");
        using var d = System.Text.Json.JsonDocument.Parse(reviewed.Details);
        Assert.Equal("Finance", d.RootElement.GetProperty("zielbereich").GetString());
        Assert.True(d.RootElement.GetProperty("recovered").GetBoolean());     // recovery replay marker (dedup signal)

        // C10 rides recovery too: the reconcile-written Filed carries the SINK's verdict — the fake reports
        // pdfaDeclared=false, so a hardcoded `true` in the reconcile writer fails here (mutation finding).
        var filed = (await store.GetEventsAsync(id)).Single(e => e.Type == "Filed");
        using var fd = System.Text.Json.JsonDocument.Parse(filed.Details);
        Assert.False(fd.RootElement.GetProperty("pdfaDeclared").GetBoolean());
        Assert.True(fd.RootElement.GetProperty("diRendition").GetBoolean());
    }

    // ---- T14: business RE-SCAN duplicate — a no-KVNR doc whose content fingerprint matches an already-FILED business
    // document is routed to review with duplicateOf (Layer-3). This is the business analog of the medical patient+type+date
    // key: same text / different bytes, caught via BusinessDedup.Fingerprint recorded on the prior doc's Filed event. -----
    [Fact]
    public async Task T14_BusinessReScan_MatchingContentFingerprint_IsFlaggedToReviewWithDuplicateOf()
    {
        using var store = NewStore();
        const string text = "Personalabteilung Arbeitsvertrag Max Mustermann gueltig ab 01.06.2026, Gehalt 4200 EUR monatlich.";
        var key = BusinessDedup.Fingerprint(BarcodeSummary.LeadWith(text, Array.Empty<DecodedBarcode>())); // exactly as the worker computes it
        Assert.NotNull(key);

        // A previously FILED business document carrying the fingerprint on its Filed event (as POST /api/review records it).
        const string filedId = "t14filed";
        await store.AppendAsync(new AuditEntry(filedId, "Received", "test"));
        await store.AppendAsync(new AuditEntry(filedId, "Filed", "sharepoint", Details: Detail(new
        {
            fileName = "2026-05-01_Triagedokument_t14filed.pdf", domain = "Business", library = "Eingang",
            businessDedupKey = key, driveItemId = "drv-t14", disposition = "ReviewedThenFiled",
        })));

        // A NEW no-KVNR document whose transcript fingerprints to the same key → the worker's Layer-3 must flag it.
        const string id = "t14new";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store,
            transcriber: new TextTranscriber(text), extractor: new NoKvnrExtractor(), directory: new NoMatchDirectory());

        await proc.ProcessAsync(new UploadJob(id, PdfPath(id), "t14new.pdf"), CancellationToken.None);

        Assert.Equal("NeedsReview", await StateOf(store, id));
        var nr = (await store.GetEventsAsync(id)).Single(e => e.Type == "NeedsReview");
        using var d = System.Text.Json.JsonDocument.Parse(nr.Details);
        Assert.Equal(filedId, d.RootElement.GetProperty("duplicateOf").GetString());   // flagged as a duplicate of the filed business doc
        Assert.Contains("duplicate_suspect", d.RootElement.GetProperty("reasonCodes").EnumerateArray().Select(x => x.GetString()));
    }

    // ---- T15: a split child REUSES the parent's carried transcript — the (throwing) transcriber is SKIPPED, the
    // transcribe step is audited as a reuse (source=parent, carrying the parent id + sliced page count), the carried
    // sidecar is consumed, and the outbox still completes to Filed. This is the cost invariant: a manual split never
    // re-runs the (expensive) vision transcription on the children.
    [Fact]
    public async Task T15_SplitChild_ReusesCarriedTranscript_SkipsVision_StillFiles()
    {
        using var store = NewStore();
        const string id = "t15-u1";
        await store.AppendAsync(new AuditEntry(id, "Received", "test", Detail(new { channel = "Split", splitFrom = "t15" })));
        WriteCapturedPdf(id);
        // The parent's sliced transcript, carried to this child exactly as /api/split writes it (2 pages).
        await UploadProcessor.WritePreTranscriptAsync(PdfPath(id),
            new UploadProcessor.CarriedTranscript(new[] { "Teil A markdown", "Teil B markdown" }, 0.9, "t15", "split-manual-v1"),
            CancellationToken.None);

        // The transcriber THROWS if invoked — the test only passes if reuse SKIPS it entirely.
        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store, transcriber: new FakeTranscriber { Throw = true });
        await proc.ProcessAsync(new UploadJob(id, PdfPath(id), "t15-u1.pdf"), CancellationToken.None);

        // Transcribe was audited as a REUSE (not a vision call), carrying the parent + the sliced page count.
        var t = (await store.GetEventsAsync(id)).Single(e => e.Type == "Transcribed");
        using var d = System.Text.Json.JsonDocument.Parse(t.Details);
        Assert.Equal("parent", d.RootElement.GetProperty("source").GetString());
        Assert.Equal("t15", d.RootElement.GetProperty("parent").GetString());
        Assert.Equal(2, d.RootElement.GetProperty("pages").GetInt32());
        Assert.Equal("split-manual-v1", d.RootElement.GetProperty("separatorVersion").GetString());
        // The carried sidecar was consumed, and the outbox still completed (never dead-lettered).
        Assert.False(File.Exists(UploadProcessor.PreTranscriptSidecarPath(PdfPath(id))));
        Assert.Equal("Filed", await StateOf(store, id));
    }

    // ---- T16: with NO carried transcript, the pipeline transcribes normally (the fallback is intact — reuse is
    // strictly additive). Same fake data, but the transcriber IS called and the transcribe step is a vision source.
    [Fact]
    public async Task T16_NoCarriedTranscript_TranscribesNormally()
    {
        using var store = NewStore();
        const string id = "t16";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id); // NO .pretranscript.json written

        var proc = Build(Channel.CreateUnbounded<UploadJob>(), store, store); // default FakeTranscriber (does NOT throw)
        await proc.ProcessAsync(new UploadJob(id, PdfPath(id), "t16.pdf"), CancellationToken.None);

        var t = (await store.GetEventsAsync(id)).Single(e => e.Type == "Transcribed");
        using var d = System.Text.Json.JsonDocument.Parse(t.Details);
        Assert.False(d.RootElement.TryGetProperty("source", out _)); // vision-source Transcribed carries no reuse marker
        Assert.Equal("Filed", await StateOf(store, id));
    }

    // ---- T17: redrive bound — the counter reads the ledger exactly as reconcile writes it -----------------------
    [Fact]
    public void T17_RedrivesSinceLastIngest_ProgressDoesNotReset_FreshIngestDoes()
    {
        static AuditEvent E(string type, string actor) => new(0, "d", null, type, DateTimeOffset.UtcNow, actor, null, "{}", "h");

        // Progress events between re-drives do NOT reset the count — a document that transcribes and then kills the
        // process every time is still poison; only a real ingest (different actor) may hand out a fresh budget.
        var events = new List<AuditEvent>
        {
            E("Received", "test"), E("Transcribed", "worker"),
            E("Received", "reconcile"), E("Transcribed", "worker"),
            E("Received", "reconcile"), E("Received", "reconcile"),
        };
        Assert.Equal(3, UploadProcessor.RedrivesSinceLastIngest(events));

        // A manual retry appends Received under the human actor → budget reset; later re-drives count from there.
        events.Add(E("Received", "user:reviewer"));
        Assert.Equal(0, UploadProcessor.RedrivesSinceLastIngest(events));
        events.Add(E("Received", "reconcile"));
        Assert.Equal(1, UploadProcessor.RedrivesSinceLastIngest(events));

        Assert.Equal(0, UploadProcessor.RedrivesSinceLastIngest(new List<AuditEvent> { E("Received", "test") }));
    }

    // ---- T18: at the ATTEMPT limit, reconcile PARKS as DeadLetter instead of re-driving — the crash-loop breaker.
    // The crash-loop ledger shape: each cycle is a pickup (durable attempt mark) followed by an unclean stop
    // (reconcile re-drive event). Only that shape parks — see T20 for the restarts-without-pickups counterexample.
    [Fact]
    public async Task T18_AttemptLimitReached_ParksAsDeadLetter_DoesNotReEnqueue()
    {
        using var store = NewStore();
        var transcripts = new TranscriptStore(Path.Combine(_dir, "t18-transcripts.db"));
        const string id = "t18";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);
        for (var i = 0; i < UploadProcessor.MaxFailedAttempts; i++)
        {
            await transcripts.MarkAttemptStartAsync(id, DateTimeOffset.UtcNow.AddMinutes(i + 1)); // safely after the ingest anchor
            await store.AppendAsync(new AuditEntry(id, "Received", "reconcile", Details: Detail(new { channel = "Reconcile" })));
        }

        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store, transcripts: transcripts);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Equal("DeadLetter", await StateOf(store, id));   // parked: loop broken, visible, human-retryable
        Assert.Equal(0, queue.Reader.Count);                    // and NOT re-enqueued
        var dl = (await store.GetEventsAsync(id)).Last(e => e.Type == "DeadLetter");
        Assert.Equal("reconcile", dl.Actor);
        using var d = System.Text.Json.JsonDocument.Parse(dl.Details);
        Assert.Contains(ReasonCodes.RedriveLimit,
            d.RootElement.GetProperty("reasonCodes").EnumerateArray().Select(x => x.GetString()));
    }

    // ---- T19: below the attempt limit, the re-drive proceeds — and is itself counted on the ledger --------------
    [Fact]
    public async Task T19_BelowAttemptLimit_ReEnqueues()
    {
        using var store = NewStore();
        var transcripts = new TranscriptStore(Path.Combine(_dir, "t19-transcripts.db"));
        const string id = "t19";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);
        for (var i = 0; i < UploadProcessor.MaxFailedAttempts - 1; i++)
        {
            await transcripts.MarkAttemptStartAsync(id, DateTimeOffset.UtcNow.AddMinutes(i + 1));
            await store.AppendAsync(new AuditEntry(id, "Received", "reconcile", Details: Detail(new { channel = "Reconcile" })));
        }

        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store, transcripts: transcripts);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.False(await HasEvent(store, id, "DeadLetter"));
        Assert.True(queue.Reader.TryRead(out var job));         // re-driven exactly once
        Assert.Equal(JobOrigin.ReconcileRedrive, job!.Origin);
        Assert.Equal(0, queue.Reader.Count);
        Assert.Equal(UploadProcessor.MaxFailedAttempts, UploadProcessor.RedrivesSinceLastIngest(await store.GetEventsAsync(id)));
    }

    // ---- T20: REGRESSION (verification finding) — re-drives WITHOUT pickups must never park. A document merely
    // queued through N routine restarts (the in-memory queue re-drives the whole unprocessed backlog every time)
    // accumulates reconcile-Received events but no attempt marks; parking it as a crasher was the original bug.
    [Fact]
    public async Task T20_RedrivesWithoutAttempts_NeverPark()
    {
        using var store = NewStore();
        var transcripts = new TranscriptStore(Path.Combine(_dir, "t20-transcripts.db")); // present, but NO marks
        const string id = "t20";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);
        for (var i = 0; i < UploadProcessor.MaxFailedAttempts * 3; i++)
            await store.AppendAsync(new AuditEntry(id, "Received", "reconcile", Details: Detail(new { channel = "Reconcile" })));

        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store, transcripts: transcripts);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.False(await HasEvent(store, id, "DeadLetter"));  // never parked, no matter how many restarts
        Assert.Equal(1, queue.Reader.Count);                    // still re-driven
    }

    // ---- T21: no capture store at all (tests/degraded) ⇒ the bound is fail-open — never parks ------------------
    [Fact]
    public async Task T21_NoCaptureStore_NeverParks()
    {
        using var store = NewStore();
        const string id = "t21";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);
        for (var i = 0; i < UploadProcessor.MaxFailedAttempts * 2; i++)
            await store.AppendAsync(new AuditEntry(id, "Received", "reconcile", Details: Detail(new { channel = "Reconcile" })));

        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, store, store); // transcripts: null
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.False(await HasEvent(store, id, "DeadLetter"));
        Assert.Equal(1, queue.Reader.Count);
    }

    // ---- T22: a failing history read is fail-open — the re-drive proceeds ---------------------------------------
    [Fact]
    public async Task T22_HistoryReadFailure_FailsOpen_ReEnqueues()
    {
        using var store = NewStore();
        var transcripts = new TranscriptStore(Path.Combine(_dir, "t22-transcripts.db"));
        const string id = "t22";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);
        for (var i = 0; i < UploadProcessor.MaxFailedAttempts; i++)
        {
            await transcripts.MarkAttemptStartAsync(id, DateTimeOffset.UtcNow.AddMinutes(i + 1));
            await store.AppendAsync(new AuditEntry(id, "Received", "reconcile", Details: Detail(new { channel = "Reconcile" })));
        }

        var audit = new RedriveFaultAudit(store) { ThrowOnGetEvents = true }; // would park, but the count is unreadable
        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, audit, store, transcripts: transcripts);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.False(await HasEvent(store, id, "DeadLetter"));
        Assert.Equal(1, queue.Reader.Count);                    // availability of the re-drive beats the bound
    }

    // ---- T23: a failing park append skips the enqueue and leaves the doc for the next eligible sweep ------------
    [Fact]
    public async Task T23_ParkAppendFailure_NoEnqueue_StateUnchanged()
    {
        using var store = NewStore();
        var transcripts = new TranscriptStore(Path.Combine(_dir, "t23-transcripts.db"));
        const string id = "t23";
        await store.AppendAsync(new AuditEntry(id, "Received", "test"));
        WriteCapturedPdf(id);
        for (var i = 0; i < UploadProcessor.MaxFailedAttempts; i++)
        {
            await transcripts.MarkAttemptStartAsync(id, DateTimeOffset.UtcNow.AddMinutes(i + 1));
            await store.AppendAsync(new AuditEntry(id, "Received", "reconcile", Details: Detail(new { channel = "Reconcile" })));
        }

        var audit = new RedriveFaultAudit(store) { ThrowOnDeadLetterAppend = true };
        var queue = Channel.CreateUnbounded<UploadJob>();
        var proc = Build(queue, audit, store, transcripts: transcripts);
        await proc.ReconcileAsync(CancellationToken.None);

        Assert.Equal(0, queue.Reader.Count);                    // at the limit ⇒ never re-enqueued, parked or not
        Assert.Equal("Received", await StateOf(store, id));     // park append failed ⇒ non-terminal, retried later
        Assert.False(await HasEvent(store, id, "DeadLetter"));
    }

    // Delegating audit decorator for the redrive-bound failure paths.
    private sealed class RedriveFaultAudit(IAuditStore inner) : IAuditLog
    {
        public bool ThrowOnGetEvents { get; set; }
        public bool ThrowOnDeadLetterAppend { get; set; }
        public Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct = default)
            => ThrowOnDeadLetterAppend && entry.Type == "DeadLetter"
                ? throw new InvalidOperationException("park append blocked (test)")
                : inner.AppendAsync(entry, ct);
        public Task<DocumentAudit?> GetDocumentAsync(string documentId, CancellationToken ct = default) => inner.GetDocumentAsync(documentId, ct);
        public Task<IReadOnlyList<DocumentAudit>> ListDocumentsAsync(int limit = 5000, CancellationToken ct = default) => inner.ListDocumentsAsync(limit, ct);
        public Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string documentId, CancellationToken ct = default)
            => ThrowOnGetEvents
                ? throw new InvalidOperationException("history read blocked (test)")
                : inner.GetEventsAsync(documentId, ct);
        public Task<ChainVerification> VerifyChainAsync(CancellationToken ct = default) => inner.VerifyChainAsync(ct);
    }

    // ---- harness ------------------------------------------------------------------------------------------------
    private SqliteAuditStore NewStore() => new(_db);                       // temp-file SQLite — the established test pattern
    private string PdfPath(string id) => Path.Combine(_dir, $"{id}.pdf");
    private void WriteCapturedPdf(string id) => File.WriteAllBytes(PdfPath(id), "%PDF-1.4 test"u8.ToArray());
    private static string Detail(object o) => System.Text.Json.JsonSerializer.Serialize(o); // audit Details JSON (mirrors Program.J)

    private static async Task<string> StateOf(IAuditStore store, string id) => (await store.GetDocumentAsync(id))!.State;
    private static async Task<bool> HasEvent(IAuditStore store, string id, string type) =>
        (await store.GetEventsAsync(id)).Any(e => e.Type == type);

    // Build an UploadProcessor with fakes for the scope-resolved services. The audit/store params are passed directly.
    private UploadProcessor Build(Channel<UploadJob> queue, IAuditLog audit, IAuditStore store,
        ITranscriber? transcriber = null, IExtractor? extractor = null, IPatientDirectory? directory = null, IFiler? filer = null,
        TranscriptStore? transcripts = null, FilingClaims? claims = null, SweepHealth? sweepHealth = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(transcriber ?? new FakeTranscriber());
        services.AddSingleton(extractor ?? new FakeExtractor());
        services.AddSingleton(directory ?? new FakePatientDirectory());
        services.AddSingleton(filer ?? new RecordingFiler());
        var sp = services.BuildServiceProvider();
        return new UploadProcessor(queue, sp.GetRequiredService<IServiceScopeFactory>(), audit, store,
            claims ?? new FilingClaims(), NullLogger<UploadProcessor>.Instance, transcripts, null, sweepHealth);
    }
}

// ---- fakes ----------------------------------------------------------------------------------------------------

// Returns a clean transcript, or throws (a pre-filing failure).
// The body must be a realistic length: these tests assert the AUTO-FILE path, and the old "md" placeholder is two
// characters, which the empty-transcript gate now correctly routes to review (see Core.TranscriptQuality). Keeping
// the placeholder would silently turn every outbox test into a review-path test.
file sealed class FakeTranscriber : ITranscriber
{
    public const string CleanBody =
        "Verordnung häuslicher Krankenpflege (Muster 12) — Verordnungszeitraum von 01.05.2026 bis 31.05.2026. Folgeverordnung.";
    public bool Throw;
    public Task<Transcript> TranscribeAsync(IncomingDocument doc, CancellationToken ct = default) =>
        Throw ? throw new InvalidOperationException("fake transcribe failure (pre-filing)")
              : Task.FromResult(new Transcript(CleanBody, 1, 0.95, Array.Empty<DecodedBarcode>()));
}

// Returns AutoFile-qualifying data (a complete allow-listed Verordnung — see ReviewGateTests), or throws (pre-filing).
file sealed class FakeExtractor : IExtractor
{
    public bool Throw;
    public Task<ExtractedData> ExtractAsync(DocumentUnit unit, CancellationToken ct = default) =>
        Throw ? throw new InvalidOperationException("fake extract failure (pre-filing)")
              : Task.FromResult(new ExtractedData(new[]
                {
                    new ExtractedField("ContentType", "Verordnung", 0.97, "vision"),
                    new ExtractedField("KVNR", "Z000000005", 0.97, "barcode"),
                    new ExtractedField("DocumentDate", "2026-05-01", 0.95, "vision"),
                    new ExtractedField("PrescriptionOrder", "Folgeverordnung", 0.95, "barcode"),
                }));
}

// A single confident KVNR match (synthetic patient; no PHI).
file sealed class FakePatientDirectory : IPatientDirectory
{
    public Task<PatientMatch> MatchAsync(ExtractedData data, CancellationToken ct = default) =>
        Task.FromResult(new PatientMatch(MatchOutcome.Single,
            new PatientRef(1, "Z000000005", "Test", "T", new DateOnly(1956, 5, 29), "Active"), "kvnr"));
}

// ---- fakes for the business content-fingerprint (Layer-3) dedup: a no-KVNR document routed to review -------------
file sealed class TextTranscriber(string text) : ITranscriber
{
    public Task<Transcript> TranscribeAsync(IncomingDocument doc, CancellationToken ct = default) =>
        Task.FromResult(new Transcript(text, 1, 0.95, Array.Empty<DecodedBarcode>()));
}
// Extracts a content type + date but NO KVNR → BuildDuplicateKeyPrefix returns null → the business fingerprint layer applies.
file sealed class NoKvnrExtractor : IExtractor
{
    public Task<ExtractedData> ExtractAsync(DocumentUnit unit, CancellationToken ct = default) =>
        Task.FromResult(new ExtractedData(new[]
        {
            new ExtractedField("ContentType", "Kassenkorrespondenz", 0.9, "vision"),
            new ExtractedField("DocumentDate", "2026-05-01", 0.9, "vision"),
        }));
}
// No patient match → the gate routes to review (the no-KVNR / business-destined shape).
file sealed class NoMatchDirectory : IPatientDirectory
{
    public Task<PatientMatch> MatchAsync(ExtractedData data, CancellationToken ct = default) =>
        Task.FromResult(new PatientMatch(MatchOutcome.None, null, "none"));
}

// The injectable filing seam: throw or succeed on FileAsync, record every call. No Graph/HTTP — that's the point of IFiler.
file sealed class RecordingFiler : IFiler
{
    public bool ThrowOnFile;
    public bool ThrowOnDelete;
    public bool ExistsResult = true;
    public List<FilingSpec> FileCalls { get; } = new();
    public List<string> DeleteCalls { get; } = new();   // driveItemIds passed to TryDeleteByIdAsync (supersede's delete half)
    public List<FilingTarget?> DeleteTargets { get; } = new(); // the drive each delete was addressed through (C-2); parallel to DeleteCalls

    public Task<FilingResult> FileAsync(FilingSpec spec, CancellationToken ct = default)
    {
        if (ThrowOnFile) throw new InvalidOperationException("fake FileAsync failure (post-Filing-intent)");
        FileCalls.Add(spec);
        // pdfaDeclared deliberately FALSE (not the happy default): a writer that hardcodes `true` instead of
        // plumbing the sink's verdict is then distinguishable from correct code (mutation finding).
        return Task.FromResult(new FilingResult("drv-fake-item-1", PdfADeclared: false, DiRendition: true));
    }
    public Task<bool> ExistsByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default) => Task.FromResult(ExistsResult);

    // Supersede seam: record the id AND the drive it was addressed through (a real 404 is folded into success inside
    // GraphDeleteAsync, so returning true models both a real delete and an already-gone re-drive). ThrowOnDelete
    // simulates a transient Graph failure for retry tests.
    public Task<bool> TryDeleteByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
    {
        if (ThrowOnDelete) throw new InvalidOperationException("fake TryDeleteByIdAsync failure (transient)");
        DeleteCalls.Add(driveItemId);
        DeleteTargets.Add(target);
        return Task.FromResult(true);
    }
}

// IAuditLog decorator whose GetDocumentAsync reports the document already in a TERMINAL state while the underlying
// store still holds the truth — the deterministic reproduction of D-1's window: the candidate list (store) sees the
// snapshot, the re-read under the claim (audit) sees the disposition that landed in between.
file sealed class TerminalOnReReadAudit(IAuditLog inner, string terminalState) : IAuditLog
{
    public Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct = default) => inner.AppendAsync(entry, ct);
    public async Task<DocumentAudit?> GetDocumentAsync(string id, CancellationToken ct = default) =>
        await inner.GetDocumentAsync(id, ct) is { } d ? d with { State = terminalState } : null;
    public Task<IReadOnlyList<DocumentAudit>> ListDocumentsAsync(int limit = 100, CancellationToken ct = default) => inner.ListDocumentsAsync(limit, ct);
    public Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string id, CancellationToken ct = default) => inner.GetEventsAsync(id, ct);
    public Task<ChainVerification> VerifyChainAsync(CancellationToken ct = default) => inner.VerifyChainAsync(ct);
}

// IAuditLog decorator that throws on the "Filed" append (only while FailFiledAppend is set); everything else delegates.
file sealed class FiledAppendFailingAudit(IAuditLog inner) : IAuditLog
{
    public bool FailFiledAppend;
    public Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct = default) =>
        FailFiledAppend && entry.Type == "Filed"
            ? throw new InvalidOperationException("fake Filed-append failure (after a successful upload)")
            : inner.AppendAsync(entry, ct);
    public Task<DocumentAudit?> GetDocumentAsync(string id, CancellationToken ct = default) => inner.GetDocumentAsync(id, ct);
    public Task<IReadOnlyList<DocumentAudit>> ListDocumentsAsync(int limit = 100, CancellationToken ct = default) => inner.ListDocumentsAsync(limit, ct);
    public Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string id, CancellationToken ct = default) => inner.GetEventsAsync(id, ct);
    public Task<ChainVerification> VerifyChainAsync(CancellationToken ct = default) => inner.VerifyChainAsync(ct);
}
