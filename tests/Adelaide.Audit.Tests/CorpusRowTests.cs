using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// The corpus row: one row per document, non-PHI, holding everything a later model comparison needs so it never has to
// join Extracted/Filed/Reviewed or go digging in the filesystem again.
public sealed class CorpusRowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "adelaide-corpus-" + Guid.NewGuid().ToString("n")[..8]);
    private TranscriptStore Store(string name = "c.db") => new(Path.Combine(_dir, name));
    public CorpusRowTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private static CorpusRow Row(string id, DateTimeOffset? at = null) => new(
        DocumentId: id, At: at ?? DateTimeOffset.UtcNow, TranscriptSha256: TranscriptDigest.Of("t-" + id),
        Model: "gpt-4o", ModelVersion: "gpt-4o-2024-11-20", PromptHash: "acffc7526284",
        Pages: 3, Legibility: 0.95, Barcodes: 1, TranscriptChars: 1234,
        ModelDomain: "Medical", ModelContentType: "Verordnung", ModelConfidence: 0.91,
        ContentSha256: "abc123", ReviewState: "Review", ReasonCodesJson: """["low_confidence"]""");

    // THE query the whole design exists to make possible.
    [Fact]
    public async Task Scoreable_is_exactly_the_rows_that_have_a_human_label()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusAsync(Row("d1", now));
        await s.PutCorpusAsync(Row("d2", now));
        await s.PutCorpusAsync(Row("d3", now));
        await s.SetLabelAsync("d1", "Verordnung", DocumentDomain.Medical, "drive-1", now);
        await s.SetLabelAsync("d3", "Pflegevertrag", DocumentDomain.Medical, null, now);

        var scoreable = await s.ScoreableSinceAsync(now.AddMinutes(-5));
        Assert.Equal(2, scoreable.Count);
        Assert.Equal(["d1", "d3"], scoreable.Select(x => x.DocumentId).OrderBy(x => x).ToArray());
        Assert.Equal("drive-1", scoreable.Single(x => x.DocumentId == "d1").DriveItemId);
    }

    [Fact]
    public async Task The_window_bound_is_honoured()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutCorpusAsync(Row("old", now.AddDays(-40)));
        await s.SetLabelAsync("old", "Verordnung", DocumentDomain.Medical, null, now);
        Assert.Empty(await s.ScoreableSinceAsync(now.AddDays(-30)));
        Assert.Single(await s.ScoreableSinceAsync(now.AddDays(-50)));
    }

    // Labelling a document that was never retained must not invent a row — otherwise the corpus would contain rows
    // with a label and no transcript, which cannot be scored.
    [Fact]
    public async Task Labelling_an_unretained_document_is_a_no_op()
        => Assert.False(await Store().SetLabelAsync("never-retained", "Verordnung", DocumentDomain.Medical, null, DateTimeOffset.UtcNow));

    [Fact]
    public async Task Reprocessing_updates_the_row_rather_than_duplicating_it()
    {
        var s = Store();
        await s.PutCorpusAsync(Row("d1") with { ModelContentType = "Genehmigung" });
        await s.PutCorpusAsync(Row("d1") with { ModelContentType = "Verordnung" });
        await s.SetLabelAsync("d1", "Verordnung", DocumentDomain.Medical, null, DateTimeOffset.UtcNow);
        Assert.Single(await s.ScoreableSinceAsync(DateTimeOffset.UtcNow.AddHours(-1)));
    }

    // A re-run of the pipeline must not silently discard the human's answer.
    [Fact]
    public async Task Reprocessing_preserves_an_existing_label()
    {
        var s = Store();
        await s.PutCorpusAsync(Row("d1"));
        await s.SetLabelAsync("d1", "Verordnung", DocumentDomain.Medical, null, DateTimeOffset.UtcNow);
        await s.PutCorpusAsync(Row("d1") with { ModelContentType = "Bescheid" });   // re-extracted later
        var counts = await s.LabelCountsAsync();
        Assert.Equal(("Verordnung", 1), Assert.Single(counts));
    }

    // ---- Task 3: the model INPUTS that used to be discarded ----

    [Fact]
    public async Task Artifacts_round_trip()
    {
        var s = Store();
        await s.PutArtifactsAsync("d1", """["page one","page two"]""", """[{"symbology":"DataMatrix"}]""", "[X] Erstverordnung", DateTimeOffset.UtcNow);
        await s.PutAsync("d1", "text", TranscriptDigest.Of("text"), DateTimeOffset.UtcNow);
        Assert.Equal(1, await s.CountAsync());
    }

    // ---- erasure semantics: content and measurement are separable, and that is deliberate ----

    [Fact]
    public async Task Erasing_content_keeps_the_non_PHI_measurement_row()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutAsync("d1", "patient text", TranscriptDigest.Of("patient text"), now);
        await s.PutArtifactsAsync("d1", """["p1"]""", null, null, now);
        await s.PutCorpusAsync(Row("d1", now));
        await s.SetLabelAsync("d1", "Verordnung", DocumentDomain.Medical, null, now);

        Assert.True(await s.DeleteAsync("d1"));
        Assert.Null(await s.GetAsync("d1"));                                   // PHI gone
        Assert.Single(await s.ScoreableSinceAsync(now.AddMinutes(-5)));         // measurement survives
    }

    [Fact]
    public async Task Forget_removes_the_measurement_row_too()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutAsync("d1", "patient text", TranscriptDigest.Of("patient text"), now);
        await s.PutCorpusAsync(Row("d1", now));
        await s.SetLabelAsync("d1", "Verordnung", DocumentDomain.Medical, null, now);

        Assert.True(await s.ForgetAsync("d1"));
        Assert.Null(await s.GetAsync("d1"));
        Assert.Empty(await s.ScoreableSinceAsync(now.AddMinutes(-5)));
    }

    // The window ages out CONTENT; the measurement row SURVIVES it (migration plan C2): a retention window is a
    // decision about document text, and it must not silently cap the corpus — the longitudinal record the model
    // comparisons run on — at the window length. Removing measurement rows is ForgetAsync, per document, explicit.
    [Fact]
    public async Task Purge_removes_content_but_the_corpus_row_survives()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-40);
        await s.PutAsync("old", "x", TranscriptDigest.Of("x"), old);
        await s.PutArtifactsAsync("old", """["p"]""", null, null, old);
        await s.PutCorpusAsync(Row("old", old));
        await s.SetLabelAsync("old", "Verordnung", DocumentDomain.Medical, null, old);
        await s.PutAsync("new", "y", TranscriptDigest.Of("y"), now);
        await s.PutCorpusAsync(Row("new", now));

        Assert.Equal(1, await s.PurgeOlderThanAsync(new TranscriptRetentionPolicy(RetentionDays: 30).CutoffFrom(now)));
        Assert.Null(await s.GetAsync("old"));                          // the transcript text is gone
        var scoreable = await s.ScoreableSinceAsync(now.AddDays(-60)); // …but the labeled measurement row remains
        Assert.Equal("old", Assert.Single(scoreable).DocumentId);
        Assert.NotNull(await s.GetAsync("new"));
    }
}
