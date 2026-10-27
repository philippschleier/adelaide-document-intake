using Adelaide.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Adelaide.Audit.Tests;

/// <summary>C6 (event-payload hygiene): display values reach the UPDATABLE projection through the
/// AuditEntry.Projection side-channel and NEVER through the append-only Details. These tests pin the store
/// contract: overrides land, overrides win over parsed details, "" clears, the hash chain is untouched by
/// side-channel values, the drain idempotency lookup works from the projection alone, and the events_readable
/// view re-joins the erasable names in one query.</summary>
public class ProjectionOverridesTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-audit-{Guid.NewGuid():N}.db");
    private SqliteAuditStore New() => new(_db);
    public void Dispose() { try { File.Delete(_db); } catch { /* best effort */ } }

    [Fact]
    public async Task Overrides_reach_projection_but_never_the_event()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc1", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"abc123"}""",
            Projection: new ProjectionOverrides(OriginalName: "Muster_Erika_A123456789.pdf", SourceRecordId: "scans/2026/Muster_Erika_A123456789.pdf")));

        var d = await s.GetDocumentAsync("doc1");
        Assert.Equal("Muster_Erika_A123456789.pdf", d!.OriginalName);
        Assert.Equal("scans/2026/Muster_Erika_A123456789.pdf", d.SourceRecordId);

        var e = Assert.Single(await s.GetEventsAsync("doc1"));
        Assert.DoesNotContain("Muster", e.Details);       // the name is nowhere in the immutable payload
        Assert.DoesNotContain("scans/", e.Details);       // nor the path-shaped source-record id
        Assert.Contains("\"sha256\":\"abc123\"", e.Details);
    }

    [Fact]
    public async Task Override_wins_over_a_details_value_and_the_chain_ignores_it()
    {
        using var s = New();
        // Pre-C6 shape in Details AND an override: the side-channel wins (it is the post-C6 source of truth).
        await s.AppendAsync(new AuditEntry("doc2", "Received", "test",
            Details: """{"original":"from-details.pdf"}""",
            Projection: new ProjectionOverrides(OriginalName: "from-override.pdf")));
        Assert.Equal("from-override.pdf", (await s.GetDocumentAsync("doc2"))!.OriginalName);

        // The chain hash covers Details only — an entry with an override verifies exactly like one without.
        var v = await s.VerifyChainAsync();
        Assert.True(v.Intact);
    }

    [Fact]
    public async Task Old_shape_details_still_populate_the_projection()
    {
        using var s = New();
        // Replaying pre-C6 history (no side-channel): KnownFields parsing is unchanged.
        await s.AppendAsync(new AuditEntry("doc3", "Received", "test",
            Details: """{"channel":"Upload","original":"legacy-name.pdf","sha256":"ff00"}"""));
        var d = await s.GetDocumentAsync("doc3");
        Assert.Equal("legacy-name.pdf", d!.OriginalName);
        Assert.Equal("ff00", d.Sha256);
    }

    [Fact]
    public async Task Empty_reasons_override_clears_the_prior_value()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc4", "DuplicateSuspect", "dedup",
            Details: """{"duplicateOf":"other1"}""",
            Projection: new ProjectionOverrides(Reasons: "inhaltsgleich (SHA-256) mit „other.pdf“")));
        Assert.Equal("inhaltsgleich (SHA-256) mit „other.pdf“", (await s.GetDocumentAsync("doc4"))!.Reasons);

        // A re-entry event (retry/override/reconcile) resets stale review prose with "" — same semantics the old
        // empty reasons array had, now via the side-channel.
        await s.AppendAsync(new AuditEntry("doc4", "Received", "test",
            Details: """{"channel":"DuplicateOverride"}""",
            Projection: new ProjectionOverrides(Reasons: "")));
        Assert.Equal("", (await s.GetDocumentAsync("doc4"))!.Reasons);
    }

    [Fact]
    public async Task Source_record_idempotency_works_from_the_projection_alone()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc5", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"beef"}""",
            Projection: new ProjectionOverrides(SourceRecordId: "share/sub/file-a.pdf")));

        var hit = await s.FindBySourceRecordIdAsync("share/sub/file-a.pdf", "");
        Assert.Equal("doc5", hit!.DocumentId);
        Assert.Equal("beef", hit.Sha256);
    }

    [Fact]
    public async Task Name_lookup_searches_the_whole_projection_and_prefers_the_newest_match()
    {
        using var s = New();
        // Two docs share a reused scanner filename (the drain's normal path-reuse case) — the LOOKUP must return
        // the newest, and it must search the projection (post-C6 events carry no names at all).
        await s.AppendAsync(new AuditEntry("older0000001", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"aa"}""",
            Projection: new ProjectionOverrides(OriginalName: "Scanner_g.pdf", SourceRecordId: "scans/Scanner_g.pdf")));
        await s.AppendAsync(new AuditEntry("newer0000001", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"bb"}""",
            Projection: new ProjectionOverrides(OriginalName: "Scanner_g.pdf", SourceRecordId: "scans/Scanner_g.pdf")));

        Assert.Equal("newer0000001", await s.FindDocumentIdByNameAsync("Scanner_g"));

        // LIKE wildcards in the needle are literals, not pattern syntax.
        await s.AppendAsync(new AuditEntry("wildcard0001", "Received", "test",
            Details: """{"channel":"Upload","sha256":"cc"}""",
            Projection: new ProjectionOverrides(OriginalName: "100%_rabatt.pdf")));
        Assert.Equal("wildcard0001", await s.FindDocumentIdByNameAsync("100%_rabatt"));
        Assert.Null(await s.FindDocumentIdByNameAsync("100X_rabatt")); // '_' must not match arbitrary chars
    }

    [Fact]
    public async Task Events_readable_view_joins_the_erasable_names_onto_the_chain()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("doc6", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"aa11"}""",
            Projection: new ProjectionOverrides(OriginalName: "readable-join.pdf")));

        using var c = new SqliteConnection($"Data Source={_db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT OriginalName, Details FROM events_readable WHERE DocumentId='doc6'";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("readable-join.pdf", r.GetString(0));   // joined from the projection…
        Assert.DoesNotContain("readable-join", r.GetString(1)); // …not present in the event payload
    }
}
