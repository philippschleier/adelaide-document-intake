using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// The policy is now a PURE DELETION WINDOW (docs/CAPTURE.md): capture is unconditional at the model boundary, so
// the old ShouldRetain/SampleFraction selection — which decided WHICH documents were written at all — is retired.
// What remains testable is the window arithmetic and the off-by-default posture: deletion requires opting in.
public class TranscriptRetentionPolicyTests
{
    [Fact]
    public void No_window_by_default()
    {
        var p = new TranscriptRetentionPolicy();
        Assert.Equal(0, p.RetentionDays);
        Assert.False(p.Enabled);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Zero_or_negative_days_is_no_window(int days)
        => Assert.False(new TranscriptRetentionPolicy(days).Enabled);

    [Fact]
    public void A_positive_window_enables_the_purge()
        => Assert.True(new TranscriptRetentionPolicy(RetentionDays: 30).Enabled);

    [Fact]
    public void The_cutoff_is_exactly_window_days_before_now()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddDays(-30), new TranscriptRetentionPolicy(30).CutoffFrom(now));
    }
}
