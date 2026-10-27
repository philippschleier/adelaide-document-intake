using System.Text.Json;
using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// The separation the N3 rework is for: the ledger keeps the EVIDENCE, the deletable store keeps the TEXT.
// (The event's byte-shape is pinned in Adelaide.Web.Tests, against the builder the host actually uses.)
public sealed class TranscriptChainSeparationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "adelaide-sep-" + Guid.NewGuid().ToString("n")[..8]);
    public TranscriptChainSeparationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    // The hash-bearing event shape, as emitted by the host.
    private static string Details(string markdown) => JsonSerializer.Serialize(new
    {
        pages = 1, legibility = 0.95, barcodes = 0,
        model = "gpt-4o", modelVersion = "gpt-4o-2024-11-20", systemFingerprint = "fp", promptHash = "acffc7526284",
        transcriptSha256 = TranscriptDigest.Of(markdown),
    });

    [Fact]
    public async Task The_chain_validates_with_the_hash_bearing_event()
    {
        var store = new SqliteAuditStore(Path.Combine(_dir, "audit.db"));
        await store.AppendAsync(new AuditEntry("doc-1", "Received", "test", Details: "{}"));
        await store.AppendAsync(new AuditEntry("doc-1", "Transcribed", "vision", Details: Details("transcript body")));
        var v = await store.VerifyChainAsync();
        Assert.True(v.Intact, $"chain broken at seq {v.FirstBrokenSeq}");
    }

    // THE point of the rework: erasing the text is an ordinary delete that cannot touch the audit trail, and the
    // evidence of what was produced survives the erasure.
    [Fact]
    public async Task Erasing_the_text_leaves_the_chain_valid_and_the_evidence_intact()
    {
        const string body = "text that must be erasable";
        var ledger = new SqliteAuditStore(Path.Combine(_dir, "audit2.db"));
        await ledger.AppendAsync(new AuditEntry("doc-2", "Transcribed", "vision", Details: Details(body)));

        var transcripts = new TranscriptStore(Path.Combine(_dir, "transcripts.db"));
        await transcripts.PutAsync("doc-2", body, TranscriptDigest.Of(body), DateTimeOffset.UtcNow);

        Assert.True(await transcripts.DeleteAsync("doc-2"));
        Assert.Null(await transcripts.GetAsync("doc-2"));

        var v = await ledger.VerifyChainAsync();
        Assert.True(v.Intact, "erasing a transcript must not affect the ledger");

        var ev = await ledger.GetEventsAsync("doc-2");
        using var doc = JsonDocument.Parse(ev.Single(e => e.Type == "Transcribed").Details);
        Assert.Equal(TranscriptDigest.Of(body), doc.RootElement.GetProperty("transcriptSha256").GetString());
    }

    // A retained transcript can be proven to be the one the ledger recorded — re-hashing the stored text reproduces
    // the event's hash. Without this the stored copy would be unverifiable and worthless as a baseline.
    [Fact]
    public async Task A_retained_transcript_verifies_against_the_ledgers_hash()
    {
        const string body = "Verordnung häuslicher Krankenpflege — Zeitraum 01.05.2026 bis 31.05.2026.";
        var ledger = new SqliteAuditStore(Path.Combine(_dir, "audit3.db"));
        await ledger.AppendAsync(new AuditEntry("doc-3", "Transcribed", "vision", Details: Details(body)));

        var transcripts = new TranscriptStore(Path.Combine(_dir, "transcripts3.db"));
        await transcripts.PutAsync("doc-3", body, TranscriptDigest.Of(body), DateTimeOffset.UtcNow);

        var ev = await ledger.GetEventsAsync("doc-3");
        using var doc = JsonDocument.Parse(ev.Single(e => e.Type == "Transcribed").Details);
        var fromLedger = doc.RootElement.GetProperty("transcriptSha256").GetString();

        var retained = await transcripts.GetAsync("doc-3");
        Assert.Equal(fromLedger, TranscriptDigest.Of(retained!.Value.Transcript));
    }

    // A tampered stored transcript no longer matches the ledger — the hash is doing real work.
    [Fact]
    public async Task A_tampered_stored_transcript_fails_verification()
    {
        const string body = "original text";
        var transcripts = new TranscriptStore(Path.Combine(_dir, "transcripts4.db"));
        await transcripts.PutAsync("doc-4", "tampered text", TranscriptDigest.Of(body), DateTimeOffset.UtcNow);

        var retained = await transcripts.GetAsync("doc-4");
        Assert.NotEqual(retained!.Value.Sha256, TranscriptDigest.Of(retained.Value.Transcript));
    }
}
