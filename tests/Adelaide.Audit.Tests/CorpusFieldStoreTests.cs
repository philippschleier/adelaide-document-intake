using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// corpus_fields (non-PHI measurement) + field_adjudications (PHI value pairs): upsert semantics that never
// un-adjudicate, case-insensitive outcome matching (the review diff camelCases column names), FILL insertion,
// the AUTOFILED blanket, and erasure/purge coverage. Synthetic data only.
public sealed class CorpusFieldStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("adelaide-cf-tests").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private TranscriptStore Store() => new(Path.Combine(_dir, "capture.db"));

    private static CorpusFieldRow F(string name, bool proposed = true, double conf = 0.9, string src = "vision")
        => new(name, proposed, conf, src);

    [Fact]
    public async Task Reprocessing_refreshes_the_proposal_but_keeps_the_adjudication()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("d1", [F("DocumentDate", conf: 0.8)], now);
        await s.SetFieldOutcomesAsync("d1", [("documentDate", "CORRECTION")], null, now);

        await s.PutCorpusFieldsAsync("d1", [F("DocumentDate", conf: 0.95)], now.AddMinutes(1)); // a re-run

        var row = Assert.Single(await s.GetCorpusFieldsAsync("d1"));
        Assert.Equal(0.95, row.Confidence);            // proposal facts refreshed
        Assert.Equal("CORRECTION", row.Outcome);       // adjudication preserved (the Label-preserving pattern)
    }

    [Fact]
    public async Task Outcomes_match_camelCased_names_case_insensitively()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("d1", [F("KVNR"), F("DocumentDate")], now);

        await s.SetFieldOutcomesAsync("d1", [("kvnr", "MATCH"), ("documentDate", "CORRECTION")], null, now);

        var rows = await s.GetCorpusFieldsAsync("d1");
        Assert.Equal("MATCH", rows.Single(r => r.FieldName == "KVNR").Outcome);
        Assert.Equal("CORRECTION", rows.Single(r => r.FieldName == "DocumentDate").Outcome);
    }

    [Fact]
    public async Task A_human_filled_field_with_no_proposal_is_inserted_as_a_row()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("d1", [F("DocumentDate")], now);

        await s.SetFieldOutcomesAsync("d1", [("legalBasis", "FILL")], null, now);

        var fill = (await s.GetCorpusFieldsAsync("d1")).Single(r => r.FieldName == "legalBasis");
        Assert.False(fill.Proposed);
        Assert.Null(fill.Confidence);
        Assert.Equal("FILL", fill.Outcome);
    }

    [Fact]
    public async Task The_autofiled_blanket_stamps_every_pending_row_and_only_those()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("d1", [F("DocumentDate"), F("KVNR")], now);
        await s.SetFieldOutcomesAsync("d1", [("KVNR", "MATCH")], null, now); // pre-adjudicated (e.g. reclassify then review)

        await s.SetFieldOutcomesAsync("d1", [], "AUTOFILED", now);

        var rows = await s.GetCorpusFieldsAsync("d1");
        Assert.Equal("AUTOFILED", rows.Single(r => r.FieldName == "DocumentDate").Outcome);
        Assert.Equal("MATCH", rows.Single(r => r.FieldName == "KVNR").Outcome); // never overwritten
        Assert.DoesNotContain(rows, r => r.Outcome is null);                    // no NULL-pending masquerade
    }

    [Fact]
    public async Task Outcome_counts_report_autofiled_separately_from_pending()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("d1", [F("A"), F("B")], now);
        await s.SetFieldOutcomesAsync("d1", [], "AUTOFILED", now);
        await s.PutCorpusFieldsAsync("d2", [F("A")], now); // still pending

        var counts = await s.CorpusFieldOutcomeCountsAsync();
        Assert.Equal(2, counts["AUTOFILED"]);
        Assert.Equal(1, counts["pending"]);
    }

    [Fact]
    public async Task Adjudications_roundtrip_and_a_null_provenance_never_clears_a_recorded_one()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutAdjudicationsAsync("d1",
            [new FieldAdjudicationRow("DocumentDate", "2026-05-01", "2026-05-02", "correction", "typed")], now);
        // A reconcile replay without the client map must not erase the observed provenance.
        await s.PutAdjudicationsAsync("d1",
            [new FieldAdjudicationRow("DocumentDate", "2026-05-01", "2026-05-02", "correction", null)], now.AddMinutes(1));

        var row = Assert.Single(await s.GetAdjudicationsAsync("d1"));
        Assert.Equal("2026-05-01", row.ProposedValue);
        Assert.Equal("2026-05-02", row.FinalValue);
        Assert.Equal("correction", row.Signal);
        Assert.Equal("typed", row.Provenance);
    }

    [Fact]
    public async Task Erasure_removes_adjudications_and_keeps_the_measurement_rows()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("d1", [F("DocumentDate")], now);
        await s.PutAdjudicationsAsync("d1", [new FieldAdjudicationRow("DocumentDate", "v1", "v2", "correction", null)], now);

        await s.DeleteAsync("d1"); // Art. 17 content erasure

        Assert.Empty(await s.GetAdjudicationsAsync("d1"));       // value pairs are PHI → gone
        Assert.Single(await s.GetCorpusFieldsAsync("d1"));       // measurement (names/confidences/outcomes) survives

        await s.ForgetAsync("d1");                               // full removal
        Assert.Empty(await s.GetCorpusFieldsAsync("d1"));
    }

    // The window purges the PHI side (adjudicated VALUES) but never the measurement side (corpus_fields): the
    // (Confidence, Outcome) pairs are the calibration history, and a retention window must not erase it
    // (migration plan C2). Only ForgetAsync removes a document's measurement rows.
    [Fact]
    public async Task The_window_purge_ages_out_adjudications_but_spares_corpus_fields()
    {
        var s = Store();
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        await s.PutCorpusFieldsAsync("old-doc", [F("A")], old);
        await s.PutAdjudicationsAsync("old-doc", [new FieldAdjudicationRow("A", "v", "v", "match", null)], old);
        await s.PutCorpusFieldsAsync("new-doc", [F("A")], DateTimeOffset.UtcNow);

        await s.PurgeOlderThanAsync(new TranscriptRetentionPolicy(RetentionDays: 30).CutoffFrom(DateTimeOffset.UtcNow));

        Assert.Single(await s.GetCorpusFieldsAsync("old-doc"));   // measurement survives the window
        Assert.Empty(await s.GetAdjudicationsAsync("old-doc"));   // adjudicated values (PHI) age out
        Assert.Single(await s.GetCorpusFieldsAsync("new-doc"));
    }
}
