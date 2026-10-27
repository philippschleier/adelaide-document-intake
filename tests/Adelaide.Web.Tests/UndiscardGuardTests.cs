using Adelaide.Core;
using Xunit;

namespace Adelaide.Web.Tests;

// The pure undiscard decision (UndiscardGuard.Evaluate): predecessor derivation from the LATEST Discarded
// event, the whitelist, the window boundary with an injected clock, and the per-provenance artifact
// requirements. Pure-function tests carry the window boundary because the HTTP harness cannot move the
// clock — the store stamps event times and the handler reads UtcNow.
public class UndiscardGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 08, 01, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<AuditEvent> Stream(params string[] types)
    {
        var list = new List<AuditEvent>();
        for (var i = 0; i < types.Length; i++)
            list.Add(new AuditEvent(i + 1, "doc1", null, types[i], T0.AddMinutes(i), "test", null, "{}", $"h{i}"));
        return list;
    }

    private static UndiscardGuard.Decision Eval(IReadOnlyList<AuditEvent> events, DateTimeOffset now,
        int windowDays = 30, bool pdf = true, bool sidecar = true)
        => UndiscardGuard.Evaluate(events, now, windowDays, pdf, sidecar);

    [Fact]
    public void Predecessor_comes_from_the_LATEST_discard_not_the_first()
    {
        // First discard's predecessor is DuplicateSuspect, the latest's is NeedsReview — a first-scan
        // implementation returns the wrong state here, and vice versa in the mirrored stream.
        var d1 = Eval(Stream("DuplicateSuspect", "Discarded", "NeedsReview", "Discarded"), T0.AddDays(1));
        Assert.Equal(UndiscardGuard.Result.Ok, d1.Outcome);
        Assert.Equal("NeedsReview", d1.Predecessor);

        var d2 = Eval(Stream("NeedsReview", "Discarded", "DuplicateSuspect", "Discarded"), T0.AddDays(1), sidecar: false);
        Assert.Equal(UndiscardGuard.Result.Ok, d2.Outcome);
        Assert.Equal("DuplicateSuspect", d2.Predecessor);
    }

    [Fact]
    public void Annotations_are_transparent_to_the_predecessor_scan()
    {
        var d = Eval(Stream("NeedsReview", AuditEventTypes.SharePointItemRemoved, "Discarded"), T0.AddDays(1));
        Assert.Equal(UndiscardGuard.Result.Ok, d.Outcome);
        Assert.Equal("NeedsReview", d.Predecessor);
    }

    [Theory]
    [InlineData("Received")]  // re-entering Received would re-run the pipeline — not a restore
    [InlineData("Filed")]     // a filed predecessor means this stream is not a duplicate-discard shape at all
    [InlineData("Reviewed")]  // crash intermediate
    public void Non_whitelisted_predecessors_refuse(string predecessor)
    {
        var d = Eval(Stream(predecessor, "Discarded"), T0.AddDays(1));
        Assert.Equal(UndiscardGuard.Result.UnknownPredecessor, d.Outcome);
        Assert.Equal(predecessor, d.Predecessor);
    }

    [Fact]
    public void A_lone_discard_and_a_never_discarded_stream_both_refuse()
    {
        Assert.Equal(UndiscardGuard.Result.UnknownPredecessor, Eval(Stream("Discarded"), T0.AddDays(1)).Outcome);
        Assert.Equal(UndiscardGuard.Result.NotDiscarded, Eval(Stream("Received", "NeedsReview"), T0.AddDays(1)).Outcome);
    }

    [Fact]
    public void Window_boundary_is_inclusive_inside_exclusive_outside()
    {
        var events = Stream("NeedsReview", "Discarded");       // discardedAt = T0 + 1min
        var discardedAt = events[^1].At;
        Assert.Equal(UndiscardGuard.Result.Ok, Eval(events, discardedAt.AddDays(30)).Outcome);                      // just inside (the boundary instant itself)
        Assert.Equal(UndiscardGuard.Result.WindowExpired, Eval(events, discardedAt.AddDays(30).AddSeconds(1)).Outcome); // just outside
    }

    [Fact]
    public void Explicit_zero_window_refuses_any_later_undiscard()
    {
        var events = Stream("NeedsReview", "Discarded");
        Assert.Equal(UndiscardGuard.Result.WindowExpired, Eval(events, events[^1].At.AddSeconds(1), windowDays: 0).Outcome);
    }

    [Fact]
    public void Artifacts_are_required_per_provenance()
    {
        var review = Stream("NeedsReview", "Discarded");
        Assert.Equal(UndiscardGuard.Result.PdfMissing, Eval(review, T0.AddDays(1), pdf: false).Outcome);           // pdf outranks sidecar
        Assert.Equal(UndiscardGuard.Result.SidecarMissing, Eval(review, T0.AddDays(1), sidecar: false).Outcome);   // NeedsReview needs its sidecar

        var suspect = Stream("DuplicateSuspect", "Discarded");
        Assert.Equal(UndiscardGuard.Result.Ok, Eval(suspect, T0.AddDays(1), sidecar: false).Outcome);              // a suspect never had one
    }

    [Fact]
    public void Evaluate_is_pure_in_its_inputs()
    {
        // Same inputs, same answer — the clock is an argument, never ambient. (The ambient-clock discipline
        // for the projection itself is pinned store-side in ReplayDeterminismTests.)
        var events = Stream("NeedsReview", "Discarded");
        var a = Eval(events, T0.AddDays(2));
        var b = Eval(events, T0.AddDays(2));
        Assert.Equal(a, b);
    }
}

// The recovery-window knob parse: ARMING-IMPLIES-A-WINDOW, deliberately not FiledPurgeGrace's unset=0.
// An operator who arms ADELAIDE_PURGE_ENABLED without ever hearing of this knob gets 30 days of recovery,
// not silent next-tick deletion; only an explicit "0" opts out of recovery.
public class DiscardPurgeGraceTests
{
    [Theory]
    [InlineData(null, 30)]      // unset → the default window
    [InlineData("", 30)]
    [InlineData("garbage", 30)]
    [InlineData("-5", 30)]      // negative is malformed, not an opt-out
    [InlineData("0", 0)]        // the ONLY spelling of "no recovery, purge next tick"
    [InlineData("14", 14)]
    public void Arming_implies_a_window(string? env, int expected)
        => Assert.Equal(expected, DiscardPurgeGrace.DaysFrom(env));
}
