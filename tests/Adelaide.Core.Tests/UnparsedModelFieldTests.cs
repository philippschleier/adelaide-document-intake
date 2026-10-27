using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// N1: an unreadable model response must be distinguishable from a genuinely bad document.
//
// The parsers substitute 0.5 when a required numeric field is absent or the wrong JSON type. 0.5 is below the 0.60
// legibility floor and below both 0.80 gates, so the document routes to review either way — which is correct, and is
// exactly why the failure was invisible: it looked identical to a quality collapse. Across a model migration those two
// need opposite responses (roll back vs. fix a field name), so the gate has to say which one happened.
public class UnparsedModelFieldTests
{
    private static readonly PipelineOptions Opt = new(MinQuality: 0.60, MinConfidence: 0.80);

    private static PatientMatch SingleMatch() =>
        new(MatchOutcome.Single, new PatientRef(1, "Z000000005", "Test", "T", new DateOnly(1956, 5, 29), "Active"), "kvnr");

    private static ProcessingResult Run(Transcript t, ExtractedData data) =>
        IntakePipeline.Decide(new DocumentUnit("u1", 1, 1, t), t, data, SingleMatch(), Opt);

    private static Transcript T(double quality, params string[] unparsed) =>
        new(GateFixtures.Transcript, 1, quality, [], null, null, null, null, null, unparsed.Length > 0 ? unparsed : null);

    private static ExtractedData D(IReadOnlyList<string>? unparsed = null) =>
        new([new ExtractedField("KVNR", "Z000000005", 0.95, "barcode")], UnparsedFields: unparsed);

    // ---- the positive case: a field we could not read is named as such ----

    [Fact]
    public void Unparsed_legibility_yields_the_unparsed_code()
    {
        var r = Run(T(0.5, "legibility"), D());
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(ReasonCodes.ModelFieldUnparsed, r.ReasonCodes!);
        Assert.Contains(r.Reasons, x => x.Contains("legibility"));   // the reason names the field, so the log is actionable
    }

    [Fact]
    public void Unparsed_classify_confidence_yields_the_unparsed_code()
    {
        var r = Run(T(0.95), D(["classify.confidence"]));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(ReasonCodes.ModelFieldUnparsed, r.ReasonCodes!);
    }

    // ---- the discrimination that is the whole point: a REAL 0.5 is not an unparsed field ----

    [Fact]
    public void A_genuine_low_legibility_of_0_5_is_low_quality_NOT_unparsed()
    {
        var r = Run(T(0.5), D());                                     // same number, no defect recorded
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.DoesNotContain(ReasonCodes.ModelFieldUnparsed, r.ReasonCodes!);
        Assert.Contains("low_quality", r.ReasonCodes!);
    }

    // An unreadable legibility must NOT also be reported as low quality: the 0.5 is our fallback, not a measurement of
    // the scan, and double-flagging would keep the quality metric contaminated by parser failures post-migration.
    [Fact]
    public void Unparsed_legibility_does_not_also_claim_low_quality()
    {
        var r = Run(T(0.5, "legibility"), D());
        Assert.DoesNotContain("low_quality", r.ReasonCodes!);
    }

    // A confidence we could not read still leaves the scan quality claim intact — only the confidence is in doubt.
    [Fact]
    public void Unparsed_confidence_on_a_poor_scan_still_reports_low_quality()
    {
        var r = Run(T(0.4), D(["classify.confidence"]));
        Assert.Contains(ReasonCodes.ModelFieldUnparsed, r.ReasonCodes!);
        Assert.Contains("low_quality", r.ReasonCodes!);
    }

    [Fact]
    public void Clean_document_records_no_unparsed_code()
    {
        var r = Run(T(0.95), D());
        Assert.Equal(Disposition.AutoFile, r.Disposition);
        Assert.DoesNotContain(ReasonCodes.ModelFieldUnparsed, r.ReasonCodes ?? []);
    }

    // Defects from BOTH stages surface together — transcription and extraction fail independently.
    [Fact]
    public void Defects_from_transcript_and_extraction_are_merged()
    {
        var r = Run(T(0.5, "legibility"), D(["classify.confidence"]));
        Assert.Contains(r.Reasons, x => x.Contains("legibility") && x.Contains("classify.confidence"));
    }

    // The code must be the registry constant, not a look-alike literal — the registry is what the ledger is audited against.
    [Fact]
    public void The_code_is_registered()
        => Assert.Contains(ReasonCodes.ModelFieldUnparsed, ReasonCodes.Known);
}
