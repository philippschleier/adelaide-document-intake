using Adelaide.Core;

// The pure undiscard decision (discard-recovery): everything POST /api/undiscard must check that does not
// touch storage — predecessor derivation, window arithmetic, per-provenance artifact requirements. Pure so
// the window boundary is unit-testable with an injected clock: the HANDLER reads the wall clock and passes
// it in; reducers never do, and expiry only ever becomes true state via the sweep's Purged append.
internal static class UndiscardGuard
{
    /// <summary>States a discarded document may be restored INTO. A whitelist, not "whatever preceded": the
    /// stream can contain states that make no sense to re-enter directly (Received would re-run the pipeline;
    /// Filing/Reviewed are crash intermediates), and an unknown predecessor means an unforeseen history —
    /// refuse and let a human look. `DuplicateSuspect` documents never ran the pipeline, so their restore
    /// needs no review sidecar; `NeedsReview` restores need the sidecar the review screen serves.</summary>
    internal static readonly string[] RestorablePredecessors = { "NeedsReview", "DuplicateSuspect" };

    internal enum Result { Ok, NotDiscarded, UnknownPredecessor, WindowExpired, PdfMissing, SidecarMissing }

    internal sealed record Decision(Result Outcome, string? Predecessor, DateTimeOffset? DiscardedAt);

    /// <summary>Scan back from the LATEST Discarded event — repeated discard/undiscard cycles put several in
    /// the stream, and the restore target is the state before the most recent discard, not the first — for
    /// the last ADVANCING type before it (annotations are transparent, exactly as the projection treats
    /// them). Window: inside means <c>now ≤ discardedAt + windowDays</c>; an explicit 0-day window therefore
    /// refuses any later undiscard, matching the knob's "purge next tick, no recovery" meaning.</summary>
    internal static Decision Evaluate(IReadOnlyList<AuditEvent> events, DateTimeOffset now, int windowDays,
        bool pdfExists, bool sidecarExists)
    {
        var lastDiscard = -1;
        for (var i = events.Count - 1; i >= 0; i--)
            if (events[i].Type == "Discarded") { lastDiscard = i; break; }
        if (lastDiscard < 0) return new(Result.NotDiscarded, null, null);
        var discardedAt = events[lastDiscard].At;

        string? predecessor = null;
        for (var i = lastDiscard - 1; i >= 0; i--)
            if (AuditEventTypes.Advances(events[i].Type)) { predecessor = events[i].Type; break; }
        if (predecessor is null || !Array.Exists(RestorablePredecessors, p => string.Equals(p, predecessor, StringComparison.Ordinal)))
            return new(Result.UnknownPredecessor, predecessor, discardedAt);

        if (now > discardedAt.AddDays(windowDays)) return new(Result.WindowExpired, predecessor, discardedAt);
        if (!pdfExists) return new(Result.PdfMissing, predecessor, discardedAt);
        if (predecessor == "NeedsReview" && !sidecarExists) return new(Result.SidecarMissing, predecessor, discardedAt);
        return new(Result.Ok, predecessor, discardedAt);
    }
}
