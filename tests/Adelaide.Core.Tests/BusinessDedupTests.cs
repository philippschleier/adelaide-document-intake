using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// The business content fingerprint (design §12 Phase 1): the "same document" signal for a non-patient doc that has no
// patient+type+date key. Normalization means trivial whitespace/case differences fingerprint the SAME; genuinely
// different text does not (this catches an exact re-scan, not a fuzzy near-duplicate). Too-little text → null, so two
// near-empty transcripts never collide.
public class BusinessDedupTests
{
    [Fact]
    public void Same_text_with_different_whitespace_and_case_fingerprints_the_same()
    {
        var a = BusinessDedup.Fingerprint("Rechnung Nr. 4711  vom 01.05.2026\n\nBetrag: 1.234,56 EUR");
        var b = BusinessDedup.Fingerprint("rechnung   nr. 4711 vom 01.05.2026\nbetrag: 1.234,56 eur");
        Assert.NotNull(a);
        Assert.Equal(a, b); // whitespace-collapsed + lowercased → identical fingerprint (a re-scan with the same OCR text)
    }

    [Fact]
    public void Different_text_fingerprints_differently()
        => Assert.NotEqual(
            BusinessDedup.Fingerprint("Rechnung Nr. 4711 vom 01.05.2026 über 1.234,56 EUR"),
            BusinessDedup.Fingerprint("Rechnung Nr. 4712 vom 02.05.2026 über 9.999,00 EUR"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("zu kurz")] // below the min-length floor → no reliable fingerprint
    public void Too_little_text_returns_null(string? text)
        => Assert.Null(BusinessDedup.Fingerprint(text));

    [Fact]
    public void A_real_length_transcript_produces_a_64_hex_fingerprint()
    {
        var fp = BusinessDedup.Fingerprint("Personalabteilung — Arbeitsvertrag für Max Mustermann, gültig ab 01.06.2026.");
        Assert.NotNull(fp);
        Assert.Equal(64, fp!.Length);
        Assert.Matches("^[0-9a-f]{64}$", fp);
    }

    [Fact]
    public void Long_text_is_bounded_but_still_fingerprints_stably()
    {
        var big = new string('a', 20000);
        Assert.Equal(BusinessDedup.Fingerprint(big + " one tail"), BusinessDedup.Fingerprint(big + " two tail")); // beyond the cap → same prefix hashes the same
    }
}
