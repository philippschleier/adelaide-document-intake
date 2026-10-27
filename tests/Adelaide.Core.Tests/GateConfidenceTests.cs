using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

/// <summary>The redesigned gate: auto-file keys on identity + classification confidence, not on metadata fields.</summary>
public class GateConfidenceTests
{
    private static readonly PipelineOptions Opt = new();

    private static PatientMatch Single() =>
        new(MatchOutcome.Single, new PatientRef(1, "Z000000005", "Mustermann", "M", new DateOnly(1950, 1, 1), "Active"), "kvnr");

    private static ProcessingResult Run(params ExtractedField[] fields)
    {
        var t = new Transcript(GateFixtures.Transcript, 1, 0.95, Array.Empty<DecodedBarcode>());
        return IntakePipeline.Decide(new DocumentUnit("u", 1, 1, t), t, new ExtractedData(fields), Single(), Opt);
    }

    [Fact]
    public void Low_confidence_metadata_field_does_not_block_autofile()
    {
        // Identity solid (content type + barcode KVNR) and the REQUIRED fields present, but a non-required metadata date
        // is shaky → still auto-files (low-confidence OPTIONAL metadata is flagged for correction, not reviewed wholesale).
        var r = Run(
            new ExtractedField("ContentType", "Verordnung", 0.98, "vision"),
            new ExtractedField("KVNR", "Z000000005", 0.99, "barcode"),
            new ExtractedField("DocumentDate", "2026-04-01", 0.95, "vision"),
            new ExtractedField("PrescriptionOrder", "Folgeverordnung", 0.95, "barcode"),
            new ExtractedField("ValidFrom", "2026-04-01", 0.50, "vision"));
        Assert.Equal(Disposition.AutoFile, r.Disposition);
    }

    [Fact]
    public void Self_corrected_classification_routes_to_review_regardless_of_confidence()
    {
        // The "never auto-file the self-check's own answer" guarantee must be structural (keyed on the source),
        // not a numeric coincidence between the 0.75 cap and a tunable MinConfidence — pinned here at high confidence.
        var r = Run(
            new ExtractedField("ContentType", "Kassenkorrespondenz", 0.95, "vision/self-corrected"),
            new ExtractedField("KVNR", "Z000000005", 0.99, "barcode"));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("self-check"));
    }

    [Fact]
    public void Unverified_classification_routes_to_review()
    {
        var r = Run(
            new ExtractedField("ContentType", "Verordnung", 0.95, "vision/unverified"),
            new ExtractedField("KVNR", "Z000000005", 0.99, "barcode"));
        Assert.Equal(Disposition.Review, r.Disposition);
    }

    [Fact]
    public void Fallback_document_type_routes_to_review_even_at_high_confidence()
    {
        // "I'm 0.9 sure NONE of the types fit" must not sail through the gate — the catalog promises the bare
        // fallback type is always routed to review.
        var r = Run(
            new ExtractedField("ContentType", "Document", 0.95, "vision"),
            new ExtractedField("KVNR", "Z000000005", 0.99, "barcode"));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("unclassified"));
    }

    [Fact]
    public void Low_confidence_identity_blocks_autofile()
    {
        // Shaky identity (KVNR read poorly) → review, even though the metadata is confident.
        var r = Run(
            new ExtractedField("ContentType", "Verordnung", 0.98, "vision"),
            new ExtractedField("KVNR", "Z000000005", 0.55, "vision"),
            new ExtractedField("ValidFrom", "2026-04-01", 0.99, "vision"));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("identity"));
    }
}
