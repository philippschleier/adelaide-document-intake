using Adelaide.Audit;
using Adelaide.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Adelaide.Audit.Tests;

public class SqliteAuditStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-audit-{Guid.NewGuid():N}.db");
    private SqliteAuditStore New() => new(_db);
    public void Dispose() { try { File.Delete(_db); } catch { /* best effort */ } }

    [Fact]
    public async Task Append_updates_projection_state_and_count()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc1", "Received", "test"));
        await s.AppendAsync(new AuditEntry("doc1", "Transcribed", "test"));

        var d = await s.GetDocumentAsync("doc1");
        Assert.NotNull(d);
        Assert.Equal("Transcribed", d!.State);   // projection tracks the latest event
        Assert.Equal(2, d.EventCount);
        Assert.True(d.LastUpdated >= d.FirstSeen);
    }

    [Fact]
    public async Task Filed_event_details_populate_projection()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc2", "Received", "test"));
        await s.AppendAsync(new AuditEntry("doc2", "Filed", "test",
            Details: "{\"fileName\":\"2026-05-28_Verordnung_Z000000005_3f9a1c2e.pdf\",\"contentType\":\"Verordnung\",\"patientListItemId\":102,\"disposition\":\"AutoFile\"}"));

        var d = await s.GetDocumentAsync("doc2");
        Assert.Equal("Verordnung", d!.ContentType);
        Assert.Equal(102, d.PatientListItemId);
        Assert.Equal("AutoFile", d.Disposition);
        Assert.StartsWith("2026-05-28_Verordnung_", d.FileName);
    }

    [Fact]
    public async Task Earlier_detail_is_retained_when_later_event_omits_it()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc3", "Filed", "test", Details: "{\"contentType\":\"Arztbrief\"}"));
        await s.AppendAsync(new AuditEntry("doc3", "Corrected", "test")); // no details
        var d = await s.GetDocumentAsync("doc3");
        Assert.Equal("Arztbrief", d!.ContentType); // COALESCE keeps the prior value
        Assert.Equal("Corrected", d.State);
    }

    [Fact]
    public async Task One_row_per_document()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("docA", "Received", "test"));
        await s.AppendAsync(new AuditEntry("docA", "Filed", "test"));
        await s.AppendAsync(new AuditEntry("docB", "Received", "test"));
        Assert.Equal(2, (await s.GetEventsAsync("docA")).Count);
        Assert.Single(await s.GetEventsAsync("docB"));
    }

    [Fact]
    public async Task Chain_verifies_after_appends()
    {
        using var s = New();
        for (int i = 0; i < 5; i++) await s.AppendAsync(new AuditEntry("doc", $"Event{i}", "test"));
        Assert.True((await s.VerifyChainAsync()).Intact);
    }

    [Fact]
    public async Task Tampering_with_an_event_breaks_the_chain()
    {
        using (var s = New())
        {
            await s.AppendAsync(new AuditEntry("doc", "Received", "test"));
            await s.AppendAsync(new AuditEntry("doc", "Filed", "test"));
            Assert.True((await s.VerifyChainAsync()).Intact);
        }

        // simulate tampering: alter a stored event's details directly in the DB
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE events SET Details='{\"tampered\":true}' WHERE Type='Filed'";
            cmd.ExecuteNonQuery();
        }

        using var s2 = New();
        var v = await s2.VerifyChainAsync();
        Assert.False(v.Intact);              // hash no longer matches → detected
        Assert.Equal(2L, v.FirstBrokenSeq);  // and it pinpoints the tampered event (the 2nd append) — the bool→{seq} upgrade
    }

    [Fact]
    public async Task Ledger_set_state_appends_event()
    {
        using var s = New();
        await ((ILedger)s).SetStateAsync("doc", ProcessingState.Filed);
        var d = await s.GetDocumentAsync("doc");
        Assert.Equal("Filed", d!.State);
    }
}
