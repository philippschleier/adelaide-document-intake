// EVIDENCE-PRESERVATION KILL-SWITCH.
//
// Every code path that deletes a captured inbox PDF or its review sidecar — the artifacts any later re-scan or
// mis-split repair would depend on — is gated on this switch. It FAILS CLOSED: purging runs ONLY when
// ADELAIDE_PURGE_ENABLED is explicitly a truthy value (1 / true / yes / on). Unset, empty, or malformed => PRESERVE
// (never delete). Read once at startup so the decision cannot flip mid-run. Normal inbox hygiene is ADELAIDE_PURGE_ENABLED=1.
// NOTE: with purging disabled the inbox grows unbounded — an operator freeze is meant to be deliberate and temporary.
internal static class PurgeGate
{
    internal static readonly bool Enabled =
        (Environment.GetEnvironmentVariable("ADELAIDE_PURGE_ENABLED") ?? "").Trim().ToLowerInvariant()
            is "1" or "true" or "yes" or "on";
}

// GRACE WINDOW for the filed-input purge (migration plan C5/D4). With ADELAIDE_FILED_PURGE_GRACE_DAYS set to a
// positive integer, the purge-at-filing DEFERS: the inbox copy of a filed document survives until the reconcile
// tick's aged sweep finds it Filed + SharePoint-verified + older than the window. The window is the period in which
// a human-discovered misfile can still be repaired from the arrival bytes — under R7 (the DI rendition is the record
// of file; the arrival copy is transient) it is the ONLY such period. 0 / unset / malformed = purge at filing (the
// pre-C5 behavior). Subordinate to PurgeGate either way: nothing is ever deleted while the kill-switch is closed.
// Read once at startup so the window cannot flip mid-run.
internal static class FiledPurgeGrace
{
    internal static readonly int Days = DaysFrom(Environment.GetEnvironmentVariable("ADELAIDE_FILED_PURGE_GRACE_DAYS"));

    /// <summary>True when a window is configured: the inline purge defers and the aged sweep owns deletion.</summary>
    internal static bool Deferred => Days > 0;

    /// <summary>Pure parse (testable): positive integer days, else 0 (= purge at filing). Negative/garbage → 0.</summary>
    internal static int DaysFrom(string? value) =>
        int.TryParse((value ?? "").Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 0;
}

// RECOVERY WINDOW for discarded documents (discard-recovery). Deliberately NOT FiledPurgeGrace's parse:
// there, unset = 0 = delete at filing (the pre-C5 behavior); here ARMING PURGE IMPLIES A WINDOW — an
// operator who never set the knob gets the 30-day default, and only an explicit "0" means no recovery
// (undiscard always refused; the aged discard sweep purges on its next tick). There is no configuration in
// which the discard endpoint itself deletes: the sweep is the sole deletion authority, and /api/undiscard
// enforces this window in the command handler (wall clock at the command boundary — reducers never read
// time; expiry becomes state only via the sweep's Purged append). Read once at startup, like every purge
// knob, so the decision cannot flip mid-run.
internal static class DiscardPurgeGrace
{
    internal const int DefaultDays = 30;

    internal static readonly int Days = DaysFrom(Environment.GetEnvironmentVariable("ADELAIDE_DISCARD_PURGE_GRACE_DAYS"));

    /// <summary>Pure parse (testable): explicit non-negative integer → that many days (0 = no recovery,
    /// purge next tick); unset/malformed/negative → the 30-day default (arming-implies-a-window).</summary>
    internal static int DaysFrom(string? value) =>
        int.TryParse((value ?? "").Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : DefaultDays;
}
