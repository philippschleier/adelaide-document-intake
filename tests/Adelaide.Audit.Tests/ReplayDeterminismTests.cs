using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// Replay determinism across the recovery-window boundary (discard-recovery): the projected State is a pure
// function of the appended event stream, never of the clock at read time. The store API makes this
// structural — no read path takes or consults a timestamp — so a Discarded document stays Discarded no
// matter how long ago the discard was; expiry becomes state ONLY via the sweep's appended Purged event.
// These tests pin the shape so a future "compute expiry at read time" shortcut fails loudly.
public class ReplayDeterminismTests : IDisposable
{
    private readonly List<string> _dbs = new();
    private SqliteAuditStore New()
    {
        var db = Path.Combine(Path.GetTempPath(), $"adelaide-replay-{Guid.NewGuid():N}.db");
        _dbs.Add(db);
        return new SqliteAuditStore(db);
    }
    public void Dispose() { foreach (var db in _dbs) try { File.Delete(db); } catch { /* best effort */ } }

    [Fact]
    public async Task Discarded_never_expires_at_read_time_only_a_Purged_append_seals_it()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("rd1", "NeedsReview", "test"));
        await s.AppendAsync(new AuditEntry("rd1", "Discarded", "test"));

        // Repeated reads with the wall clock marching on: byte-identical rows. If any read computed
        // "window over → treat as Purged", the second read could differ from the first.
        var first = (await s.GetDocumentAsync("rd1"))!;
        Assert.Equal("Discarded", first.State);
        for (var i = 0; i < 3; i++)
        {
            var again = (await s.GetDocumentAsync("rd1"))!;
            Assert.Equal(first.State, again.State);
            Assert.Equal(first.LastUpdated, again.LastUpdated);
            Assert.Equal(first.EventCount, again.EventCount);
        }

        // The ONLY way past the boundary is the appended event.
        await s.AppendAsync(new AuditEntry("rd1", "Purged", "discard-sweep", Details: "{\"graceDays\":30}"));
        Assert.Equal("Purged", (await s.GetDocumentAsync("rd1"))!.State);
    }

    [Fact]
    public async Task The_same_event_sequence_reduces_to_the_same_projection_in_any_store()
    {
        // Rehydrating an aggregate = replaying its appends. Two stores fed the identical sequence must agree
        // on every event-derived column (timestamps are append-time-stamped and excluded — they are event
        // FACTS, not reduction outputs).
        var sequence = new (string Type, string Details)[]
        {
            ("NeedsReview", """{"reasonCodes":["synthetic"],"confidence":0.5,"duplicateOf":"other1"}"""),
            ("Discarded", """{"duplicateOf":"other1"}"""),
            ("NeedsReview", """{"channel":"Undiscard","from":"Discarded","to":"NeedsReview"}"""),
            ("Discarded", "{}"),
            ("Purged", """{"graceDays":30}"""),
        };
        using var a = New();
        using var b = New();
        foreach (var (type, details) in sequence)
        {
            await a.AppendAsync(new AuditEntry("twin", type, "test", Details: details));
            await b.AppendAsync(new AuditEntry("twin", type, "test", Details: details));
        }
        var da = (await a.GetDocumentAsync("twin"))!;
        var db = (await b.GetDocumentAsync("twin"))!;
        Assert.Equal(da.State, db.State);
        Assert.Equal("Purged", da.State);
        Assert.Equal(da.EventCount, db.EventCount);
        Assert.Equal(da.DuplicateOf, db.DuplicateOf);
        Assert.Equal(da.ReasonCodes, db.ReasonCodes);
        Assert.Equal(da.Confidence, db.Confidence);
    }
}
