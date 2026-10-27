using System.Threading.Channels;
using Adelaide.Audit;
using Adelaide.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

/// <summary>C6 at the drain ingestion core: the Received payload is structural only (channel + content hash) —
/// the original filename and the path-shaped source-record id go to the projection via the side-channel; rejection
/// events carry errorClass + reason codes while the human-readable text lands in the erasable projection Reasons.</summary>
public class EventPayloadHygieneTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-hyg-{Guid.NewGuid():N}.db");
    private readonly string _inbox = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"adelaide-hyg-in-{Guid.NewGuid():N}")).FullName;
    public void Dispose()
    {
        try { File.Delete(_db); } catch { }
        try { Directory.Delete(_inbox, recursive: true); } catch { }
    }

    private static byte[] Pdf(string seed) => System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4\n% {seed}\n%%EOF");

    private async Task<DrainIngest.Result> IngestAsync(SqliteAuditStore store, Channel<UploadJob> queue, byte[] bytes,
        string name, string srid, IMalwareScanner? scanner = null) =>
        await DrainIngest.IngestAsync(bytes, name, srid, "drain-poller", recordRejections: true, _inbox,
            store, store, queue, new KeyGate(), scanner ?? new DisabledMalwareScanner(), maxFileBytes: 50 * 1024 * 1024,
            NullLogger<DrainPoller>.Instance, CancellationToken.None);

    [Fact]
    public async Task Ingested_received_payload_is_structural_only()
    {
        using var store = new SqliteAuditStore(_db);
        var queue = Channel.CreateUnbounded<UploadJob>();
        var result = await IngestAsync(store, queue, Pdf("a"), "Muster_Erika_A123456789.pdf", "scans/2026/Muster_Erika_A123456789.pdf");
        Assert.Equal(DrainIngest.ResultKind.Ingested, result.Kind);

        var e = Assert.Single(await store.GetEventsAsync(result.DocumentId!));
        Assert.Contains("\"channel\":\"Drain\"", e.Details);
        Assert.Contains("sha256", e.Details);
        Assert.DoesNotContain("Muster", e.Details);   // no filename in the immutable payload
        Assert.DoesNotContain("scans/", e.Details);   // no source path either

        var d = await store.GetDocumentAsync(result.DocumentId!);
        Assert.Equal("Muster_Erika_A123456789.pdf", d!.OriginalName);            // …but the projection has both
        Assert.Equal("scans/2026/Muster_Erika_A123456789.pdf", d.SourceRecordId);

        // The idempotency contract survives the payload change: a re-delivery of the same path+bytes dedups.
        var again = await IngestAsync(store, queue, Pdf("a"), "Muster_Erika_A123456789.pdf", "scans/2026/Muster_Erika_A123456789.pdf");
        Assert.Equal(DrainIngest.ResultKind.Deduplicated, again.Kind);
    }

    [Fact]
    public async Task Invalid_input_deadletter_carries_class_and_code_not_prose()
    {
        using var store = new SqliteAuditStore(_db);
        var queue = Channel.CreateUnbounded<UploadJob>();
        var result = await IngestAsync(store, queue, System.Text.Encoding.ASCII.GetBytes("this is not a pdf"), "garbage.bin", "scans/garbage.bin");
        Assert.Equal(DrainIngest.ResultKind.RejectedRecorded, result.Kind);

        var events = await store.GetEventsAsync(result.DocumentId!);
        var dead = Assert.Single(events, e => e.Type == "DeadLetter");
        Assert.Contains($"\"errorClass\":\"{ReasonCodes.InvalidPdf}\"", dead.Details);
        Assert.Contains(ReasonCodes.InvalidPdf, dead.Details);
        Assert.DoesNotContain("not a valid PDF", dead.Details); // the prose is projection-only

        var d = await store.GetDocumentAsync(result.DocumentId!);
        Assert.Equal("DeadLetter", d!.State);
        Assert.Equal("not a valid PDF", d.Reasons); // the dashboard text is intact — just erasable now
    }

    [Fact]
    public async Task Malware_deadletter_quarantines_the_reason_in_the_projection()
    {
        using var store = new SqliteAuditStore(_db);
        var queue = Channel.CreateUnbounded<UploadJob>();
        var result = await IngestAsync(store, queue, Pdf("mal"), "flagged.pdf", "scans/flagged.pdf", new BlockingScanner());
        Assert.Equal(DrainIngest.ResultKind.RejectedRecorded, result.Kind);
        Assert.False(result.SafeToDeleteSource); // the source copy stays the quarantine

        var events = await store.GetEventsAsync(result.DocumentId!);
        var dead = Assert.Single(events, e => e.Type == "DeadLetter");
        Assert.Contains($"\"errorClass\":\"{ReasonCodes.MalwareBlocked}\"", dead.Details);
        Assert.DoesNotContain("EICAR", dead.Details);

        var d = await store.GetDocumentAsync(result.DocumentId!);
        Assert.Equal("EICAR (test)", d!.Reasons);
        Assert.Equal("flagged.pdf", d.OriginalName);
    }

    private sealed class BlockingScanner : IMalwareScanner
    {
        public Task<MalwareDecision> ScanAsync(byte[] bytes, CancellationToken ct = default) =>
            Task.FromResult(new MalwareDecision(false, "EICAR (test)", ScanVerdict.Infected));
    }
}
