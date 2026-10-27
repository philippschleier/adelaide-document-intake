using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// Regression for the reported bug: a split PARENT (terminal Segmented) lingered in the "Duplikat?" queue.
//
// Repro: a re-run discharge bundle matched a still-filed standalone prescription at intake, so it was flagged as a
// business-key duplicate (NeedsReview carrying a DuplicateOf). The reviewer then SPLIT it — the parent became terminal
// (Segmented) and its child unit re-entered the pipeline. The parent still carried its never-cleared DuplicateOf, and
// the duplicate-filter predicate did not exclude Segmented, so the terminal parent kept satisfying it: it surfaced in
// Duplikat? with Status "Aufgeteilt" while the real prescription child sat in Prüfung. Exercised against the real
// SQLite store; the Azure store composes the identical predicate (see AuditQueryPredicateTests).
public class DuplicateQueueSplitParentTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-audit-{Guid.NewGuid():N}.db");
    private SqliteAuditStore New() => new(_db);
    public void Dispose() { try { File.Delete(_db); } catch { /* best effort */ } }

    private const string Parent = "2a29996572ba";
    private const string Child = "2a29996572ba-u2";
    private const string Standalone = "standalonepres";

    // The full scenario: parent flagged as a possible duplicate at intake → reviewer splits → child re-processed to
    // review for a missing required field (and NOT flagged as a duplicate).
    private static async Task SeedSplitScenarioAsync(SqliteAuditStore s)
    {
        await s.AppendAsync(new AuditEntry(Parent, "Received", "test"));
        await s.AppendAsync(new AuditEntry(Parent, "NeedsReview", "gate",
            Details: $"{{\"disposition\":\"Review\",\"reasons\":[\"mögliches Duplikat\"],\"duplicateOf\":\"{Standalone}\"}}"));
        // Split: child unit created (Received), parent gets its terminal Segmented fan-out event.
        await s.AppendAsync(new AuditEntry(Child, "Received", "reviewer",
            Details: $"{{\"channel\":\"Split\",\"splitFrom\":\"{Parent}\"}}"));
        await s.AppendAsync(new AuditEntry(Parent, "Segmented", "reviewer",
            Details: "{\"unitCount\":2,\"evidence\":\"manual\"}"));
        // Child re-processes and lands in review for a missing field — no duplicateOf.
        await s.AppendAsync(new AuditEntry(Child, "NeedsReview", "gate",
            Details: "{\"disposition\":\"Review\",\"reasons\":[\"Pflichtfeld 'Dokumentendatum' fehlt\"]}"));
    }

    [Fact]
    public async Task Split_parent_is_absent_from_the_duplicate_queue_and_its_badge()
    {
        using var s = New();
        await SeedSplitScenarioAsync(s);

        var parent = await s.GetDocumentAsync(Parent);
        Assert.Equal("Segmented", parent!.State);            // terminal — the file fanned out into units
        Assert.Equal(Standalone, parent.DuplicateOf);        // the intake flag remains on the record's history (audit-true)

        Assert.DoesNotContain(await s.ListDuplicatesAsync(), d => d.DocumentId == Parent); // …but a terminal parent is not open work
        Assert.Equal(0, await s.CountDuplicatesAsync());                                   // list and badge agree
    }

    [Fact]
    public async Task Split_parent_is_absent_from_every_active_queue()
    {
        using var s = New();
        await SeedSplitScenarioAsync(s);

        Assert.DoesNotContain(await s.ListActiveAsync(), d => d.DocumentId == Parent);        // In Bearbeitung / active
        Assert.DoesNotContain(await s.ListReconcilableAsync(), d => d.DocumentId == Parent);  // never auto-re-enqueued
        Assert.DoesNotContain(await s.ListByStateAsync("NeedsReview"), d => d.DocumentId == Parent); // Prüfung
    }

    [Fact]
    public async Task Prescription_child_sits_in_review_exactly_once_and_is_not_a_duplicate()
    {
        using var s = New();
        await SeedSplitScenarioAsync(s);

        var child = await s.GetDocumentAsync(Child);
        Assert.Equal("NeedsReview", child!.State);
        Assert.Null(child.DuplicateOf);

        Assert.Contains(await s.ListByStateAsync("NeedsReview"), d => d.DocumentId == Child); // in Prüfung
        Assert.DoesNotContain(await s.ListDuplicatesAsync(), d => d.DocumentId == Child);     // and NOT in Duplikat?
    }

    [Fact]
    public async Task Before_the_split_the_flagged_parent_IS_an_open_duplicate_in_both_Pruefung_and_Duplikat()
    {
        // Pins the fix's scope: the exclusion is about the TERMINAL transition, not about ignoring DuplicateOf. A LIVE
        // NeedsReview+duplicateOf doc is intentionally surfaced in BOTH Prüfung and Duplikat? (the compare overlay).
        using var s = New();
        await s.AppendAsync(new AuditEntry(Parent, "Received", "test"));
        await s.AppendAsync(new AuditEntry(Parent, "NeedsReview", "gate", Details: $"{{\"duplicateOf\":\"{Standalone}\"}}"));

        Assert.Contains(await s.ListDuplicatesAsync(), d => d.DocumentId == Parent);
        Assert.Contains(await s.ListByStateAsync("NeedsReview"), d => d.DocumentId == Parent);
        Assert.Equal(1, await s.CountDuplicatesAsync());
    }

    [Fact]
    public async Task Both_split_transitions_are_recorded_and_the_chain_stays_intact()
    {
        using var s = New();
        await SeedSplitScenarioAsync(s);

        var parentEvents = await s.GetEventsAsync(Parent);
        Assert.Contains(parentEvents, e => e.Type == "NeedsReview"); // the intake duplicate flag
        Assert.Contains(parentEvents, e => e.Type == "Segmented");   // the split fan-out
        Assert.Contains(await s.GetEventsAsync(Child), e => e.Type == "Received");
        Assert.True((await s.VerifyChainAsync()).Intact); // corrections are new events; history is never overwritten
    }

    [Fact]
    public async Task DuplicateSuspect_with_no_DuplicateOf_still_populates_the_duplicate_queue_and_badge()
    {
        // The OTHER branch of the duplicate filter: the exact-byte layer holds a doc at upload as DuplicateSuspect. Even
        // with NO DuplicateOf on the projection it must populate Duplikat? + its badge (the "State='DuplicateSuspect'"
        // branch). Guards the fix's scope: DuplicateSuspect is a HumanPending state, so the duplicate filter must NEVER
        // apply the reconcile/HumanPending exclusion — if it did, every exact-byte suspect would vanish from the queue.
        using var s = New();
        await s.AppendAsync(new AuditEntry("suspectdoc01", "Received", "test"));
        await s.AppendAsync(new AuditEntry("suspectdoc01", "DuplicateSuspect", "dedup")); // no details ⇒ no DuplicateOf

        var doc = await s.GetDocumentAsync("suspectdoc01");
        Assert.Equal("DuplicateSuspect", doc!.State);
        Assert.Null(doc.DuplicateOf); // matched purely on State, not DuplicateOf

        Assert.Contains(await s.ListDuplicatesAsync(), d => d.DocumentId == "suspectdoc01");
        Assert.Equal(1, await s.CountDuplicatesAsync());
    }
}
