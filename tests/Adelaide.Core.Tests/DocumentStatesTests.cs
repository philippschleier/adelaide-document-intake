using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// The terminal-state set now feeds BOTH the store query predicates and the endpoint re-entry guards
// (/api/review, /api/split). These pin the membership a re-file/re-split guard depends on — the
// supersede-lifecycle finding was that Superseded and Discarded documents were re-fileable from a stale tab
// because each endpoint tested only its own single state.
public class DocumentStatesTests
{
    [Theory]
    [InlineData("Filed")]
    [InlineData("Discarded")]
    [InlineData("Superseded")]
    [InlineData("Segmented")]
    [InlineData("SeededHistory")]
    [InlineData("Purged")]
    public void Settled_states_are_terminal(string state) => Assert.True(DocumentStates.IsSettled(state));

    [Theory]
    [InlineData("Received")]
    [InlineData("NeedsReview")]
    [InlineData("DuplicateSuspect")]
    [InlineData("DeadLetter")]
    [InlineData("Filing")]     // in-doubt, NOT terminal: reconcile completes it from the sidecar
    [InlineData("Reviewed")]   // post-review intermediate left by a crash between appends
    [InlineData("Rotated")]
    public void Live_states_are_not_terminal(string state) => Assert.False(DocumentStates.IsSettled(state));

    [Fact]
    public void Null_and_unknown_are_not_terminal()
    {
        Assert.False(DocumentStates.IsSettled(null));   // unseen document → the endpoints' own null handling decides
        Assert.False(DocumentStates.IsSettled(""));
        Assert.False(DocumentStates.IsSettled("filed")); // ordinal, like every other state comparison
    }

    // SEALED ⊆ SETTLED is load-bearing: every query and guard that ignores settled documents — the startup
    // reconcile above all — must ignore sealed ones. A sealed state outside the settled set would be
    // re-driven as in-flight work (ReconcileWhere renders from Settled; the no-sidecar branch re-enqueues).
    [Fact]
    public void Sealed_states_are_a_subset_of_settled()
        => Assert.All(DocumentStates.Sealed, s => Assert.True(DocumentStates.IsSettled(s)));

    [Fact]
    public void Purged_is_sealed_and_nothing_else_is()
    {
        Assert.True(DocumentStates.IsSealed("Purged"));
        foreach (var s in new[] { "Filed", "Discarded", "Superseded", "Segmented", "SeededHistory",
                                  "Received", "NeedsReview", "DuplicateSuspect", "DeadLetter", "Filing", "Reviewed", "Rotated" })
            Assert.False(DocumentStates.IsSealed(s), $"'{s}' must not be sealed — sealed means: accepts no events, ever");
        Assert.False(DocumentStates.IsSealed(null));
        Assert.False(DocumentStates.IsSealed(""));
        Assert.False(DocumentStates.IsSealed("purged")); // ordinal, like IsSettled
    }
}
