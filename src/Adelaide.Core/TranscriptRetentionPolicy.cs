namespace Adelaide.Core;

/// <summary>How long captured content lives — a PURE DELETION WINDOW, nothing else.
///
/// This type used to also decide WHICH documents were captured at all (gate-hit always, plus a deterministic
/// sample of the rest). That selection is retired: capture is now unconditional at the model boundary — gate-off
/// selection is exactly how the corpus starved at 4 rows while 1,500+ documents flowed past it, and the sampled
/// survivors were the review tail, not the population (see docs/CAPTURE.md for the history). What remains here is
/// the one decision that still belongs to configuration: how long the captured PHI is kept before the window
/// purge (TranscriptStore.PurgeOlderThanAsync) removes it.</summary>
/// <param name="RetentionDays">Days to keep captured rows. 0 or less = NO WINDOW: nothing is ever purged by age
/// (erasure via DeleteAsync/ForgetAsync still applies). This is the default — deletion requires opting in,
/// capture does not.</param>
public sealed record TranscriptRetentionPolicy(int RetentionDays = 0)
{
    /// <summary>True when a purge window is configured. NOTE the inversion from the old meaning: this no longer
    /// gates whether anything is WRITTEN — it gates whether old rows are DELETED.</summary>
    public bool Enabled => RetentionDays > 0;

    /// <summary>The cutoff instant for a purge: anything stored before this is past the window.</summary>
    public DateTimeOffset CutoffFrom(DateTimeOffset now) => now.AddDays(-RetentionDays);
}
