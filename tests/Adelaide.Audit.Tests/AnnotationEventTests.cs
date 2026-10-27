using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// ANNOTATION events (AuditEventTypes): appended to the hash chain exactly like any other event — Seq, PrevHash,
// tamper-evidence, EventCount — but they leave the projection's State alone. They exist because State was the ONE
// projection column that could not abstain (every other column is COALESCE keep-prior-on-null), which made a whole
// class of true facts unrecordable: a deferred side effect completing is a fact ABOUT a document, not a change to
// where it sits in its lifecycle.
//
// The hazard these tests guard is the mirror of the bug the mechanism fixed: a type wrongly treated as non-advancing
// makes documents STOP ADVANCING silently. So both directions are pinned — annotations must not move State, and
// ordinary types must still move it.
public class AnnotationEventTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-annot-{Guid.NewGuid():N}.db");
    private SqliteAuditStore New() => new(_db);
    public void Dispose() { try { File.Delete(_db); } catch { /* best effort */ } }

    [Fact]
    public async Task Annotation_chains_but_leaves_State_untouched()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("a1", "Received", "test"));
        await s.AppendAsync(new AuditEntry("a1", "Filed", "test", Details: "{\"driveItemId\":\"drv-1\"}"));

        await s.AppendAsync(new AuditEntry("a1", AuditEventTypes.SharePointItemRemoved, "reconcile",
            Details: "{\"driveItemIdDeleted\":\"drv-old\"}"));

        var d = await s.GetDocumentAsync("a1");
        Assert.Equal("Filed", d!.State);        // NOT "SharePointItemRemoved" — the lifecycle did not move
        Assert.Equal(3, d.EventCount);          // but it IS an event: it counts
        Assert.Equal("drv-1", d.DriveItemId);   // and it did not clobber other projection columns

        var events = await s.GetEventsAsync("a1");
        Assert.Equal(3, events.Count);
        Assert.Equal(AuditEventTypes.SharePointItemRemoved, events[^1].Type);
    }

    [Fact]
    public async Task Annotation_does_not_break_the_hash_chain()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("a2", "Received", "test"));
        await s.AppendAsync(new AuditEntry("a2", AuditEventTypes.SharePointItemRemoved, "reconcile"));
        await s.AppendAsync(new AuditEntry("a2", "Filed", "test"));

        var v = await s.VerifyChainAsync();
        Assert.True(v.Intact);                  // annotations hash and chain like anything else
        Assert.Equal(0, v.FirstBrokenSeq);
        Assert.Equal(3, v.EventCount);
    }

    [Fact]
    public async Task An_advancing_event_after_an_annotation_still_moves_State()
    {
        // The failure mode worth fearing is a document that silently stops advancing. Prove the annotation does not
        // "stick" — the next real lifecycle event takes over normally.
        using var s = New();
        await s.AppendAsync(new AuditEntry("a3", "Received", "test"));
        await s.AppendAsync(new AuditEntry("a3", AuditEventTypes.SharePointItemRemoved, "reconcile"));
        Assert.Equal("Received", (await s.GetDocumentAsync("a3"))!.State);

        await s.AppendAsync(new AuditEntry("a3", "Transcribed", "test"));
        Assert.Equal("Transcribed", (await s.GetDocumentAsync("a3"))!.State);
    }

    [Fact]
    public async Task Annotation_as_the_very_first_event_falls_back_to_its_own_type()
    {
        // A STORE-LEVEL SAFETY NET, not a supported path. No prior row means no State to preserve, so the INSERT
        // branch must not write NULL into a NOT NULL column. It does mint a row whose State is outside the lifecycle
        // vocabulary, which is why callers must not rely on it: DriveReclassifyAsync refuses to annotate a document
        // with no projection row precisely so this branch is never reached in production.
        using var s = New();
        await s.AppendAsync(new AuditEntry("a4", AuditEventTypes.SharePointItemRemoved, "reconcile"));

        var d = await s.GetDocumentAsync("a4");
        Assert.NotNull(d);
        Assert.Equal(AuditEventTypes.SharePointItemRemoved, d!.State);
        Assert.Equal(1, d.EventCount);
    }

    [Fact]
    public async Task Annotation_does_not_bump_LastUpdated_so_lifecycle_timers_are_not_regranted()
    {
        // LastUpdated drives cutoffs — the C5 aged-filed-input grace window among them. If a bookkeeping append
        // restarted that clock, a deferred completion would silently extend retention for a settled document. So an
        // annotation is neutral to lifecycle timers for the same reason it is neutral to State.
        using var s = New();
        await s.AppendAsync(new AuditEntry("a5", "Received", "test"));
        await s.AppendAsync(new AuditEntry("a5", "Filed", "test"));
        var filedAt = (await s.GetDocumentAsync("a5"))!.LastUpdated;

        await Task.Delay(15);
        await s.AppendAsync(new AuditEntry("a5", AuditEventTypes.SharePointItemRemoved, "reconcile"));

        var after = (await s.GetDocumentAsync("a5"))!;
        Assert.Equal(filedAt, after.LastUpdated);   // clock untouched
        Assert.Equal(3, after.EventCount);          // but it still counted as an event

        await Task.Delay(15);
        await s.AppendAsync(new AuditEntry("a5", "Discarded", "test"));
        Assert.True((await s.GetDocumentAsync("a5"))!.LastUpdated > filedAt); // a real event still moves it
    }

    [Fact]
    public void The_non_advancing_set_is_tiny_and_explicit()
    {
        // Unknown types MUST advance — the safe default. A regression here (e.g. someone inverting the predicate, or
        // introducing a prefix convention) would silently freeze whole classes of document.
        Assert.False(AuditEventTypes.Advances(AuditEventTypes.SharePointItemRemoved));
        foreach (var t in new[] { "Received", "Transcribed", "Extracted", "Matched", "NeedsReview", "Reviewed",
                                  "Filing", "Filed", "Segmented", "Superseded", "Discarded", "DeadLetter",
                                  "Rotated", "DuplicateSuspect", "SomeFutureTypeNobodyListed" })
            Assert.True(AuditEventTypes.Advances(t), $"'{t}' must advance State — a non-advancing type freezes documents silently");
    }
}
