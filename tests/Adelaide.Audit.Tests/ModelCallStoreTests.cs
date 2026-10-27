using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// The model_calls table: append-only per attempt, attempt auto-assignment per (document, site), and — the part the
// erasure hard rule hangs on — full coverage by DeleteAsync / ForgetAsync / the window purge. Synthetic data only.
public sealed class ModelCallStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("adelaide-mc-tests").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private TranscriptStore Store() => new(Path.Combine(_dir, "capture.db"));

    private static ModelCallRecord Rec(string doc, ModelCallSite site, int attempt = 0, DateTimeOffset? at = null) => new(
        new CallContext(doc, site, attempt, PromptHash: "ph-1"),
        at ?? DateTimeOffset.UtcNow,
        Deployment: "gpt-4o", ServedModel: "gpt-4o-2024-11-20", SystemFingerprint: "fp",
        ReasoningEffort: null,
        RequestBody: "{\"messages\":[\"synthetic\"]}", ResponseBody: "{\"choices\":[]}",
        PromptTokens: 10, CompletionTokens: 5, ReasoningTokens: 0, LatencyMs: 42, RequestId: "rid-1", HttpStatus: 200);

    [Fact]
    public async Task A_row_roundtrips_completely()
    {
        var s = Store();
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.Extraction));

        var row = Assert.Single(await s.GetModelCallsAsync("d1"));
        Assert.Equal("extraction", row.CallSite);
        Assert.Equal(1, row.Attempt);
        Assert.Equal("gpt-4o", row.Deployment);
        Assert.Equal("gpt-4o-2024-11-20", row.ServedModel);
        Assert.Equal("ph-1", row.PromptHash);
        Assert.Equal(10, row.PromptTokens);
        Assert.Equal(42, row.LatencyMs);
        Assert.Equal("rid-1", row.RequestId);
        Assert.Equal(200, row.HttpStatus);
        Assert.Contains("synthetic", row.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attempts_auto_increment_per_document_and_site()
    {
        var s = Store();
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.Classify));
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.Classify));       // a re-processed document
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.Extraction));     // a different site starts at 1
        await s.PutModelCallAsync(Rec("d2", ModelCallSite.Classify));       // a different document starts at 1

        var d1 = await s.GetModelCallsAsync("d1");
        Assert.Equal(new[] { 1, 2 }, d1.Where(r => r.CallSite == "classify").Select(r => r.Attempt).ToArray());
        Assert.Equal(1, d1.Single(r => r.CallSite == "extraction").Attempt);
        Assert.Equal(1, (await s.GetModelCallsAsync("d2")).Single().Attempt);
    }

    [Fact]
    public async Task An_explicit_attempt_is_honoured()
    {
        var s = Store();
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.Domain, attempt: 7));
        Assert.Equal(7, (await s.GetModelCallsAsync("d1")).Single().Attempt);
    }

    [Fact]
    public async Task The_call_sites_store_their_wire_names()
    {
        var s = Store();
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.ClassifyEscalation));
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.DiReadPdf));
        var sites = (await s.GetModelCallsAsync("d1")).Select(r => r.CallSite).ToArray();
        Assert.Contains("classify-escalation", sites); // the previously-invisible escalation is its own site
        Assert.Contains("di-read-pdf", sites);
    }

    // ---- erasure completeness (the hard rule): content erasure removes call records; measurement survives ----

    [Fact]
    public async Task DeleteAsync_erases_model_calls_with_the_content_and_keeps_the_corpus_row()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutAsync("d1", "synthetic transcript", "sha-t", now);
        await s.PutArtifactsAsync("d1", "[\"p1\"]", null, null, now);
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.Transcription));
        await s.PutCorpusAsync(new CorpusRow("d1", now, TranscriptSha256: "sha-t"));

        Assert.True(await s.DeleteAsync("d1"));

        Assert.Null(await s.GetAsync("d1"));
        Assert.Empty(await s.GetModelCallsAsync("d1"));
        // The corpus row survives: labelling it later still works (measurement outlives content).
        await s.SetLabelAsync("d1", "Verordnung", DocumentDomain.Medical, null, now);
        Assert.Single(await s.ScoreableSinceAsync(now.AddMinutes(-5)));
    }

    [Fact]
    public async Task ForgetAsync_erases_everything_including_the_call_records()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutAsync("d1", "synthetic transcript", "sha-t", now);
        await s.PutModelCallAsync(Rec("d1", ModelCallSite.Extraction));
        await s.PutCorpusAsync(new CorpusRow("d1", now));

        Assert.True(await s.ForgetAsync("d1"));

        Assert.Empty(await s.GetModelCallsAsync("d1"));
        await s.SetLabelAsync("d1", "Verordnung", DocumentDomain.Medical, null, now); // no row → no-op
        Assert.Empty(await s.ScoreableSinceAsync(now.AddMinutes(-5)));
    }

    [Fact]
    public async Task The_window_purge_covers_model_calls()
    {
        var s = Store();
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        await s.PutModelCallAsync(Rec("old-doc", ModelCallSite.Extraction, at: old));
        await s.PutModelCallAsync(Rec("new-doc", ModelCallSite.Extraction));

        await s.PurgeOlderThanAsync(new TranscriptRetentionPolicy(RetentionDays: 30).CutoffFrom(DateTimeOffset.UtcNow));

        Assert.Empty(await s.GetModelCallsAsync("old-doc"));
        Assert.Single(await s.GetModelCallsAsync("new-doc"));
    }
}
