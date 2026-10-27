using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

public class ReviewGateTests
{
    private static readonly PipelineOptions Opt = new(MinQuality: 0.60, MinConfidence: 0.80);

    private static PatientMatch SingleMatch() =>
        new(MatchOutcome.Single, new PatientRef(1, "Z000000005", "Test", "T", new DateOnly(1956, 5, 29), "Active"), "kvnr");

    private static ExtractedData D(params ExtractedField[] f) => new(f);

    private static ProcessingResult Run(double quality, ExtractedData data, PatientMatch match)
    {
        var t = new Transcript(GateFixtures.Transcript, 1, quality, Array.Empty<DecodedBarcode>());
        return IntakePipeline.Decide(new DocumentUnit("u1", 1, 1, t), t, data, match, Opt);
    }

    [Fact]
    public void Clean_document_auto_files()
    {
        var r = Run(0.95, D(new ExtractedField("KVNR", "Z000000005", 0.95, "barcode")), SingleMatch());
        Assert.Equal(Disposition.AutoFile, r.Disposition);
        Assert.Empty(r.Reasons);
    }

    [Fact]
    public void Low_quality_routes_to_review()
    {
        var r = Run(0.40, D(new ExtractedField("KVNR", "Z000000005", 0.95, "barcode")), SingleMatch());
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("quality"));
    }

    [Fact]
    public void Bad_kvnr_check_digit_routes_to_review()
    {
        var r = Run(0.95, D(new ExtractedField("KVNR", "Z000000004", 0.95, "vision")), SingleMatch());
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("KVNR"));
    }

    [Fact]
    public void No_patient_match_routes_to_review()
    {
        var r = Run(0.95, D(new ExtractedField("LastName", "X", 0.95, "vision")), new PatientMatch(MatchOutcome.None, null, "name+dob"));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("kein Patiententreffer") && x.Contains("keine Versichertennummer"));
    }

    [Fact]
    public void Unmatched_valid_kvnr_says_so_in_the_reason()
    {
        // a valid KVNR that matched nobody is a different reviewer task than a blank field — the reason must say which
        var r = Run(0.95, D(new ExtractedField("KVNR", "Z000000005", 0.99, "barcode")), new PatientMatch(MatchOutcome.None, null, "none"));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("ohne Treffer im Patientenstamm"));
    }

    [Fact]
    public void Ambiguous_match_routes_to_review()
    {
        var r = Run(0.95, D(new ExtractedField("LastName", "X", 0.95, "vision")), new PatientMatch(MatchOutcome.Multiple, null, "name+dob"));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("mehrere Patienten"));
    }

    [Fact]
    public void Low_confidence_routes_to_review()
    {
        var r = Run(0.95, D(new ExtractedField("KVNR", "Z000000005", 0.50, "vision")), SingleMatch());
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("confidence"));
    }

    private static ExtractedField[] GenehmigungBase(string outcome) => new[]
    {
        new ExtractedField("ContentType", "Genehmigung", 0.95, "vision"),
        new ExtractedField("KVNR", "Z000000005", 0.95, "barcode"),
        new ExtractedField("ApprovalOutcome", outcome, 0.95, "vision"),
    };

    [Fact]
    public void Approved_genehmigung_without_number_routes_to_review()
    {
        // "Wie verordnet" (genehmigt) must carry a Genehmigungsnummer — a blank one is a human task.
        var r = Run(0.95, D(GenehmigungBase("Wie verordnet")), SingleMatch());
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("Genehmigungsnummer"));
    }

    [Fact]
    public void Rejected_genehmigung_without_number_auto_files()
    {
        // An Ablehnung legitimately has NO Genehmigungsnummer — it must NOT be held for review over a blank number.
        var r = Run(0.95, D(GenehmigungBase("Abgelehnt")), SingleMatch());
        Assert.Equal(Disposition.AutoFile, r.Disposition);
        Assert.Empty(r.Reasons);
    }

    [Fact]
    public void Approved_genehmigung_with_number_auto_files()
    {
        var r = Run(0.95, D(
            new ExtractedField("ContentType", "Genehmigung", 0.95, "vision"),
            new ExtractedField("KVNR", "Z000000005", 0.95, "barcode"),
            new ExtractedField("ApprovalOutcome", "Wie verordnet", 0.95, "vision"),
            new ExtractedField("ApprovalNumber", "GN-2026-001", 0.95, "vision")), SingleMatch());
        Assert.Equal(Disposition.AutoFile, r.Disposition);
        Assert.Empty(r.Reasons);
    }

    private static ExtractedField[] VerordnungComplete() => new[]
    {
        new ExtractedField("ContentType", "Verordnung", 0.97, "vision"),
        new ExtractedField("KVNR", "Z000000005", 0.97, "barcode"),
        new ExtractedField("DocumentDate", "2026-05-01", 0.95, "vision"),
        new ExtractedField("PrescriptionOrder", "Folgeverordnung", 0.95, "barcode"),
    };

    [Fact]
    public void Complete_allowlisted_verordnung_auto_files()
    {
        // Sanity: the fixes don't over-block — a complete, allowlisted Verordnung still auto-files.
        var r = Run(0.95, D(VerordnungComplete()), SingleMatch());
        Assert.Equal(Disposition.AutoFile, r.Disposition);
        Assert.Empty(r.Reasons);
    }

    [Fact]
    public void Verordnung_missing_required_field_routes_to_review_at_the_gate()
    {
        // Issue 1: the GATE (not just the submit form) blocks auto-file on a missing REQUIRED field. A Verordnung whose
        // Verordnungsfolge is blank — even with a clean barcode KVNR and a confident classification — routes to review.
        var r = Run(0.95, D(
            new ExtractedField("ContentType", "Verordnung", 0.97, "vision"),
            new ExtractedField("KVNR", "Z000000005", 0.97, "barcode"),
            new ExtractedField("DocumentDate", "2026-05-01", 0.95, "vision")), SingleMatch()); // no Verordnungsfolge
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("Verordnungsfolge"));
    }

    [Fact]
    public void Non_allowlisted_type_routes_to_review_even_at_high_confidence()
    {
        // Issue 2: the auto-file allowlist DOMINATES confidence — a non-allowlisted type (the confusable-misclassification
        // cases) routes to review no matter how sure the model is. Deny-by-default; the allowlist is a gate, not a hint.
        var r = Run(0.95, D(
            new ExtractedField("ContentType", "Medikamentenplan", 0.99, "vision"),
            new ExtractedField("KVNR", "Z000000005", 0.99, "barcode"),
            new ExtractedField("DocumentDate", "2026-05-01", 0.99, "vision")), SingleMatch());
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(r.Reasons, x => x.Contains("automatische Ablage freigegeben"));
    }
}
