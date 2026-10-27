using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// N4: an empty transcript is its own failure, and legibility does not catch it.
//
// The canonical case is document 117203e499e9: SIX pages, self-reported legibility 0.9, and a transcript that trims to
// zero characters. It cleared the 0.60 floor and reached a human only because classification happened to fail on empty
// text — with a luckier type guess it would have auto-filed an empty document. Self-reported scores never clear
// anything; the text itself is the evidence.
//
// Threshold is measured, not assumed (368 recoverable transcripts, 2026-08-05): exactly one document had zero
// characters, nothing at all fell between 1 and 127, and the sparsest legitimate document ran 61 chars/page.
public class EmptyTranscriptGateTests
{
    private static readonly PipelineOptions Opt = new(MinQuality: 0.60, MinConfidence: 0.80);

    private static PatientMatch SingleMatch() =>
        new(MatchOutcome.Single, new PatientRef(1, "Z000000005", "Test", "T", new DateOnly(1956, 5, 29), "Active"), "kvnr");

    private static ProcessingResult Run(string md, int pages, double leg = 0.9, params DecodedBarcode[] barcodes)
    {
        var t = new Transcript(md, pages, leg, barcodes);
        var data = new ExtractedData([
            new ExtractedField("ContentType", "Verordnung", 0.95, "vision"),
            new ExtractedField("KVNR", "Z000000005", 0.95, "barcode")]);
        return IntakePipeline.Decide(new DocumentUnit("u1", 1, pages, t), t, data, SingleMatch(), Opt);
    }

    // The exact shape of 117203e499e9 — high legibility, many pages, no text.
    [Fact]
    public void The_117203e499e9_case_is_caught_despite_high_legibility()
    {
        var r = Run("\n\n", pages: 6, leg: 0.9);
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains(ReasonCodes.EmptyTranscript, r.ReasonCodes!);
        Assert.DoesNotContain("low_quality", r.ReasonCodes!);   // the score was fine; the TEXT was not
    }

    [Fact]
    public void A_completely_empty_transcript_is_caught()
        => Assert.Contains(ReasonCodes.EmptyTranscript, Run("", pages: 1).ReasonCodes!);

    [Fact]
    public void Whitespace_only_is_caught()
        => Assert.Contains(ReasonCodes.EmptyTranscript, Run("   \t \r\n  ", pages: 1).ReasonCodes!);

    // A decoded barcode is an authoritative alternative source — the BFB payload carries the prescription outright —
    // so a thin transcript alongside one is NOT a failure.
    [Fact]
    public void A_decoded_barcode_exempts_a_thin_transcript()
    {
        var r = Run("", pages: 4, leg: 0.9, new DecodedBarcode("DataMatrix", "payload", true));
        Assert.DoesNotContain(ReasonCodes.EmptyTranscript, r.ReasonCodes ?? []);
    }

    // Margin check against the real corpus: the sparsest LEGITIMATE document measured 61 chars/page and must pass.
    // (These assert only on the empty-transcript code — a bare Verordnung still routes to review on
    // missing_required:DocumentDate, which is a different gate and not what this file is testing.)
    [Fact]
    public void The_sparsest_legitimate_document_in_the_corpus_still_passes()
    {
        var r = Run(new string('x', 912), pages: 15, leg: 1.0);   // 52d144889cc4: 912 chars over 15 pages
        Assert.DoesNotContain(ReasonCodes.EmptyTranscript, r.ReasonCodes ?? []);
    }

    [Fact]
    public void A_normal_transcript_is_not_flagged()
    {
        var r = Run(new string('x', 2400), pages: 2);
        Assert.DoesNotContain(ReasonCodes.EmptyTranscript, r.ReasonCodes ?? []);
    }

    // ---- the shared predicate itself, which the filing path also calls so the two cannot drift ----

    [Theory]
    [InlineData("", 1, true)]
    [InlineData("  ", 3, true)]
    [InlineData("123456789", 1, true)]      // 9 chars, one page → below 10/page
    [InlineData("1234567890", 1, false)]    // exactly at the threshold
    [InlineData("1234567890", 2, true)]     // same text, two pages → below
    public void The_predicate_is_density_based(string md, int pages, bool unusable)
        => Assert.Equal(unusable, TranscriptQuality.IsUnusable(md, pages, null));

    // A malformed page count must not divide the rule away into never firing.
    [Fact]
    public void A_zero_page_count_is_treated_as_one_page()
        => Assert.True(TranscriptQuality.IsUnusable("", 0, null));

    [Fact]
    public void The_code_is_registered()
        => Assert.Contains(ReasonCodes.EmptyTranscript, ReasonCodes.Known);
}
