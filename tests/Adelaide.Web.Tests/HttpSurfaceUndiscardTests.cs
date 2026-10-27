using System.Net;
using System.Text.Json;
using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Web.Tests;

// POST /api/undiscard against the real out-of-process host: the guarded exit from Discarded. Uses ONLY the
// fixture's dedicated Undiscard* specimens (a successful undiscard mutates the shared ledger — that is the
// feature under test), plus read-only probes of the ledger to assert what landed.
[Collection("http-surface")]
public sealed class HttpSurfaceUndiscardTests(HttpSurfaceFixture fx)
{
    private static StringContent Reason(string text = "synthetic undo reason") =>
        HttpSurfaceFixture.Json(JsonSerializer.Serialize(new { reason = text }));

    private async Task<DocumentAudit?> DocAsync(string id)
    {
        var store = new SqliteAuditStore(fx.LedgerPath, "test");
        var d = await store.GetDocumentAsync(id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return d;
    }

    private async Task<IReadOnlyList<AuditEvent>> EventsAsync(string id)
    {
        var store = new SqliteAuditStore(fx.LedgerPath, "test");
        var e = await store.GetEventsAsync(id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return e;
    }

    /// <summary>Undiscard is rejected from every non-Discarded state — settled states get the per-state 409
    /// text (Purged included: sealed), live/parked states the generic refusal. Unknown ids 404.</summary>
    [Theory]
    [InlineData(HttpSurfaceFixture.Filed, HttpStatusCode.Conflict)]
    [InlineData(HttpSurfaceFixture.NeedsReview, HttpStatusCode.Conflict)]
    [InlineData(HttpSurfaceFixture.DuplicateSuspect, HttpStatusCode.Conflict)]
    [InlineData(HttpSurfaceFixture.DeadLetter, HttpStatusCode.Conflict)]
    [InlineData(HttpSurfaceFixture.Segmented, HttpStatusCode.Conflict)]
    [InlineData(HttpSurfaceFixture.Superseded, HttpStatusCode.Conflict)]
    [InlineData(HttpSurfaceFixture.SeededHistory, HttpStatusCode.Conflict)]
    [InlineData(HttpSurfaceFixture.Purged, HttpStatusCode.Conflict)]
    [InlineData(HttpSurfaceFixture.Unknown, HttpStatusCode.NotFound)]
    public async Task Undiscard_is_refused_from_every_non_discarded_state(string id, HttpStatusCode expected)
    {
        var r = await fx.AsReviewer(HttpMethod.Post, $"/api/undiscard?id={id}", csrf: true, body: Reason());
        Assert.Equal(expected, r.StatusCode);
    }

    [Fact]
    public async Task A_discard_with_no_restorable_predecessor_is_refused()
    {
        // The fixture's plain Discarded specimen has a LONE Discarded event — nothing to restore into.
        var r = await fx.AsReviewer(HttpMethod.Post, $"/api/undiscard?id={HttpSurfaceFixture.Discarded}", csrf: true, body: Reason());
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Vorzustand", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_free_text_reason_is_required()
    {
        var r = await fx.AsReviewer(HttpMethod.Post, $"/api/undiscard?id={HttpSurfaceFixture.UndiscardReview}", csrf: true,
            body: HttpSurfaceFixture.Json("""{"reason":"   "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Undiscard_is_refused_when_the_pdf_is_missing()
    {
        var r = await fx.AsReviewer(HttpMethod.Post, $"/api/undiscard?id={HttpSurfaceFixture.UndiscardNoPdf}", csrf: true, body: Reason());
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Original-PDF", await r.Content.ReadAsStringAsync());
    }

    /// <summary>The NeedsReview-provenance happy path: one appended event whose Type is the predecessor;
    /// Details structural (channel/from/to/actor, NEVER the reason prose); the prose in the erasable
    /// projection Reasons carrying the event's correlation id; DuplicateOf survived via COALESCE; the
    /// document id unchanged.</summary>
    [Fact]
    public async Task Undiscard_restores_a_NeedsReview_provenance_document_in_place()
    {
        var before = await EventsAsync(HttpSurfaceFixture.UndiscardReview);

        var r = await fx.AsReviewer(HttpMethod.Post, $"/api/undiscard?id={HttpSurfaceFixture.UndiscardReview}", csrf: true,
            body: Reason("kein Duplikat - synthetic"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("NeedsReview", await r.Content.ReadAsStringAsync());

        var doc = (await DocAsync(HttpSurfaceFixture.UndiscardReview))!;
        Assert.Equal("NeedsReview", doc.State);
        Assert.Equal(HttpSurfaceFixture.Filed, doc.DuplicateOf);            // COALESCE kept the flag — back to the decision point
        Assert.StartsWith("wiederhergestellt [", doc.Reasons);              // prose lives in the projection…
        Assert.Contains("kein Duplikat - synthetic", doc.Reasons);

        var events = await EventsAsync(HttpSurfaceFixture.UndiscardReview);
        Assert.Equal(before.Count + 1, events.Count);                       // exactly ONE appended event
        var undo = events[^1];
        Assert.Equal("NeedsReview", undo.Type);
        Assert.NotNull(undo.CorrelationId);
        Assert.Contains(undo.CorrelationId![..8], doc.Reasons);             // …and carries the event's correlation id
        using var details = JsonDocument.Parse(undo.Details);
        Assert.Equal("Undiscard", details.RootElement.GetProperty("channel").GetString());
        Assert.Equal("Discarded", details.RootElement.GetProperty("from").GetString());
        Assert.Equal("NeedsReview", details.RootElement.GetProperty("to").GetString());
        Assert.False(string.IsNullOrEmpty(details.RootElement.GetProperty("actor").GetString()));
        Assert.DoesNotContain("kein Duplikat", undo.Details);               // the prose never touches the chain (C6)
    }

    /// <summary>The list endpoint's affordance flag is the SERVER's answer, computed with the same guard the
    /// POST runs: true only where an undiscard would succeed right now. Asserted on the two specimens whose
    /// state never changes across the collection — artifacts present → true, artifacts gone → false.</summary>
    [Fact]
    public async Task The_documents_list_marks_undiscardable_only_where_the_POST_would_succeed()
    {
        var r = await fx.AsReviewer(HttpMethod.Get, "/api/documents?scope=Discarded");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var rows = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        bool FlagOf(string id) => rows.RootElement.EnumerateArray()
            .First(e => e.GetProperty("documentId").GetString() == id)
            .GetProperty("undiscardable").GetBoolean();
        Assert.True(FlagOf(HttpSurfaceFixture.UndiscardAffordance));   // in-window, artifacts present
        Assert.False(FlagOf(HttpSurfaceFixture.UndiscardNoPdf));       // artifacts gone → no affordance
    }

    [Fact]
    public async Task Undiscard_restores_a_DuplicateSuspect_provenance_document_on_the_pdf_alone()
    {
        // The specimen deliberately has NO review sidecar — a suspect never ran the pipeline.
        Assert.False(File.Exists(Path.Combine(fx.InboxPath, HttpSurfaceFixture.UndiscardSuspect + ".review.json")));

        var r = await fx.AsReviewer(HttpMethod.Post, $"/api/undiscard?id={HttpSurfaceFixture.UndiscardSuspect}", csrf: true, body: Reason());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("DuplicateSuspect", (await DocAsync(HttpSurfaceFixture.UndiscardSuspect))!.State);
    }

    /// <summary>discard → undiscard → discard → undiscard, through the REAL endpoints, with DIFFERENT
    /// predecessors on the two cycles: the second restore must come from the LATEST Discarded event's
    /// predecessor (DuplicateSuspect), not the first's (NeedsReview) — a first-scan implementation fails
    /// exactly here. Document id stable throughout.</summary>
    [Fact]
    public async Task Repeated_discard_undiscard_cycles_restore_the_latest_predecessor_each_time()
    {
        const string id = HttpSurfaceFixture.UndiscardCycle;

        // Cycle 1: NeedsReview → Discarded → back to NeedsReview.
        var d1 = await fx.AsReviewer(HttpMethod.Post, $"/api/duplicate-discard?id={id}", csrf: true);
        Assert.Equal(HttpStatusCode.OK, d1.StatusCode);
        var u1 = await fx.AsReviewer(HttpMethod.Post, $"/api/undiscard?id={id}", csrf: true, body: Reason("cycle 1"));
        Assert.Equal(HttpStatusCode.OK, u1.StatusCode);
        Assert.Contains("NeedsReview", await u1.Content.ReadAsStringAsync());

        // Force a DIFFERENT predecessor for cycle 2 (no endpoint moves NeedsReview→DuplicateSuspect).
        await fx.AppendEventAsync(id, "DuplicateSuspect", $$"""{"duplicateOf":"{{HttpSurfaceFixture.Filed}}"}""");

        // Cycle 2: DuplicateSuspect → Discarded → back to DuplicateSuspect (NOT NeedsReview).
        var d2 = await fx.AsReviewer(HttpMethod.Post, $"/api/duplicate-discard?id={id}", csrf: true);
        Assert.Equal(HttpStatusCode.OK, d2.StatusCode);
        var u2 = await fx.AsReviewer(HttpMethod.Post, $"/api/undiscard?id={id}", csrf: true, body: Reason("cycle 2"));
        Assert.Equal(HttpStatusCode.OK, u2.StatusCode);
        Assert.Contains("DuplicateSuspect", await u2.Content.ReadAsStringAsync());

        var doc = (await DocAsync(id))!;
        Assert.Equal("DuplicateSuspect", doc.State);
        Assert.Equal(id, doc.DocumentId);   // stable id across every cycle — nothing re-minted
    }
}
