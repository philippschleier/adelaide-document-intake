using Adelaide.Audit;
using Xunit;

namespace Adelaide.Audit.Tests;

// The three queue predicates (active list, reconcile, duplicate filter) must all be built from ONE terminal-state set,
// so a new terminal state can't be added to some and missed on others — the exact drift that let a split parent
// (Segmented) leak into the "Duplikat?" filter. Both stores (SqliteAuditStore + AzureSqlAuditStore) compose their WHERE
// clauses from AuditQueryPredicates, so asserting on it here covers the Azure predicate the same as the SQLite one
// (the Azure store can't be exercised against a live SQL Server in this environment; sharing the source is what keeps
// the two from diverging, and this test guards that shared source).
public class AuditQueryPredicateTests
{
    [Fact]
    public void Segmented_and_the_other_resolved_states_are_terminal()
    {
        Assert.Contains("Segmented", AuditQueryPredicates.Settled); // the regression: a split parent is terminal
        Assert.Contains("Filed", AuditQueryPredicates.Settled);
        Assert.Contains("Discarded", AuditQueryPredicates.Settled);
        Assert.Contains("Superseded", AuditQueryPredicates.Settled);
    }

    [Fact]
    public void Predicates_and_endpoint_guards_read_the_same_terminal_source()
    {
        // The set moved to Adelaide.Core so the endpoint re-entry guards (/api/review, /api/split) share it. If a
        // future edit re-declares a local copy here, the queries and the guards can drift — exactly the class of
        // bug this file exists to prevent, one layer up.
        Assert.Same(Adelaide.Core.DocumentStates.Settled, AuditQueryPredicates.Settled);
    }

    [Fact]
    public void Duplicate_filter_excludes_every_terminal_state()
    {
        foreach (var state in AuditQueryPredicates.Settled)
            Assert.Contains($"'{state}'", AuditQueryPredicates.DuplicateWhere); // Segmented specifically must be here now
    }

    [Fact]
    public void Active_and_duplicate_predicates_share_the_same_terminal_exclusion_core()
    {
        // Both must exclude EXACTLY the terminal set — extract each "State NOT IN (...)" list and compare, order-insensitive.
        Assert.Equal(TerminalNotInList(AuditQueryPredicates.ActiveWhere),
                     TerminalNotInList(AuditQueryPredicates.DuplicateWhere));
    }

    [Fact]
    public void Reconcile_excludes_the_terminal_core_plus_the_human_pending_states()
    {
        foreach (var state in AuditQueryPredicates.Settled.Concat(AuditQueryPredicates.HumanPending))
            Assert.Contains($"'{state}'", AuditQueryPredicates.ReconcileWhere);
    }

    [Fact]
    public void Duplicate_filter_still_carries_the_duplicate_condition()
    {
        // The fix narrows the predicate; it must not have dropped the two dedup layers it spans.
        Assert.Contains("State='DuplicateSuspect'", AuditQueryPredicates.DuplicateWhere);
        Assert.Contains("DuplicateOf IS NOT NULL", AuditQueryPredicates.DuplicateWhere);
    }

    // The normalized "State NOT IN (...)" item list from a WHERE fragment, sorted so order differences don't matter.
    private static string TerminalNotInList(string where)
    {
        const string marker = "State NOT IN (";
        var start = where.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = where.IndexOf(')', start);
        var items = where[start..end].Split(',').Select(x => x.Trim()).OrderBy(x => x, StringComparer.Ordinal);
        return string.Join(",", items);
    }
}
