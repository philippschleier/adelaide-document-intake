using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

public class KvnrEdgeTests
{
    [Fact] public void Surrounding_whitespace_is_trimmed() => Assert.True(Kvnr.IsValid("  Z000000005  "));

    [Fact] public void Check_digit_zero_is_valid() => Assert.True(Kvnr.IsValid("A000000040")); // mod-10 boundary (check digit 0)
}

public class ReviewGateEdgeTests
{
    private static readonly PipelineOptions Opt = new(MinQuality: 0.60, MinConfidence: 0.80);

    private static PatientMatch Single() =>
        new(MatchOutcome.Single, new PatientRef(1, "Z000000005", "T", "T", new DateOnly(1956, 5, 29), "Active"), "kvnr");

    private static ProcessingResult Run(double quality, ExtractedData data, PatientMatch m)
    {
        var t = new Transcript(GateFixtures.Transcript, 1, quality, Array.Empty<DecodedBarcode>());
        return IntakePipeline.Decide(new DocumentUnit("u", 1, 1, t), t, data, m, Opt);
    }

    [Fact]
    public void Exactly_at_thresholds_auto_files() // strict '<' means the boundary must pass
    {
        var r = Run(0.60, new ExtractedData(new[] { new ExtractedField("KVNR", "Z000000005", 0.80, "barcode") }), Single());
        Assert.Equal(Disposition.AutoFile, r.Disposition);
        Assert.Empty(r.Reasons);
    }

    [Fact]
    public void Multiple_failures_accumulate_all_reasons()
    {
        var r = Run(0.40,
            new ExtractedData(new[] { new ExtractedField("KVNR", "Z000000004", 0.50, "vision") }),
            new PatientMatch(MatchOutcome.None, null, "none"));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.True(r.Reasons.Count >= 3, $"expected >=3 reasons, got {r.Reasons.Count}: {string.Join(" | ", r.Reasons)}");
    }

    [Fact]
    public void Empty_extraction_routes_to_review_on_low_confidence()
    {
        var r = Run(0.95, new ExtractedData(Array.Empty<ExtractedField>()), Single());
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("confidence"));
    }
}

public class PatientMatchingEdgeTests
{
    private static PatientRef P(int id, string status) => new(id, "Z000000005", "M", "M", new DateOnly(1956, 5, 29), status);

    [Fact]
    public void Three_hits_one_active_resolves_to_the_active_one()
    {
        var m = PatientMatching.Resolve(new[] { P(1, "Inactive"), P(2, "Active"), P(3, "Inactive") }, "kvnr");
        Assert.Equal(MatchOutcome.Single, m.Outcome);
        Assert.Equal(2, m.Patient!.ListItemId);
    }

    [Fact]
    public void All_inactive_hits_is_multiple() =>
        Assert.Equal(MatchOutcome.Multiple, PatientMatching.Resolve(new[] { P(1, "Inactive"), P(2, "Inactive") }, "kvnr").Outcome);

    [Theory]
    [InlineData("Aktiv", true)]
    [InlineData("aktiv", true)]
    [InlineData("active", true)]
    [InlineData("", false)]
    [InlineData("Inaktiv", false)]
    public void IsActive_status_variants(string status, bool expected) =>
        Assert.Equal(expected, PatientMatching.IsActive(P(1, status)));
}

public class CatalogVocabEdgeTests
{
    [Fact] public void Find_unknown_type_is_null() => Assert.Null(DocumentCatalog.Find("Nonexistent"));

    [Fact] public void Vocab_unknown_column_is_null() => Assert.Null(Vocab.For("Nonexistent"));

    [Fact]
    public void IsFree_distinguishes_choice_from_freetext()
    {
        Assert.False(Vocab.IsFree("CareLevel"));   // a choice column
        Assert.True(Vocab.IsFree("DocSubject"));   // free text
    }
}
