using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// The stalled-document gauge (StalledDocuments / IAuditStore.CountStalledAsync): machine-owed documents that have
// not had an ADVANCING event in an unusual interval. It exists because AuditEventTypes made "no advancing event" a
// reachable state and nothing detected it — and because the periodic sweep is filingOnly, so a document stranded in
// any other live state has no periodic recovery at all.
//
// Aging is expressed by moving the CUTOFF rather than the clock: every append lands "now", so a cutoff in the future
// makes everything stale and a cutoff in the past makes everything fresh. That exercises the real query path (the
// same SQL, the same string comparison on LastUpdated) instead of hand-writing backdated rows.
public class StalledDocumentTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-stall-{Guid.NewGuid():N}.db");
    private SqliteAuditStore New() => new(_db);
    public void Dispose() { try { File.Delete(_db); } catch { /* best effort */ } }

    private static DateTimeOffset Future => DateTimeOffset.UtcNow.AddHours(1);
    private static DateTimeOffset Past => DateTimeOffset.UtcNow.AddHours(-1);

    [Fact]
    public async Task Machine_owed_documents_past_the_cutoff_are_counted_and_broken_down_by_state()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("m1", "Received", "test"));
        await s.AppendAsync(new AuditEntry("m2", "Transcribed", "test"));
        await s.AppendAsync(new AuditEntry("m3", "Transcribed", "test"));
        await s.AppendAsync(new AuditEntry("m4", "Filing", "test"));

        var stalled = await s.CountStalledAsync(Future, Future);

        Assert.Equal(4, stalled.Count);
        Assert.Equal(2, stalled.ByState["Transcribed"]);
        Assert.Equal(1, stalled.ByState["Received"]);
        Assert.Equal(1, stalled.ByState["Filing"]);
        Assert.NotNull(stalled.Oldest);
    }

    [Fact]
    public async Task Fresh_documents_are_not_counted()
    {
        using var s = New();
        await s.AppendAsync(new AuditEntry("f1", "Received", "test"));

        var stalled = await s.CountStalledAsync(Past, Past);

        Assert.Equal(0, stalled.Count);
        Assert.Empty(stalled.ByState);
        Assert.Empty(stalled.Sample);
        Assert.Null(stalled.Oldest);   // no group, so nothing to be oldest — not DateTimeOffset.MinValue
    }

    [Fact]
    public async Task Human_pending_documents_are_never_stalled_however_old_they_get()
    {
        // THE design decision, pinned. A document awaiting a reviewer is a QUEUE, not a stall: the machine is not
        // late. This is not fastidiousness — measured on the live ledger (2026-08-11) there were 100 human-pending
        // documents, 87 older than a week, against ZERO machine-owed ones older than fifteen minutes. Folding the
        // two populations together would have shipped a gauge reading 100 on day one, i.e. a gauge nobody reads.
        using var s = New();
        await s.AppendAsync(new AuditEntry("h1", "NeedsReview", "test"));
        await s.AppendAsync(new AuditEntry("h2", "DeadLetter", "test"));
        await s.AppendAsync(new AuditEntry("h3", "DuplicateSuspect", "test"));

        var stalled = await s.CountStalledAsync(Future, Future);

        Assert.Equal(0, stalled.Count);
        Assert.Empty(stalled.ByState);
    }

    [Fact]
    public async Task Terminal_documents_are_never_stalled_including_the_C9_seeded_history()
    {
        // SeededHistory matters most here: the C9 migrator transmutes every non-terminal source row to it at seed
        // time, so a seed can mint rows that are old on arrival with no events at all — if terminal states leaked
        // into this population, a seed would open the gauge at the size of the seeded corpus. (An earlier version
        // of this comment cited "1,000+ rows, LastUpdated years old"; that number was invented, was retracted in
        // the StalledDocuments docstring — the live ledger held NONE when measured 2026-08-11 — and survived here,
        // in the test named for the case, until the failure-composition audit caught the mismatch.)
        using var s = New();
        foreach (var (id, state) in new[] { ("t1", "Filed"), ("t2", "Discarded"), ("t3", "Superseded"),
                                            ("t4", "Segmented"), ("t5", "SeededHistory") })
            await s.AppendAsync(new AuditEntry(id, state, "test"));

        var stalled = await s.CountStalledAsync(Future, Future);

        Assert.Equal(0, stalled.Count);
    }

    [Fact]
    public async Task An_annotated_document_stays_stalled_because_the_annotation_did_not_advance_it()
    {
        // THE case this gauge was built for. An annotation chains and increments EventCount, so a reader watching
        // "events are still arriving" sees a healthy document. LastUpdated is the only thing that tells the truth:
        // nothing has ADVANCED. Asserting on Oldest (not merely on the count) is what makes this discriminating —
        // if the annotation bumped LastUpdated, Oldest would move with it.
        using var s = New();
        await s.AppendAsync(new AuditEntry("x1", "Transcribed", "test"));
        var advancedAt = (await s.GetDocumentAsync("x1"))!.LastUpdated;

        await Task.Delay(15);
        await s.AppendAsync(new AuditEntry("x1", AuditEventTypes.SharePointItemRemoved, "reconcile"));

        var stalled = await s.CountStalledAsync(Future, Future);
        Assert.Equal(1, stalled.Count);
        Assert.Equal(1, stalled.ByState["Transcribed"]);
        Assert.Equal(advancedAt, stalled.Oldest);                          // the annotation did NOT restart the clock
        Assert.Equal(2, (await s.GetDocumentAsync("x1"))!.EventCount);     // ...while events kept arriving
    }

    [Fact]
    public async Task An_advancing_event_clears_the_stall()
    {
        // The mirror: the gauge must fall silent the moment the document actually moves, or it becomes a ratchet
        // that only ever counts up.
        using var s = New();
        await s.AppendAsync(new AuditEntry("x2", "Transcribed", "test"));
        var beforeMove = DateTimeOffset.UtcNow;

        await Task.Delay(15);
        await s.AppendAsync(new AuditEntry("x2", "Filing", "test"));

        Assert.Equal(0, (await s.CountStalledAsync(beforeMove, beforeMove)).Count);
        // ...and it is the FILING lane it now sits in, not the one it left.
        Assert.Equal("Filing", Assert.Single((await s.CountStalledAsync(Future, Future)).ByState).Key);
    }

    [Fact]
    public async Task The_sample_is_oldest_first_and_capped()
    {
        // The sample's ORDER is the whole feature: every other projection query in the stores sorts LastUpdated
        // DESC, which puts precisely the stalled rows last. A sample that inherited that ordering would return the
        // ten LEAST stuck documents — plausible-looking output, useless during an incident.
        using var s = New();
        var ids = new List<string>();
        for (var i = 0; i < StalledDocuments.SampleSize + 3; i++)
        {
            var id = $"s{i:D2}";
            await s.AppendAsync(new AuditEntry(id, "Received", "test"));
            ids.Add(id);
            await Task.Delay(2);
        }

        var stalled = await s.CountStalledAsync(Future, Future);

        Assert.Equal(ids.Count, stalled.Count);
        Assert.Equal(StalledDocuments.SampleSize, stalled.Sample.Count);       // capped, not the whole population
        Assert.Equal(ids.Take(StalledDocuments.SampleSize), stalled.Sample);   // and it is the OLDEST ten, in order
    }

    [Fact]
    public async Task The_sample_obeys_the_same_exclusions_as_the_count()
    {
        // A sample built from a different predicate than the count would hand the operator a document that is not
        // stalled at all — worse than no sample, because it looks authoritative.
        using var s = New();
        await s.AppendAsync(new AuditEntry("q1", "NeedsReview", "test"));
        await s.AppendAsync(new AuditEntry("q2", "Filed", "test"));
        await s.AppendAsync(new AuditEntry("q3", "Extracted", "test"));

        var stalled = await s.CountStalledAsync(Future, Future);

        Assert.Equal("q3", Assert.Single(stalled.Sample));
    }

    [Fact]
    public async Task Oldest_is_the_oldest_across_states_not_within_one()
    {
        // The operator reads oldestAgeMinutes to tell "a batch is draining" from "one document is frozen". That only
        // works if it spans every stalled state rather than reporting whichever group the reader happens to look at.
        using var s = New();
        await s.AppendAsync(new AuditEntry("o1", "Received", "test"));
        var first = (await s.GetDocumentAsync("o1"))!.LastUpdated;

        await Task.Delay(15);
        await s.AppendAsync(new AuditEntry("o2", "Filing", "test"));

        var stalled = await s.CountStalledAsync(Future, Future);
        Assert.Equal(2, stalled.Count);
        Assert.Equal(first, stalled.Oldest);
    }

    [Fact]
    public async Task An_empty_store_reports_a_clean_zero_rather_than_throwing()
    {
        using var s = New();
        var stalled = await s.CountStalledAsync(Future, Future);
        Assert.Equal(0, stalled.Count);
        Assert.Empty(stalled.ByState);
        Assert.Null(stalled.Oldest);
    }

    [Fact]
    public async Task The_cutoff_is_compared_in_UTC_whatever_offset_the_caller_passes()
    {
        // LastUpdated is stored as an "o"-format UTC string and compared lexicographically. A caller handing in a
        // non-UTC DateTimeOffset (a local-time "now minus four hours") must not shift the cutoff by its offset —
        // hence the ToUniversalTime() in the stores. Same instant, same answer.
        using var s = New();
        await s.AppendAsync(new AuditEntry("z1", "Received", "test"));

        var utcCutoff = DateTimeOffset.UtcNow.AddHours(1);
        var shifted = utcCutoff.ToOffset(TimeSpan.FromHours(9)); // same instant, different offset

        Assert.Equal((await s.CountStalledAsync(utcCutoff, utcCutoff)).Count, (await s.CountStalledAsync(shifted, shifted)).Count);
        Assert.Equal(1, (await s.CountStalledAsync(shifted, shifted)).Count);
    }

    [Fact]
    public async Task A_queued_document_gets_the_loose_cutoff_and_a_working_one_the_tight_cutoff()
    {
        // THE two-tier decision, pinned. These two documents are the SAME age; only their state differs, and that
        // must be enough to make one stalled and the other not. A single-cutoff gauge cannot express this, which is
        // why it was either blind to the whole pipeline (loose) or screaming through every bulk intake (tight).
        //
        // Measured over 65 days of the live ledger: Received dwells reach 806 minutes with the system healthy,
        // while no other machine-owed state ever exceeded 0.7 minutes across 6,871 dwells.
        using var s = New();
        await s.AppendAsync(new AuditEntry("k1", "Received", "test"));      // queued behind the intake channel
        await s.AppendAsync(new AuditEntry("k2", "Transcribed", "test"));   // mid-pipeline: should never sit

        // Tight cutoff already passed, loose one has not — exactly the window between the two defaults.
        var stalled = await s.CountStalledAsync(activeOlderThan: Future, queuedOlderThan: Past);

        Assert.Equal("Transcribed", Assert.Single(stalled.ByState).Key);
        Assert.Equal("Transcribed", stalled.OldestState);
        Assert.Equal(["k2"], stalled.Sample);

        // Push the loose cutoff past it too and the queued document joins.
        Assert.Equal(2, (await s.CountStalledAsync(Future, Future)).Count);
    }

    [Fact]
    public async Task An_unknown_state_is_treated_as_working_not_queued()
    {
        // Fail-safe direction: a state nobody classified takes the TIGHT cutoff. A new lifecycle step is far more
        // likely than a new queue, and the cost of guessing wrong the other way is a day of silence.
        using var s = New();
        await s.AppendAsync(new AuditEntry("u1", "SomeFutureStateNobodyListed", "test"));

        Assert.Equal(1, (await s.CountStalledAsync(activeOlderThan: Future, queuedOlderThan: Past)).Count);
    }

    [Fact]
    public async Task OldestState_names_the_lane_the_oldest_document_is_in()
    {
        // The age alone is ambiguous under two cutoffs — 900 minutes in Received is a queue, the same in Extracted
        // is a document that fell out of the pipeline. OldestState is what disambiguates them.
        using var s = New();
        await s.AppendAsync(new AuditEntry("n1", "Extracted", "test"));
        await Task.Delay(15);
        await s.AppendAsync(new AuditEntry("n2", "Received", "test"));

        var stalled = await s.CountStalledAsync(Future, Future);

        Assert.Equal(2, stalled.Count);
        Assert.Equal("Extracted", stalled.OldestState);   // the older one, not merely the first group read
    }
}
