using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// FieldVocabulary is the CLOSED set of field-name tokens allowed into anything durable — the append-only ledger's
// correctedFields/fieldSignals/fieldProvenance, and the measurement corpus's corpus_fields.FieldName. It lives in
// Adelaide.Core precisely so the EXTRACTOR can enforce it: the bare fallback type has no per-type schema, and the
// intake capture write persists extracted names into corpus_fields before any review, so a review-time filter is
// too late for that copy.
public class FieldVocabularyTests
{
    [Theory]
    [InlineData("Versichertennummer Anna Müller")] // free prose carrying a patient name — fails the shape floor
    [InlineData("VersichertennummerAnnaMueller")]  // identifier-shaped: ONLY the closed vocabulary refuses this
    [InlineData("PatientinMueller")]
    public void Model_authored_names_are_refused(string name) => Assert.False(FieldVocabulary.IsSafe(name));

    [Theory]
    [InlineData("KVNR")]           // identity block
    [InlineData("LastName")]
    [InlineData("BirthDate")]
    [InlineData("DocumentDate")]   // server-added: the fifth fixed identity input
    [InlineData("ContentType")]    // server-added discriminators
    [InlineData("Domain")]
    [InlineData("OriginalContentType")]
    [InlineData("Leistungen")]     // from the BFB barcode
    public void Identity_and_server_added_names_are_allowed(string name) => Assert.True(FieldVocabulary.IsSafe(name));

    [Fact]
    public void Every_catalog_column_of_every_type_is_allowed()
    {
        // The union is derived, not enumerated — so a newly added catalog column must never be silently dropped by
        // the extractor gate. This is the regression guard for the change's biggest risk: losing legitimate data.
        foreach (var t in DocumentCatalog.Types)
            foreach (var f in t.Fields)
                Assert.True(FieldVocabulary.IsSafe(f.Column),
                    $"catalog column '{f.Column}' of type '{t.ContentType}' is not ledger-safe — the extractor would drop it");
    }

    [Fact]
    public void Vocabulary_matching_is_case_insensitive()
    {
        Assert.True(FieldVocabulary.IsSafe("kvnr"));
        Assert.True(FieldVocabulary.IsSafe("documentdate"));
    }

    [Fact]
    public void Shape_floor_refuses_oversized_and_non_identifier_names()
    {
        Assert.False(FieldVocabulary.ValidShape(new string('a', 65))); // > 64
        Assert.False(FieldVocabulary.ValidShape("1LeadingDigit"));
        Assert.False(FieldVocabulary.ValidShape("has space"));
        Assert.False(FieldVocabulary.ValidShape(""));
        Assert.True(FieldVocabulary.ValidShape("Valid_Name9"));
    }
}
