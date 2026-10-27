using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// The Recycle-Bin census correlates ledger deletions to bin entries on the ONLY field the two sides share: the
// instant. (The bin id is a delete-transaction id with no driveItemId back-reference, and the 14 specimens predate
// the capture build so their rendition sizes are unknowable.) Everything worth getting wrong about a tolerance
// window — the boundary, the sign, ambiguity, ordering — is arithmetic, so it is tested here without a tenant.
public class BinMatchingTests
{
    private static readonly DateTimeOffset T = new(2026, 7, 5, 15, 31, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static SupersedeDeletion Doc(string id = "a1b2c3d4e5f6", double minutesFromT = 0) =>
        new(id, T.AddMinutes(minutesFromT));

    private static BinDeletion Bin(string id, double minutesFromT) => new(id, T.AddMinutes(minutesFromT));

    private static BinMatch One(SupersedeDeletion d, params BinDeletion[] bin) =>
        BinMatching.Correlate(new[] { d }, bin, Window).Single();

    [Fact]
    public void Inside_the_window_matches()
    {
        var m = One(Doc(), Bin("bin-1", 3));
        Assert.True(m.Matched);
        Assert.Equal("bin-1", m.Nearest!.Value.Item.BinItemId);
        Assert.Equal(TimeSpan.FromMinutes(3), m.Nearest!.Value.Offset);
    }

    [Fact]
    public void Outside_the_window_does_not_match()
    {
        var m = One(Doc(), Bin("bin-1", 11));
        Assert.False(m.Matched);
        Assert.Empty(m.Candidates);
        Assert.Null(m.Nearest);
    }

    [Theory]
    [InlineData(10)]   // exactly +window
    [InlineData(-10)]  // exactly -window
    public void The_boundary_is_inclusive(double minutes)
        => Assert.True(One(Doc(), Bin("bin-1", minutes)).Matched);

    [Fact]
    public void One_tick_past_the_boundary_is_excluded()
    {
        var justOutside = new BinDeletion("bin-1", T + Window + TimeSpan.FromTicks(1));
        Assert.False(BinMatching.Correlate(new[] { Doc() }, new[] { justOutside }, Window).Single().Matched);
    }

    // The supersede path deletes the SharePoint item FIRST and appends the terminal event second, so the real-world
    // offset is negative. A window that only looked forward would match nothing at all.
    [Fact]
    public void A_bin_delete_that_precedes_the_event_still_matches()
    {
        var m = One(Doc(), Bin("bin-1", -2));
        Assert.True(m.Matched);
        Assert.Equal(TimeSpan.FromMinutes(-2), m.Nearest!.Value.Offset);
        Assert.Equal(TimeSpan.FromMinutes(2), m.Nearest!.Value.Distance);
    }

    [Fact]
    public void Multiple_candidates_are_all_returned_nearest_first_and_flagged_ambiguous()
    {
        var m = One(Doc(), Bin("bin-far", 8), Bin("bin-near", -1), Bin("bin-mid", 4), Bin("bin-out", 30));
        Assert.True(m.Matched);
        Assert.True(m.Ambiguous);
        Assert.Equal(new[] { "bin-near", "bin-mid", "bin-far" }, m.Candidates.Select(c => c.Item.BinItemId));
        Assert.Equal("bin-near", m.Nearest!.Value.Item.BinItemId);
    }

    [Fact]
    public void Equidistant_candidates_tie_break_ordinally_so_the_output_is_stable()
    {
        var m = One(Doc(), Bin("bbb", 5), Bin("aaa", -5));
        Assert.Equal(new[] { "aaa", "bbb" }, m.Candidates.Select(c => c.Item.BinItemId));
    }

    // A batch supersede (five real ones inside eleven minutes on 2026-07-05) means one bin entry is legitimately a
    // candidate for several documents. The correlation reports that overlap rather than assigning a winner.
    [Fact]
    public void A_bin_entry_may_be_a_candidate_for_several_documents()
    {
        var results = BinMatching.Correlate(
            new[] { Doc("aaaaaaaaaaaa", 0), Doc("bbbbbbbbbbbb", 4) },
            new[] { Bin("bin-1", 2) },
            Window);
        Assert.All(results, r => Assert.Equal("bin-1", r.Nearest!.Value.Item.BinItemId));
    }

    [Fact]
    public void Every_deletion_gets_a_row_in_input_order_including_the_unmatched()
    {
        var results = BinMatching.Correlate(
            new[] { Doc("aaaaaaaaaaaa", 0), Doc("bbbbbbbbbbbb", 600), Doc("cccccccccccc", 1) },
            new[] { Bin("bin-1", 0) },
            Window);
        Assert.Equal(new[] { "aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc" }, results.Select(r => r.Deletion.DocumentId));
        Assert.Equal(new[] { true, false, true }, results.Select(r => r.Matched));
    }

    [Fact]
    public void An_empty_bin_leaves_everything_unmatched()
    {
        var results = BinMatching.Correlate(new[] { Doc() }, Array.Empty<BinDeletion>(), Window);
        Assert.False(results.Single().Matched);
    }

    [Fact]
    public void A_zero_window_admits_only_the_exact_instant()
    {
        var bin = new[] { Bin("exact", 0), Bin("off", 0.001) };
        var m = BinMatching.Correlate(new[] { Doc() }, bin, TimeSpan.Zero).Single();
        Assert.Equal("exact", Assert.Single(m.Candidates).Item.BinItemId);
    }

    [Fact]
    public void A_negative_window_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => BinMatching.Correlate(new[] { Doc() }, Array.Empty<BinDeletion>(), TimeSpan.FromMinutes(-1)));

    // ---- expiry ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Expiry_is_the_deletion_instant_plus_the_retention_period()
    {
        var deleted = new DateTimeOffset(2026, 7, 5, 15, 20, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 15, 20, 0, TimeSpan.Zero), BinMatching.Expiry(deleted, 93));
    }

    [Fact]
    public void Expiry_is_computed_in_absolute_time_regardless_of_the_offset_it_arrives_in()
    {
        var utc = new DateTimeOffset(2026, 7, 5, 15, 20, 0, TimeSpan.Zero);
        var local = utc.ToOffset(TimeSpan.FromHours(2));
        Assert.Equal(BinMatching.Expiry(utc, 93), BinMatching.Expiry(local, 93));
    }

    [Fact]
    public void A_zero_retention_expires_at_the_deletion_instant()
    {
        var deleted = new DateTimeOffset(2026, 7, 5, 15, 20, 0, TimeSpan.Zero);
        Assert.Equal(deleted, BinMatching.Expiry(deleted, 0));
    }

    [Fact]
    public void A_negative_retention_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BinMatching.Expiry(DateTimeOffset.UtcNow, -1));

    [Fact]
    public void Days_until_expiry_floors_a_partial_day_so_it_never_over_promises()
    {
        var expiry = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(57, BinMatching.DaysUntil(expiry, new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero))); // 57.5 → 57
        Assert.Equal(58, BinMatching.DaysUntil(expiry, new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Days_until_expiry_goes_negative_once_it_has_passed()
        => Assert.Equal(-2, BinMatching.DaysUntil(
            new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero)));
}
