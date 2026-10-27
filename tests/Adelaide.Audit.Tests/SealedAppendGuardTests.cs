using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// The SEALED append guard (discard-recovery): a document whose projected State is sealed (Purged) rejects
// EVERY append — advancing types, the annotation type, the ILedger SetStateAsync path — structurally, inside
// the store's append transaction. "No events, no carve-outs, now or later" is a store rule, not an endpoint
// convention; these tests pin it on the SQLite substrate (the Azure twin lives in
// AzureSqlAuditStoreContractTests and runs in CI against real SQL Server).
public class SealedAppendGuardTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-sealed-{Guid.NewGuid():N}.db");
    private SqliteAuditStore New() => new(_db);
    public void Dispose() { try { File.Delete(_db); } catch { /* best effort */ } }

    private static async Task<SqliteAuditStore> SealedDocAsync(SqliteAuditStore s, string id)
    {
        await s.AppendAsync(new AuditEntry(id, "Received", "test"));
        await s.AppendAsync(new AuditEntry(id, "Discarded", "test"));
        await s.AppendAsync(new AuditEntry(id, "Purged", "discard-sweep", Details: "{\"graceDays\":30}"));
        return s;
    }

    [Fact]
    public async Task Sealed_document_rejects_every_event_type_and_nothing_lands()
    {
        using var s = await SealedDocAsync(New(), "sg1");
        var before = (await s.GetDocumentAsync("sg1"))!;
        var chainBefore = await s.VerifyChainAsync();

        // Every advancing type in the vocabulary, a type nobody listed, a second Purged, and the one
        // annotation type: all rejected. The annotation case matters most — it leaves State alone by design,
        // so only the store guard (not any state transition logic) can stop it.
        foreach (var type in new[] { "Received", "Transcribed", "Extracted", "Matched", "NeedsReview", "Reviewed",
                                     "Filing", "Filed", "Segmented", "Superseded", "Discarded", "DeadLetter",
                                     "Rotated", "DuplicateSuspect", "Purged", "SomeFutureTypeNobodyListed",
                                     AuditEventTypes.SharePointItemRemoved })
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => s.AppendAsync(new AuditEntry("sg1", type, "test")));

        var after = (await s.GetDocumentAsync("sg1"))!;
        Assert.Equal("Purged", after.State);
        Assert.Equal(before.EventCount, after.EventCount);           // nothing landed
        var chainAfter = await s.VerifyChainAsync();
        Assert.True(chainAfter.Intact);
        Assert.Equal(chainBefore.EventCount, chainAfter.EventCount); // the chain did not grow
    }

    [Fact]
    public async Task Sealed_document_rejects_the_SetStateAsync_path_too()
    {
        using var s = await SealedDocAsync(New(), "sg2");
        // ILedger funnels into AppendAsync — pin that the funnel really is the choke point.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((ILedger)s).SetStateAsync("sg2", ProcessingState.Received));
    }

    [Fact]
    public async Task Sealing_itself_and_all_non_sealed_appends_stay_legal()
    {
        using var s = New();
        // The undiscard path: appending over a merely-settled (Discarded) doc must stay legal.
        await s.AppendAsync(new AuditEntry("sg3", "Received", "test"));
        await s.AppendAsync(new AuditEntry("sg3", "Discarded", "test"));
        await s.AppendAsync(new AuditEntry("sg3", "NeedsReview", "test", Details: "{\"channel\":\"Undiscard\"}"));
        Assert.Equal("NeedsReview", (await s.GetDocumentAsync("sg3"))!.State);

        // The sweep path: the Purged append itself is the one legal way to seal.
        await s.AppendAsync(new AuditEntry("sg3", "Discarded", "test"));
        await s.AppendAsync(new AuditEntry("sg3", "Purged", "discard-sweep"));
        Assert.Equal("Purged", (await s.GetDocumentAsync("sg3"))!.State);

        // A first event for an unseen id has no row to be sealed — allowed.
        var e = await s.AppendAsync(new AuditEntry("sg4", "Received", "test"));
        Assert.Equal("Received", (await s.GetDocumentAsync("sg4"))!.State);
        Assert.True(e.Seq > 0);
    }
}
