using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

public class PatientMatchingTests
{
    private static PatientRef P(int id, string status) =>
        new(id, "Z000000005", "Mustermann", "M", new DateOnly(1956, 5, 29), status);

    [Fact]
    public void No_hits_is_none() =>
        Assert.Equal(MatchOutcome.None, PatientMatching.Resolve(Array.Empty<PatientRef>(), "kvnr").Outcome);

    [Fact]
    public void One_hit_is_single() =>
        Assert.Equal(MatchOutcome.Single, PatientMatching.Resolve(new[] { P(1, "Active") }, "kvnr").Outcome);

    [Fact]
    public void Two_active_hits_is_multiple() =>
        Assert.Equal(MatchOutcome.Multiple, PatientMatching.Resolve(new[] { P(1, "Active"), P(2, "Active") }, "kvnr").Outcome);

    [Fact]
    public void Two_hits_one_active_resolves_to_the_active_one()
    {
        var m = PatientMatching.Resolve(new[] { P(1, "Inactive"), P(2, "Active") }, "kvnr");
        Assert.Equal(MatchOutcome.Single, m.Outcome);
        Assert.Equal(2, m.Patient!.ListItemId);
    }

    [Theory]
    [InlineData("Active", true)]
    [InlineData("Inactive", false)]
    public void IsActive_handles_real_status_values(string status, bool expected) =>
        Assert.Equal(expected, PatientMatching.IsActive(P(1, status)));
}
