using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

/// <summary>
/// Phase-1 UnitId activation: the "Rotated" event is the first to populate the previously-dormant AuditEntry.UnitId
/// (the canon prev|seq|doc|UNIT|type|... already reserves it). Activation must stay ADDITIVE — the hash chain still
/// verifies across a mix of historical null-unit events and new unit-bearing ones, and the unit round-trips (Acceptance #3).
/// </summary>
public class RotatedEventUnitIdTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-audit-{Guid.NewGuid():N}.db");
    private SqliteAuditStore New() => new(_db);
    public void Dispose() { try { File.Delete(_db); } catch { /* best effort */ } }

    [Fact]
    public async Task Chain_verifies_across_dormant_then_activated_unit()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc", "Received", "worker"));    // historical: UnitId dormant (null)
        await s.AppendAsync(new AuditEntry("doc", "NeedsReview", "worker")); // historical: null
        await s.AppendAsync(new AuditEntry("doc", "Rotated", "reviewer-oid", UnitId: "doc",
            Details: "{\"pages\":{\"1\":180},\"preHash\":\"aa\",\"postHash\":\"bb\"}"));         // ACTIVATING: real unit
        await s.AppendAsync(new AuditEntry("doc", "Filing", "reviewer-oid")); // back to null

        var v = await s.VerifyChainAsync();
        Assert.True(v.Intact);   // a mix of null and real unit hashes into ONE intact chain — additive, no break
        Assert.Equal(4, v.EventCount);
    }

    [Fact]
    public async Task Rotated_event_round_trips_its_unit_and_details()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc", "Rotated", "reviewer-oid", UnitId: "unit-1",
            Details: "{\"pages\":{\"1\":90}}"));
        var rotated = Assert.Single(await s.GetEventsAsync("doc"));
        Assert.Equal("Rotated", rotated.Type);
        Assert.Equal("unit-1", rotated.UnitId);   // the previously-dormant field now carries a value end-to-end
        Assert.Contains("\"1\":90", rotated.Details);
    }
}
