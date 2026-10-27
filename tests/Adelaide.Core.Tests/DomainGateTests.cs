using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// N2: domain (medical vs business) is the FIRST decision, with its own vocabulary — see DocumentDomain.
//
// The gate's job on the business lane is to stop applying medical logic to a document that was never medical. Before
// this, a business document was classified over the medical catalog, failed the patient match, failed the allowlist,
// and arrived in review carrying reasons (no_match_no_kvnr, unclassified_type) that described a medical document it
// wasn't. Those spurious reasons are what these tests pin against.
public class DomainGateTests
{
    private static readonly PipelineOptions Opt = new(MinQuality: 0.60, MinConfidence: 0.80);

    private static ProcessingResult Run(params ExtractedField[] fields)
    {
        var t = new Transcript(GateFixtures.Transcript, 1, 0.95, []);
        // No patient match — the realistic business case, and the one that used to manufacture medical reasons.
        var match = new PatientMatch(MatchOutcome.None, null, "none");
        return IntakePipeline.Decide(new DocumentUnit("u1", 1, 1, t), t, new ExtractedData(fields), match, Opt);
    }

    private static ExtractedField Domain(string d, double conf = 0.93) => new("Domain", d, conf, "vision");

    [Fact]
    public void Business_routes_to_review_with_the_business_code()
    {
        var r = Run(Domain(DocumentDomain.Business));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(ReasonCodes.BusinessDomain, r.ReasonCodes!);
    }

    // The point of the short-circuit: a business document must NOT accumulate medical failures. Those reasons would
    // tell the reviewer to find a patient for a supplier invoice, and would pollute the medical gate's metrics.
    [Fact]
    public void Business_does_not_collect_medical_failure_reasons()
    {
        var r = Run(Domain(DocumentDomain.Business));
        Assert.DoesNotContain("no_match_no_kvnr", r.ReasonCodes!);
        Assert.DoesNotContain("unclassified_type", r.ReasonCodes!);
        Assert.DoesNotContain("not_allowlisted", r.ReasonCodes!);
        Assert.Single(r.ReasonCodes!);
    }

    // Business can never auto-file: the reviewer still owns the Zielbereich the filing target depends on.
    [Fact]
    public void Business_never_auto_files_even_with_a_confident_proposal()
    {
        var r = Run(Domain(DocumentDomain.Business, 0.99));
        Assert.Equal(Disposition.Review, r.Disposition);
    }

    // The medical lane is untouched by the new step — a medical document still gets the full medical gate.
    [Fact]
    public void Medical_still_runs_the_medical_gate()
    {
        var r = Run(Domain(DocumentDomain.Medical), new ExtractedField("ContentType", "Verordnung", 0.95, "vision"));
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains("no_match_no_kvnr", r.ReasonCodes!);       // medical checks DID run
        Assert.DoesNotContain(ReasonCodes.BusinessDomain, r.ReasonCodes!);
    }

    // A document from before the domain step has no Domain field at all; it must behave exactly as it did.
    [Fact]
    public void A_document_without_a_domain_field_takes_the_medical_path()
    {
        var r = Run(new ExtractedField("ContentType", "Verordnung", 0.95, "vision"));
        Assert.Contains("no_match_no_kvnr", r.ReasonCodes!);
        Assert.DoesNotContain(ReasonCodes.BusinessDomain, r.ReasonCodes!);
    }

    // ---- the vocabulary itself ----

    [Fact]
    public void Business_is_recognised_case_insensitively()
        => Assert.True(DocumentDomain.IsBusiness("business"));

    [Fact]
    public void An_unknown_domain_is_not_business_and_not_known()
    {
        Assert.False(DocumentDomain.IsBusiness("Finanzen"));
        Assert.False(DocumentDomain.IsKnown("Finanzen"));
        Assert.False(DocumentDomain.IsKnown(null));
    }

    // Triagedokument must NOT have leaked into the medical catalog — the business lane is expressed as a domain,
    // never as a content type, or business documents inherit the medical field schema and allowlist.
    [Fact]
    public void Triagedokument_is_not_a_medical_content_type()
        => Assert.Null(DocumentCatalog.Find("Triagedokument"));

    [Fact]
    public void The_business_code_is_registered()
        => Assert.Contains(ReasonCodes.BusinessDomain, ReasonCodes.Known);
}
