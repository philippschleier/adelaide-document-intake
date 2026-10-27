using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// The persisted identity oracle. Two properties carry the weight: idempotency (a re-run must not double-count, or
// the measurement drifts every time it is re-derived) and source separation (backfilled rows only cover documents
// whose sidecar still exists, so pooling them with live rows would silently mix two different coverages).
public sealed class IdentityOracleStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "adelaide-oracle-" + Guid.NewGuid().ToString("n")[..8]);
    private IdentityOracleStore Store(string name = "o.db") => new(Path.Combine(_dir, name));
    public IdentityOracleStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private static Task Put(IdentityOracleStore s, string doc, string field, bool agreed, string? shape = null, string? source = null)
        => s.PutAsync(doc, field, agreed, shape, source ?? IdentityOracleStore.SourceBackfill, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Records_and_tallies_a_sample()
    {
        var s = Store();
        await Put(s, "d1", "KVNR", agreed: true);
        var t = Assert.Single(await s.TallyAsync());
        Assert.Equal(("KVNR", 1, 1, IdentityOracleStore.SourceBackfill), t);
    }

    // THE idempotency property: the same document+field written twice is one row, so re-running a backfill cannot
    // inflate the denominator.
    [Fact]
    public async Task Re_running_a_backfill_does_not_double_count()
    {
        var s = Store();
        for (var i = 0; i < 3; i++)
        {
            await Put(s, "d1", "KVNR", agreed: false, shape: "len 9->10");
            await Put(s, "d1", "LastName", agreed: true);
        }
        Assert.Equal(2, await s.CountAsync());
        var kvnr = (await s.TallyAsync()).Single(x => x.Field == "KVNR");
        Assert.Equal(1, kvnr.Total);
        Assert.Equal(0, kvnr.Agreed);
    }

    // A re-derivation that reaches a DIFFERENT conclusion must replace, not accumulate — the store holds the current
    // best measurement per document, not a history of attempts.
    [Fact]
    public async Task A_re_derived_sample_replaces_the_previous_one()
    {
        var s = Store();
        await Put(s, "d1", "KVNR", agreed: false, shape: "len 9->10");
        await Put(s, "d1", "KVNR", agreed: true);
        var kvnr = (await s.TallyAsync()).Single(x => x.Field == "KVNR");
        Assert.Equal((1, 1), (kvnr.Agreed, kvnr.Total));
        Assert.Empty(await s.ShapesAsync());   // the old disagreement shape is gone with it
    }

    [Fact]
    public async Task Backfilled_and_live_rows_are_tallied_separately()
    {
        var s = Store();
        await Put(s, "d1", "KVNR", agreed: false, shape: "len 9->10", source: IdentityOracleStore.SourceBackfill);
        await Put(s, "d2", "KVNR", agreed: true, source: IdentityOracleStore.SourceLive);

        var all = await s.TallyAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(1, (await s.TallyAsync(IdentityOracleStore.SourceBackfill)).Single().Total);
        Assert.Equal(1, (await s.TallyAsync(IdentityOracleStore.SourceLive)).Single().Total);
    }

    [Fact]
    public async Task Only_disagreements_contribute_shapes()
    {
        var s = Store();
        await Put(s, "d1", "KVNR", agreed: false, shape: "len 9->10");
        await Put(s, "d2", "KVNR", agreed: false, shape: "len 9->10");
        await Put(s, "d3", "KVNR", agreed: true);
        var shapes = await s.ShapesAsync();
        Assert.Equal(("KVNR", "len 9->10", 2), Assert.Single(shapes));
    }

    // The store must be a different file from the ledger: it holds derived, recomputable data and the chain must
    // never be operated on to correct an analysis artefact.
    [Fact]
    public void The_default_path_is_a_sibling_of_the_audit_database_and_not_the_ledger()
    {
        var audit = Path.Combine("C:", "data", "adelaide-audit.db");
        var p = IdentityOracleStore.DefaultPathFor(audit);
        Assert.Equal("adelaide-identity-oracle.db", Path.GetFileName(p));
        Assert.NotEqual(Path.GetFullPath(audit), Path.GetFullPath(p));
    }

    // End-to-end with the real comparison: what the backfill actually does for one document.
    [Fact]
    public async Task A_backfilled_disagreement_carries_the_structural_shape_from_IdentityAgreement()
    {
        var s = Store();
        var r = IdentityAgreement.Compare("KVNR", "Z00000000S", "Z000000005")!;   // letter vs digit at index 9
        await s.PutAsync("d1", r.Field, r.Agreed, r.Shape, IdentityOracleStore.SourceBackfill, DateTimeOffset.UtcNow);
        var shape = Assert.Single(await s.ShapesAsync());
        Assert.Equal("a->d@9", shape.Shape);
    }
}
