using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// The extraction-accuracy oracle: model-extracted identity value vs the matched patient record's value.
//
// Fixtures use the synthetic KVNRs (Z000000005 / T000000002) and fake names required by the project's test-fixture
// rule — the sample PDFs carry real KVNRs and none may appear in code.
public class IdentityAgreementTests
{
    private const string SyntheticKvnr = "Z000000005";

    [Fact]
    public void Identical_values_agree_and_carry_no_shape()
    {
        var r = IdentityAgreement.Compare("KVNR", SyntheticKvnr, SyntheticKvnr)!;
        Assert.True(r.Agreed);
        Assert.Null(r.Shape);
    }

    [Fact]
    public void Comparison_ignores_case_and_surrounding_whitespace()
        => Assert.True(IdentityAgreement.Compare("LastName", "  mustermann ", "Mustermann")!.Agreed);

    // Nothing to compare is NOT a disagreement: the model reading nothing is a recall gap, counted as FILL elsewhere.
    // Returning null keeps it out of the oracle's denominator instead of scoring it as a miss.
    [Theory]
    [InlineData(null, "Z000000005")]
    [InlineData("", "Z000000005")]
    [InlineData("   ", "Z000000005")]
    [InlineData("Z000000005", null)]
    [InlineData("Z000000005", "")]
    public void A_missing_side_yields_no_sample(string? model, string? record)
        => Assert.Null(IdentityAgreement.Compare("KVNR", model, record));

    // ---- structural descriptors: classes and positions, never characters ----

    // Same length, one position differs. 'S' is a letter, '5' a digit, at index 9.
    [Fact]
    public void A_single_substitution_reports_its_class_and_position()
    {
        var r = IdentityAgreement.Compare("KVNR", "Z00000000S", SyntheticKvnr)!;
        Assert.False(r.Agreed);
        Assert.Equal("a->d@9", r.Shape);
    }

    // The classic OCR failure this oracle exists to surface: a leading letter read as a look-alike digit.
    [Fact]
    public void A_leading_letter_read_as_a_digit_is_described_as_such()
    {
        var r = IdentityAgreement.Compare("KVNR", "0000000005", SyntheticKvnr)!;
        Assert.False(r.Agreed);
        Assert.Equal("d->a@0", r.Shape);
    }

    [Fact]
    public void A_length_difference_is_reported_as_a_length_change()
    {
        var r = IdentityAgreement.Compare("KVNR", "Z00000005", SyntheticKvnr)!;   // 9 chars vs 10
        Assert.False(r.Agreed);
        Assert.Equal("len 9->10", r.Shape);
    }

    // Up to three positions are listed individually; beyond that only the count, so a wholesale mismatch cannot turn
    // into a long positional trace of the value.
    [Fact]
    public void Many_differing_positions_collapse_to_a_count()
    {
        var r = IdentityAgreement.Compare("KVNR", "ZAAAAAAAAA", SyntheticKvnr)!;
        Assert.Equal("9 positions", r.Shape);
    }

    [Fact]
    public void Exactly_three_differing_positions_are_still_listed()
    {
        var r = IdentityAgreement.Compare("KVNR", "ZAA0000005", SyntheticKvnr)!;  // indices 1,2 differ
        Assert.Equal("a->d@1,a->d@2", r.Shape);
    }

    // The whole point of the descriptor: it must not leak the value it describes.
    [Fact]
    public void The_shape_never_contains_the_values()
    {
        var r = IdentityAgreement.Compare("LastName", "Mustermann", "Beispielman")!;
        Assert.False(r.Agreed);
        Assert.DoesNotContain("Mustermann", r.Shape!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Beispiel", r.Shape!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_identity_field_set_is_exactly_the_four_the_form_overwrites()
    {
        Assert.True(IdentityAgreement.IsIdentityField("KVNR"));
        Assert.True(IdentityAgreement.IsIdentityField("birthdate"));   // case-insensitive
        Assert.False(IdentityAgreement.IsIdentityField("DocumentDate"));
        Assert.Equal(4, IdentityAgreement.Fields.Count);
    }
}
