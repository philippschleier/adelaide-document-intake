using Adelaide.Core;
using Adelaide.SharePoint;
using Xunit;

namespace Adelaide.SharePoint.Tests;

// BuildBusinessSpec / BuildBusinessFields: a business filing carries the business FilingTarget + minimal metadata and
// NEVER a patient lookup (design §7 holding library). The medical BuildFields is proven unchanged (still sets the
// PatientLookupId) so the business addition can't have regressed the patient path.
public class BusinessFilingSpecTests
{
    private static readonly FilingTarget Business = new("site-b", "Business – Eingang", "Document", RequiresPatient: false);
    private static DocumentUnit Unit() => new("doc-b1", 1, 1, new Transcript("Rechnung Nr. 4711 vom 01.05.2026", 1, 1.0, Array.Empty<DecodedBarcode>()));
    private static IncomingDocument Doc() => new("doc-b1", "/inbox/doc-b1.pdf", SourceChannel.Api, "invoice.pdf");

    [Fact]
    public void BuildBusinessSpec_targets_the_business_library_and_content_type()
    {
        var spec = SharePointSink.BuildBusinessSpec(Doc(), Unit(), Business, "Document", "2026-05-01");
        Assert.Equal(Business, spec.Target);                     // the destination round-trips onto the spec
        Assert.Equal("Document", spec.ContentType);
        Assert.Equal("/inbox/doc-b1.pdf", spec.PdfPath);
        Assert.Contains("Rechnung", spec.Volltext);              // the transcript flows to Volltext, exactly as the medical path
    }

    [Fact]
    public void BuildBusinessSpec_never_sets_a_patient_lookup()
    {
        var spec = SharePointSink.BuildBusinessSpec(Doc(), Unit(), Business, "Document", "2026-05-01");
        Assert.False(spec.Fields.ContainsKey("PatientLookupId"));
    }

    [Fact]
    public void BuildBusinessFields_sets_DocumentDate_iso_when_present_and_no_patient()
    {
        var f = SharePointSink.BuildBusinessFields("2026-05-01");
        Assert.Equal("2026-05-01T00:00:00Z", f["DocumentDate"] as string); // day-first exact parse → ISO, like the medical fields
        Assert.False(f.ContainsKey("PatientLookupId"));
    }

    [Fact]
    public void BuildBusinessFields_omits_DocumentDate_when_absent_or_blank()
    {
        Assert.Empty(SharePointSink.BuildBusinessFields(null));
        Assert.Empty(SharePointSink.BuildBusinessFields("   "));
    }

    // Zielbereich: the reviewer's functional-area label goes into the (best-effort) SharePoint column via the Fields dict —
    // present when given, absent when null/blank, and NEVER alongside a patient lookup.
    [Fact]
    public void BuildBusinessFields_sets_Zielbereich_when_present()
    {
        var f = SharePointSink.BuildBusinessFields("2026-05-01", "Finance");
        Assert.Equal("Finance", f["Zielbereich"] as string);
        Assert.Equal("2026-05-01T00:00:00Z", f["DocumentDate"] as string);
        Assert.False(f.ContainsKey("PatientLookupId"));
    }

    [Fact]
    public void BuildBusinessFields_omits_Zielbereich_when_absent_or_blank()
    {
        Assert.False(SharePointSink.BuildBusinessFields("2026-05-01", null).ContainsKey("Zielbereich"));
        Assert.False(SharePointSink.BuildBusinessFields("2026-05-01", "  ").ContainsKey("Zielbereich"));
    }

    [Fact]
    public void BuildBusinessSpec_carries_Zielbereich_into_the_fields()
    {
        var spec = SharePointSink.BuildBusinessSpec(Doc(), Unit(), Business, "Document", "2026-05-01", "HR");
        Assert.Equal("HR", spec.Fields["Zielbereich"] as string);
        Assert.False(spec.Fields.ContainsKey("PatientLookupId"));
    }

    [Fact]
    public void Business_filename_carries_date_type_and_docId_but_no_kvnr()
    {
        var spec = SharePointSink.BuildBusinessSpec(Doc(), Unit(), Business, "Document", "2026-05-01");
        Assert.StartsWith("2026-05-01_Document_", spec.FileName);
        Assert.Contains("doc-b1", spec.FileName);                 // the FULL docId keeps the path per-document (idempotent upsert)
        Assert.EndsWith(".pdf", spec.FileName);
    }

    // Regression: the MEDICAL BuildFields still attaches the patient lookup — the business addition must not have touched it.
    [Fact]
    public void Medical_BuildFields_still_sets_PatientLookupId()
    {
        var data = new ExtractedData(new[] { new ExtractedField("ContentType", "Verordnung", 1.0, "review") });
        var patient = new PatientRef(99, "Z000000005", "Test", "T", new DateOnly(1956, 5, 29), "Active");
        var result = new ProcessingResult("d", Disposition.AutoFile, Array.Empty<string>(), data,
            new PatientMatch(MatchOutcome.Single, patient, "kvnr"), 1.0);
        var fields = SharePointSink.BuildFields(result, "Verordnung");
        Assert.Equal(99, fields["PatientLookupId"]);
    }
}
