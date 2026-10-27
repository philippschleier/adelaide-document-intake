using System.Text.Json.Nodes;
using Adelaide.Audit;
using Adelaide.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

// ReviewCapture computes the per-field value pairs + signals from the SAME change detection the Reviewed event
// uses, then lands them in corpus_fields / field_adjudications. The last test is the acceptance-1 review-lane
// check: answer the DocumentDate question from the stores alone. All fixtures synthetic.
public sealed class ReviewCaptureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("adelaide-rc-tests").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private TranscriptStore Store() => new(Path.Combine(_dir, "capture.db"));

    /// <summary>A minimal synthetic review sidecar: classification + fields[] + a matched candidate.</summary>
    private static JsonNode Sidecar(params (string Name, string? Value)[] fields)
    {
        var arr = new JsonArray();
        foreach (var (name, value) in fields)
            arr.Add(new JsonObject { ["name"] = name, ["value"] = value, ["confidence"] = 0.9, ["source"] = "vision" });
        return new JsonObject
        {
            ["contentType"] = "Verordnung",
            ["fields"] = arr,
            ["match"] = new JsonObject { ["candidate"] = new JsonObject { ["listItemId"] = 7, ["kvnr"] = "Q000000000" } },
        };
    }

    private static ReviewSubmission Submission(Dictionary<string, string?> fields, string contentType = "Verordnung", int patient = 7)
        => new(DocumentId: "doc-rc", ContentType: contentType, PatientListItemId: patient, Fields: fields);

    private static ReviewCapture.FieldRecord Rec(IReadOnlyList<ReviewCapture.FieldRecord> records, string name)
        => records.Single(r => string.Equals(r.FieldName, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void An_unchanged_pair_is_recorded_as_match()
    {
        var records = ReviewCapture.ComputeFieldRecords(
            Sidecar(("DocumentDate", "2026-05-01")),
            Submission(new() { ["DocumentDate"] = "2026-05-01" }));
        var r = Rec(records, "DocumentDate");
        Assert.Equal("2026-05-01", r.ProposedValue);
        Assert.Equal("2026-05-01", r.FinalValue);
        Assert.Equal("match", r.Signal); // unchanged pairs included — the MATCH class calibration needs
    }

    [Fact]
    public void A_reformatted_date_is_not_a_correction()
    {
        // Shared change detection with the Reviewed event: the same date re-typed dotted-German is NOT a change.
        var records = ReviewCapture.ComputeFieldRecords(
            Sidecar(("DocumentDate", "2026-05-01")),
            Submission(new() { ["DocumentDate"] = "01.05.2026" }));
        Assert.Equal("match", Rec(records, "DocumentDate").Signal);
    }

    [Fact]
    public void A_changed_proposal_is_a_correction_with_both_values()
    {
        var records = ReviewCapture.ComputeFieldRecords(
            Sidecar(("DocumentDate", "2026-05-01")),
            Submission(new() { ["DocumentDate"] = "2026-05-02" }));
        var r = Rec(records, "DocumentDate");
        Assert.Equal(("2026-05-01", "2026-05-02", "correction"), (r.ProposedValue, r.FinalValue, r.Signal));
    }

    [Fact]
    public void A_field_the_model_never_proposed_is_a_fill()
    {
        var records = ReviewCapture.ComputeFieldRecords(
            Sidecar(("DocumentDate", "2026-05-01")),
            Submission(new() { ["DocumentDate"] = "2026-05-01", ["LegalBasis"] = "§ 37.3" }));
        var r = Rec(records, "LegalBasis");
        Assert.Null(r.ProposedValue);
        Assert.Equal("§ 37.3", r.FinalValue);
        Assert.Equal("fill", r.Signal);
    }

    [Fact]
    public void A_changed_identity_field_with_a_linked_patient_is_match_not_correction()
    {
        // The patient-record overwrite (applyPatient) — the structural inference ReviewFieldSignals encodes.
        var records = ReviewCapture.ComputeFieldRecords(
            Sidecar(("KVNR", "Q000000001")),
            Submission(new() { ["KVNR"] = "Q000000000" }, patient: 7));
        Assert.Equal("match", Rec(records, "KVNR").Signal);
    }

    [Fact]
    public void A_corrected_content_type_is_its_own_correction_record()
    {
        var records = ReviewCapture.ComputeFieldRecords(
            Sidecar(("DocumentDate", "2026-05-01")),
            Submission(new() { ["DocumentDate"] = "2026-05-01" }, contentType: "Genehmigung"));
        var r = Rec(records, "ContentType");
        Assert.Equal(("Verordnung", "Genehmigung", "correction"), (r.ProposedValue, r.FinalValue, r.Signal));
    }

    [Fact]
    public async Task Acceptance_the_DocumentDate_question_is_answerable_from_the_stores_alone()
    {
        // Extraction time: the proposal's measurement row lands (as UploadProcessor does)…
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("doc-rc",
            [new CorpusFieldRow("DocumentDate", Proposed: true, Confidence: 0.83, Source: "vision")], now);

        // …review time: the human corrects the date; ReviewCapture lands outcomes + the value pair.
        await ReviewCapture.RecordReviewOutcomesAsync(s, health: null, NullLogger.Instance, "doc-rc",
            Sidecar(("DocumentDate", "2026-05-01")),
            Submission(new() { ["DocumentDate"] = "2026-05-02" }),
            provenance: null, now.AddMinutes(1), CancellationToken.None);

        // The triggering question (INVESTIGATION.md §3.1), no sidecar involved:
        // what did the model propose for DocumentDate, at what confidence, from what source — and what happened?
        var field = (await s.GetCorpusFieldsAsync("doc-rc")).Single(f => f.FieldName == "DocumentDate");
        Assert.True(field.Proposed);
        Assert.Equal(0.83, field.Confidence);
        Assert.Equal("vision", field.Source);
        Assert.Equal("CORRECTION", field.Outcome);

        var pair = (await s.GetAdjudicationsAsync("doc-rc")).Single(a => a.FieldName == "DocumentDate");
        Assert.Equal("2026-05-01", pair.ProposedValue);
        Assert.Equal("2026-05-02", pair.FinalValue);
    }

    [Fact]
    public async Task A_broken_store_never_breaks_the_caller()
    {
        // Replace the store's DB file with a DIRECTORY of the same name → every open fails with SQLITE_CANTOPEN →
        // RecordReviewOutcomesAsync must swallow (warn + count), not throw. This is the fail-open hard rule at the
        // review write point. (A FileShare.None lock is NOT a portable breaker: on Unix it's advisory and SQLite's
        // fcntl locks ignore it, so the store keeps working on Linux CI.)
        var dbPath = Path.Combine(_dir, "locked.db");
        var s = new TranscriptStore(dbPath); // creates the schema
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // release pooled handles so the file can be replaced
        File.Delete(dbPath);
        Directory.CreateDirectory(dbPath);

        var health = new CaptureHealth();
        await ReviewCapture.RecordReviewOutcomesAsync(s, health, NullLogger.Instance, "doc-rc",
            Sidecar(("DocumentDate", "2026-05-01")),
            Submission(new() { ["DocumentDate"] = "2026-05-02" }),
            provenance: null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(health.FailuresTotal >= 1); // counted, not silent
    }
}
