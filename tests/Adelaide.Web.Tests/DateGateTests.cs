using Adelaide.Core;
using Xunit;

namespace Adelaide.Web.Tests;

// Stage I (1c) — the date VALIDITY guard. A confidently day/month-swapped date is invisible to confidence gating
// (the model is confidently wrong, and the gate deliberately excludes date confidence from IdentityConfidence), so
// validity is the guard: a DocumentDate in the FUTURE routes to review with the date_implausible code. The +1 day
// grace absorbs the UTC-vs-German-local midnight skew and must NOT flag.
public sealed class DateGateTests
{
    private static readonly DateOnly Today = new(2026, 7, 3); // injected via PipelineOptions.Today — deterministic

    // The auto-file recipe (allow-listed Verordnung, confident identity, barcode-settled Erst/Folge — mirrors the
    // outbox tests' FakeExtractor): passes every OTHER gate, so the date under test is the only variable.
    private static ProcessingResult Decide(string documentDate)
    {
        var data = new ExtractedData(new[]
        {
            new ExtractedField("ContentType", "Verordnung", 0.97, "vision"),
            new ExtractedField("KVNR", "Z000000005", 0.97, "barcode"),
            new ExtractedField("DocumentDate", documentDate, 0.95, "vision"),
            new ExtractedField("PrescriptionOrder", "Folgeverordnung", 0.95, "barcode"),
        });
        // A realistic body, not the old "md" placeholder: two characters for a one-page document now (correctly) trips
        // the empty-transcript gate, which is a different gate than the one under test here. See TranscriptQuality.
        var transcript = new Transcript(
            "Verordnung häuslicher Krankenpflege — Verordnungszeitraum von 01.05.2026 bis 31.05.2026, Folgeverordnung.",
            1, 0.95, Array.Empty<DecodedBarcode>());
        var match = new PatientMatch(MatchOutcome.Single,
            new PatientRef(1, "Z000000005", "Test", "T", new DateOnly(1956, 5, 29), "Active"), "kvnr");
        return IntakePipeline.Decide(new DocumentUnit("d1", 1, 1, transcript), transcript, data, match,
            new PipelineOptions(Today: Today));
    }

    [Fact]
    public void FutureDocumentDate_RoutesToReview_WithDateImplausibleCode()
    {
        var r = Decide("2026-07-20"); // well beyond the +1 grace — e.g. a true 20.07 is fine, but a swapped 07.20 cannot exist; a future issue date is the swap's forward-landing signature
        Assert.Equal(Disposition.Review, r.Disposition);
        Assert.Contains("date_implausible", r.ReasonCodes!);
        Assert.Contains(r.Reasons, x => x.Contains("Zukunft"));
    }

    [Fact]
    public void TodayAndGraceDay_StillAutoFile_ButDayTwoIsFlagged()
    {
        Assert.Equal(Disposition.AutoFile, Decide("2026-07-03").Disposition); // today — never flagged
        Assert.Equal(Disposition.AutoFile, Decide("2026-07-04").Disposition); // +1 grace (UTC-vs-German midnight skew) — not flagged
        Assert.Equal(Disposition.Review, Decide("2026-07-05").Disposition);   // +2 — flagged
    }
}
