using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// The dedup/idempotency lookups exclude documents whose captured file is gone: Discarded and Superseded
// historically, and now Purged (post-window discard, artifacts released — the seal populated 58 such rows
// on day one). These pin the exclusion on the SQLite substrate; the WHERE lists are hand-edited twins in
// both stores, and the Azure half is covered by AzureSqlAuditStoreContractTests.
public class DedupExclusionTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-dedup-{Guid.NewGuid():N}.db");
    private SqliteAuditStore New() => new(_db);
    public void Dispose() { try { File.Delete(_db); } catch { /* best effort */ } }

    [Fact]
    public async Task Purged_documents_are_invisible_to_the_sha256_lookup()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("dx1", "Received", "test", Details: """{"sha256":"aaa111"}"""));
        await s.AppendAsync(new AuditEntry("dx1", "Discarded", "test"));
        await s.AppendAsync(new AuditEntry("dx1", "Purged", "discard-sweep"));

        Assert.Null(await s.FindBySha256Async("aaa111", "someone-else"));   // sealed row must not match

        // A live doc with the same sha IS still found — the exclusion is per-state, not per-sha.
        await s.AppendAsync(new AuditEntry("dx2", "Received", "test", Details: """{"sha256":"aaa111"}"""));
        var hit = await s.FindBySha256Async("aaa111", "someone-else");
        Assert.Equal("dx2", hit!.DocumentId);
    }

    [Fact]
    public async Task Purged_documents_are_invisible_to_the_source_record_lookup()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("dx3", "Received", "drain", Details: """{"sourceRecordId":"srid-77"}"""));
        await s.AppendAsync(new AuditEntry("dx3", "Discarded", "test"));
        await s.AppendAsync(new AuditEntry("dx3", "Purged", "discard-sweep"));

        Assert.Null(await s.FindBySourceRecordIdAsync("srid-77", ""));      // drain re-delivery ingests fresh

        // Discarded (recoverable, not sealed) stays excluded exactly as before — the historical behavior.
        await s.AppendAsync(new AuditEntry("dx4", "Received", "drain", Details: """{"sourceRecordId":"srid-88"}"""));
        await s.AppendAsync(new AuditEntry("dx4", "Discarded", "test"));
        Assert.Null(await s.FindBySourceRecordIdAsync("srid-88", ""));
    }
}
