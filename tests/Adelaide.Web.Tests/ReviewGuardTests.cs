using Adelaide.Core;
using Xunit;

namespace Adelaide.Web.Tests;

// The conditional patient guard (POST /api/review, pure core): a MEDICAL submission requires a patient; a BUSINESS one
// does NOT, and cannot supersede. "Business" is the explicit IsBusiness flag — never inferred from an empty patient id.
public class ReviewGuardTests
{
    private static ReviewSubmission Sub(int patientId, bool isBusiness = false, string contentType = "Verordnung", string? supersedeTarget = null, string? zielbereich = "HR")
        => new("doc1", contentType, patientId, new Dictionary<string, string?>(), IsBusiness: isBusiness, SupersedeTarget: supersedeTarget, Zielbereich: zielbereich);

    [Fact]
    public void Medical_without_a_patient_requires_one()
        => Assert.Equal(ReviewGuard.Result.PatientRequired, ReviewGuard.Validate(Sub(0)));

    // ---- C6: the submission's ledger inputs are gated (verification finding: contentType and Fields KEYS used to
    // flow verbatim into the append-only Filing/Filed/Reviewed Details — an un-erasable free-text channel). ----

    [Fact]
    public void Medical_type_must_be_a_catalog_type()
        => Assert.Equal(ReviewGuard.Result.UnknownContentType, ReviewGuard.Validate(Sub(42, contentType: "Freitext mit Patientenname Müller")));

    [Fact]
    public void Medical_bare_fallback_type_is_refused()
        => Assert.Equal(ReviewGuard.Result.UnknownContentType, ReviewGuard.Validate(Sub(42, contentType: DocumentCatalog.Fallback)));

    [Fact]
    public void Business_type_is_not_catalog_gated()
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(Sub(0, isBusiness: true, contentType: "Rechnung Steuerberater")));

    [Fact]
    public void Field_keys_must_be_identifier_shaped()
    {
        var bad = new ReviewSubmission("doc1", "Verordnung", 42,
            new Dictionary<string, string?> { ["Patient Müller, Erika"] = "x" }, IsBusiness: false);
        Assert.Equal(ReviewGuard.Result.InvalidFieldName, ReviewGuard.Validate(bad));

        var good = new ReviewSubmission("doc1", "Verordnung", 42,
            new Dictionary<string, string?> { ["DocumentDate"] = "2026-05-01", ["KVNR"] = "A123456789", ["LastName"] = "Wert egal", ["ValidFrom"] = null }, IsBusiness: false);
        Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(good)); // values are never ledger inputs — only keys are gated
    }

    // ---- Second verification round: the vocabulary is the UNION across types + everything the extractor emits —
    // the client submits its whole seeded values map, so per-submitted-type gating 400'd real reviewer flows. ----

    [Fact]
    public void Type_correction_keeps_the_original_types_extracted_keys_valid()
    {
        // Classified Verordnung (seeded ValidFrom/ValidUntil/PrescriptionOrder), reviewer corrects to Kassenkorrespondenz:
        // the stale Verordnung keys ride along in the submission and MUST NOT 400.
        var sub = new ReviewSubmission("doc1", "Kassenkorrespondenz", 42, new Dictionary<string, string?>
        {
            ["DocumentDate"] = "2026-05-01", ["ValidFrom"] = "2026-05-01", ["ValidUntil"] = "2026-06-30", ["PrescriptionOrder"] = "Erstverordnung",
        }, IsBusiness: false);
        Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(sub));
    }

    [Fact]
    public void Leistungsnachweis_review_with_a_document_date_is_valid()
        // The form's fixed identity block includes DocumentDate; LNW is the one type without that column.
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(new ReviewSubmission("doc1", "Leistungsnachweis", 42,
            new Dictionary<string, string?> { ["DocumentDate"] = "2026-05-01", ["ServiceYear"] = "2026" }, IsBusiness: false)));

    [Fact]
    public void Extractor_provenance_fields_are_valid()
        // OriginalContentType (classify self-check overturn) and Leistungen (BFB barcode) are server-added and seeded
        // into every submission by the client — they must pass.
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(new ReviewSubmission("doc1", "Verordnung", 42,
            new Dictionary<string, string?> { ["OriginalContentType"] = "Genehmigung", ["Leistungen"] = "Med. Gabe 2x tgl.", ["Domain"] = "Medical" }, IsBusiness: false)));

    [Fact]
    public void Identifier_shaped_pseudo_name_is_still_refused_on_the_medical_lane()
        => Assert.Equal(ReviewGuard.Result.InvalidFieldName, ReviewGuard.Validate(new ReviewSubmission("doc1", "Verordnung", 42,
            new Dictionary<string, string?> { ["MuellerErika"] = "x" }, IsBusiness: false)));

    [Fact]
    public void Prune_drops_stray_provenance_keys_and_keeps_the_vocabulary()
    {
        var sub = new ReviewSubmission("doc1", "Verordnung", 42, new Dictionary<string, string?>
        {
            ["DocumentDate"] = "2026-05-01",           // vocabulary → kept
            ["Gültig ab"] = "01.05.2026",              // pre-fix hallucinated label (charset-invalid) → dropped, not 400'd
            ["SomeHallucination"] = "x",               // identifier-shaped but non-vocabulary → dropped
        }, IsBusiness: false, FieldProvenance: new Dictionary<string, string> { ["DocumentDate"] = "typed", ["SomeHallucination"] = "prefill" });

        var pruned = ReviewGuard.Prune(sub, out var dropped);
        // The counter spans BOTH client-controlled key maps: 2 field keys + the 1 provenance key that named one of
        // them. It is a log figure, deliberately not a per-map breakdown.
        Assert.Equal(3, dropped);
        Assert.Equal(new[] { "DocumentDate" }, pruned.Fields.Keys.ToArray());
        Assert.Equal(new[] { "DocumentDate" }, pruned.FieldProvenance!.Keys.ToArray());
        Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(pruned)); // the pruned submission sails through

        // Business lane: the SAME two gates now. It used to be charset-only — Prune's predicate exempted
        // isBusiness and Validate returned from the business branch before any vocabulary check — so an
        // identifier-shaped client-chosen key reached the append-only Reviewed event's fieldProvenance[].field.
        var biz = new ReviewSubmission("doc1", "Rechnung", 0, new Dictionary<string, string?>
        { ["DocumentDate"] = "2026-05-01", ["Freitext Feld"] = "x", ["AnythingIdentifier"] = "y" }, IsBusiness: true, Zielbereich: "HR");
        var bizPruned = ReviewGuard.Prune(biz, out var bizDropped);
        Assert.Equal(2, bizDropped);                                     // charset-invalid AND non-vocabulary go
        Assert.DoesNotContain("AnythingIdentifier", bizPruned.Fields.Keys);
        Assert.Equal(new[] { "DocumentDate" }, bizPruned.Fields.Keys.ToArray());
        Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(bizPruned));
    }

    // ---- C6, business lane: the vocabulary gate applies there too (verification finding). Any authenticated
    // reviewer can set isBusiness, so an open lane was an open write channel into an immutable table. ----

    [Fact]
    public void Identifier_shaped_pseudo_name_is_refused_on_the_business_lane_too()
        => Assert.Equal(ReviewGuard.Result.InvalidFieldName, ReviewGuard.Validate(new ReviewSubmission("doc1", "Rechnung", 0,
            new Dictionary<string, string?> { ["MuellerErika"] = "x" }, IsBusiness: true, Zielbereich: "HR")));

    [Fact]
    public void The_business_lane_keeps_the_seeded_medical_keys_valid()
    {
        // The reviewer re-labels the DOMAIN only; the form still submits the whole values map the ORIGINAL medical
        // classification seeded. Gating the business lane must not 400 that — the same reason the medical
        // vocabulary is a union across types rather than the submitted type's columns.
        var sub = new ReviewSubmission("doc1", "Rechnung Steuerberater", 0, new Dictionary<string, string?>
        {
            ["DocumentDate"] = "2026-05-01", ["ValidFrom"] = "2026-05-01", ["KVNR"] = "Z000000005",
            ["OriginalContentType"] = "Genehmigung", ["Domain"] = "Medical",
        }, IsBusiness: true, Zielbereich: "Finance");
        Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(sub));
        ReviewGuard.Prune(sub, out var dropped);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void The_business_filing_columns_are_in_the_business_vocabulary()
    {
        // Derived from SharePointSink.BusinessFieldNames, so a Phase-3 business column cannot drift out of the gate.
        foreach (var column in Adelaide.SharePoint.SharePointSink.BusinessFieldNames)
            Assert.Contains(column, ReviewGuard.BusinessLedgerSafeFieldNames);
    }

    // ---- FieldProvenance is the SECOND client-controlled key map, and it reaches the same event ----
    // Adversarial review of the business-lane fix: Prune filtered provenance only as a SIDE EFFECT of a Fields key
    // being dropped (two early returns sat above it), and Validate never looked at it at all — so the ordinary
    // submission, with a well-formed Fields map and nothing to drop, carried provenance keys through ungated. They
    // were stopped one layer down by SanitizeProvenance's known-names filter, which is a thinner guarantee than
    // either function claimed. Both maps are gated here now, on both lanes.

    [Fact]
    public void A_provenance_key_outside_the_vocabulary_is_refused_even_when_fields_are_clean()
    {
        foreach (var (isBusiness, contentType, zielbereich) in new[] { (false, "Verordnung", (string?)null), (true, "Rechnung", "HR") })
            Assert.Equal(ReviewGuard.Result.InvalidFieldName, ReviewGuard.Validate(new ReviewSubmission("doc1", contentType,
                isBusiness ? 0 : 42, new Dictionary<string, string?> { ["DocumentDate"] = "2026-05-01" },
                IsBusiness: isBusiness, Zielbereich: zielbereich,
                FieldProvenance: new Dictionary<string, string> { ["MuellerErika"] = "typed" })));
    }

    [Fact]
    public void Prune_drops_a_stray_provenance_key_when_the_fields_map_needs_no_pruning()
    {
        var sub = new ReviewSubmission("doc1", "Verordnung", 42,
            new Dictionary<string, string?> { ["DocumentDate"] = "2026-05-01" }, IsBusiness: false,
            FieldProvenance: new Dictionary<string, string> { ["DocumentDate"] = "typed", ["MuellerErika"] = "typed" });

        var pruned = ReviewGuard.Prune(sub, out var dropped);
        Assert.Equal(1, dropped);                                       // counted, so the endpoint's WARN fires
        Assert.Equal(new[] { "DocumentDate" }, pruned.Fields.Keys.ToArray());
        Assert.Equal(new[] { "DocumentDate" }, pruned.FieldProvenance!.Keys.ToArray());
        Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(pruned));
    }

    [Fact]
    public void A_submission_with_no_fields_but_a_stray_provenance_key_is_still_pruned()
    {
        // The early return that used to sit here (`if (body.Fields is not { Count: > 0 }) return body;`) skipped the
        // provenance map entirely.
        var sub = new ReviewSubmission("doc1", "Verordnung", 42, new Dictionary<string, string?>(), IsBusiness: false,
            FieldProvenance: new Dictionary<string, string> { ["SomeHallucination"] = "prefill" });
        var pruned = ReviewGuard.Prune(sub, out var dropped);
        Assert.Equal(1, dropped);
        Assert.Empty(pruned.FieldProvenance!);
    }

    [Fact]
    public void The_business_vocabulary_is_a_superset_of_the_medical_one_and_both_are_closed()
    {
        Assert.True(ReviewGuard.LedgerSafeFieldNames.IsSubsetOf(ReviewGuard.BusinessLedgerSafeFieldNames));
        Assert.Same(ReviewGuard.LedgerSafeFieldNames, ReviewGuard.VocabularyFor(isBusiness: false));
        Assert.Same(ReviewGuard.BusinessLedgerSafeFieldNames, ReviewGuard.VocabularyFor(isBusiness: true));
    }

    [Fact]
    public void Medical_with_a_patient_is_ok()
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(Sub(42)));

    [Fact]
    public void Business_without_a_patient_is_ok()
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(Sub(0, isBusiness: true)));

    [Fact]
    public void Business_is_ok_even_if_a_patient_id_slips_through()
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(Sub(42, isBusiness: true)));

    [Fact]
    public void Missing_contentType_is_rejected_in_both_lanes()
    {
        Assert.Equal(ReviewGuard.Result.ContentTypeRequired, ReviewGuard.Validate(Sub(42, contentType: "")));
        Assert.Equal(ReviewGuard.Result.ContentTypeRequired, ReviewGuard.Validate(Sub(0, isBusiness: true, contentType: "  ")));
    }

    [Fact]
    public void Business_cannot_supersede()
        => Assert.Equal(ReviewGuard.Result.BusinessCannotSupersede, ReviewGuard.Validate(Sub(0, isBusiness: true, supersedeTarget: "other")));

    // The medical lane permits a supersede request at THIS gate; SupersedeGuard does the deeper (duplicateOf/state) checks.
    [Fact]
    public void Medical_supersede_passes_this_guard()
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(Sub(42, supersedeTarget: "other")));

    // ---- Zielbereich: REQUIRED for a business submission, validated against the non-patient subset {HR, Finance, unklar} ----
    [Fact]
    public void Business_without_a_zielbereich_is_rejected()
        => Assert.Equal(ReviewGuard.Result.ZielbereichRequired, ReviewGuard.Validate(Sub(0, isBusiness: true, zielbereich: null)));

    [Fact]
    public void Business_with_a_blank_zielbereich_is_rejected()
        => Assert.Equal(ReviewGuard.Result.ZielbereichRequired, ReviewGuard.Validate(Sub(0, isBusiness: true, zielbereich: "")));

    // "Patient" is in the SharePoint column vocabulary (kept for Phase-2 reuse) but NOT offered by the non-patient action;
    // the match is case-sensitive (the stored value must equal the column choice exactly).
    [Theory]
    [InlineData("Patient")]
    [InlineData("Marketing")]
    [InlineData("hr")]
    public void Business_with_an_out_of_set_zielbereich_is_rejected(string z)
        => Assert.Equal(ReviewGuard.Result.ZielbereichRequired, ReviewGuard.Validate(Sub(0, isBusiness: true, zielbereich: z)));

    [Theory]
    [InlineData("HR")]
    [InlineData("Finance")]
    [InlineData("unklar")]
    public void Business_with_an_allowed_zielbereich_is_ok(string z)
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(Sub(0, isBusiness: true, zielbereich: z)));

    // Precedence: a business supersede is refused BEFORE the Zielbereich check (locks the guard order — an invalid
    // combination reports the structural error, not the missing label).
    [Fact]
    public void Business_supersede_is_rejected_before_the_zielbereich_check()
        => Assert.Equal(ReviewGuard.Result.BusinessCannotSupersede, ReviewGuard.Validate(Sub(0, isBusiness: true, supersedeTarget: "other", zielbereich: null)));

    // The MEDICAL lane ignores Zielbereich entirely (it is a business-only label) — a stray value never affects it.
    [Fact]
    public void Medical_ignores_zielbereich()
        => Assert.Equal(ReviewGuard.Result.Ok, ReviewGuard.Validate(Sub(42, zielbereich: "Finance")));
}
