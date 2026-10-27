using Adelaide.Audit;
using Adelaide.Core;
using Adelaide.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

// "Labels land or scream": the checked label write counts a missing corpus row instead of silently no-opping
// (the anti-pattern that lost 5 straight labels), and a reconcile-recovered review filing lands its label + field
// outcomes — the recovery block previously wrote no corpus data at all. Synthetic data only.
public sealed class LabelIntegrityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("adelaide-li-tests").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private TranscriptStore Store() => new(Path.Combine(_dir, "capture.db"));

    [Fact]
    public async Task A_missing_corpus_row_is_counted_not_silent()
    {
        var s = Store();
        var health = new CaptureHealth();

        await ReviewCapture.SetLabelCheckedAsync(s, health, NullLogger.Instance, "doc-nolabel",
            "Verordnung", DocumentDomain.Medical, "drive-1", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, health.LabelsMissedSinceStart); // the exact condition that used to vanish
        Assert.Equal(0, health.FailuresTotal);          // a miss is not a failure — different counter, different meaning
    }

    [Fact]
    public async Task A_landed_label_counts_nothing()
    {
        var s = Store();
        var health = new CaptureHealth();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusAsync(new CorpusRow("doc-ok", now));

        await ReviewCapture.SetLabelCheckedAsync(s, health, NullLogger.Instance, "doc-ok",
            "Verordnung", DocumentDomain.Medical, "drive-1", now, CancellationToken.None);

        Assert.Equal(0, health.LabelsMissedSinceStart);
        Assert.Equal(1, await s.LabeledRowCountAsync());
    }

    [Fact]
    public async Task A_store_exception_hits_the_failure_counter_not_the_caller()
    {
        // A directory where the DB file was → SQLITE_CANTOPEN on every open, on every OS (FileShare.None is only
        // advisory on Unix, so an exclusive-lock breaker passes on Windows and fails on Linux CI).
        var dbPath = Path.Combine(_dir, "locked.db");
        var s = new TranscriptStore(dbPath);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        Directory.CreateDirectory(dbPath);
        var health = new CaptureHealth();

        await ReviewCapture.SetLabelCheckedAsync(s, health, NullLogger.Instance, "doc-x",
            "Verordnung", DocumentDomain.Medical, null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(health.FailuresTotal >= 1);
        Assert.Equal(0, health.LabelsMissedSinceStart);
    }

    // ---- reconcile-recovered review filings ------------------------------------------------------------------

    private static UploadProcessor.FilingRecovery Recovery(string? reviewer = "reviewer-oid", string? diffJson = null) => new(
        new Adelaide.SharePoint.FilingSpec("2026-05-02_Verordnung_x.pdf", "Verordnung",
            new Dictionary<string, object?> { ["DocumentDate"] = "2026-05-02T00:00:00Z", ["PatientLookupId"] = 7L },
            "volltext", "unused.pdf"),
        PatientLookupId: 7, Reviewer: reviewer,
        ReviewDiffJson: diffJson ?? """{ "correctedFields": ["documentDate"], "fieldSignals": [{ "field": "documentDate", "signal": "correction" }], "domain": "Medical" }""",
        AcknowledgedDuplicate: false);

    [Fact]
    public async Task A_recovered_review_filing_lands_its_label_and_outcomes_without_the_sidecar()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusAsync(new CorpusRow("doc-rec", now));
        await s.PutCorpusFieldsAsync("doc-rec",
            [new CorpusFieldRow("DocumentDate", true, 0.83, "vision"),
             new CorpusFieldRow("SenderType", true, 0.9, "vision")], now);
        var pdfPath = Path.Combine(_dir, "doc-rec.pdf"); // no .review.json beside it → the stored-diff fallback

        await ReviewCapture.RecordRecoveredReviewAsync(s, new CaptureHealth(), NullLogger.Instance,
            "doc-rec", "drive-9", Recovery(), pdfPath, now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(1, await s.LabeledRowCountAsync()); // the label landed — previously recovery wrote nothing
        var fields = await s.GetCorpusFieldsAsync("doc-rec");
        Assert.Equal("CORRECTION", fields.Single(f => f.FieldName == "DocumentDate").Outcome); // from the stored diff
        Assert.Equal("MATCH", fields.Single(f => f.FieldName == "SenderType").Outcome);        // un-changed proposal accepted
        var adj = (await s.GetAdjudicationsAsync("doc-rec")).Single(a => a.FieldName == "DocumentDate");
        Assert.Null(adj.ProposedValue);                 // sidecar gone → the proposal is not guessed
        Assert.Equal("2026-05-02", adj.FinalValue);     // Graph date-time collapsed to the submitted date shape
    }

    [Fact]
    public async Task A_recovered_review_filing_replays_the_full_pairs_when_the_sidecar_survives()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusAsync(new CorpusRow("doc-rec2", now));
        var pdfPath = Path.Combine(_dir, "doc-rec2.pdf");
        await File.WriteAllTextAsync(Path.ChangeExtension(pdfPath, ".review.json"), """
            {
              "documentId": "doc-rec2",
              "contentType": "Verordnung",
              "fields": [ { "name": "DocumentDate", "value": "2026-05-01", "confidence": 0.83, "source": "vision" } ],
              "match": { "candidate": { "listItemId": 7 } }
            }
            """);

        await ReviewCapture.RecordRecoveredReviewAsync(s, new CaptureHealth(), NullLogger.Instance,
            "doc-rec2", "drive-9", Recovery(), pdfPath, now.AddMinutes(1), CancellationToken.None);

        var adj = (await s.GetAdjudicationsAsync("doc-rec2")).Single(a => a.FieldName == "DocumentDate");
        Assert.Equal("2026-05-01", adj.ProposedValue);  // full value pair — the sidecar replay is the inline path
        Assert.Equal("2026-05-02", adj.FinalValue);
        Assert.Equal("correction", adj.Signal);
    }

    [Fact]
    public async Task An_autofile_recovery_is_not_a_review_and_writes_no_label()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusAsync(new CorpusRow("doc-auto", now));

        await ReviewCapture.RecordRecoveredReviewAsync(s, new CaptureHealth(), NullLogger.Instance,
            "doc-auto", "drive-9", Recovery(reviewer: null), Path.Combine(_dir, "doc-auto.pdf"), now, CancellationToken.None);

        Assert.Equal(0, await s.LabeledRowCountAsync()); // worker-origin recovery: AUTOFILED stamp is the caller's job
    }
}
