using System;
using System.Linq;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

public class BarcodeSummaryTests
{
    // A synthetic Muster 12 BFB PDF417 (1-based field NR -> value), wrapped as a DecodedBarcode. No real KVNRs/names.
    private static DecodedBarcode Bfb(params (int Nr, string Val)[] fields)
    {
        var max = fields.Length == 0 ? 1 : fields.Max(f => f.Nr);
        var arr = Enumerable.Repeat("", max).ToArray();
        foreach (var (nr, val) in fields) arr[nr - 1] = val;
        return new DecodedBarcode("PDF_417", string.Join('\t', arr), ChecksumValid: true);
    }

    private static DecodedBarcode[] Verordnung() => new[] { Bfb(
        (1, "12"), (2, "a"), (3, "12"),
        (4, "Mustermann"), (5, "Max"), (6, "19600101"),
        (9, "Z000000005"), (15, "20260101"), (16, "I70.20"),
        (17, "f"), (20, "260611"), (21, "260930"),
        (49, "b"), (50, "1")) };

    [Fact]
    public void Block_renders_the_full_decoded_verordnung()
    {
        var block = BarcodeSummary.Block(Verordnung());
        Assert.NotNull(block);
        Assert.Contains("Folgeverordnung", block);
        Assert.Contains("11.06.2026", block);                 // Zeitraum von, German dd.MM.yyyy
        Assert.Contains("30.09.2026", block);                 // Zeitraum bis
        Assert.Contains("Kompressionsstrümpfe anziehen (beidseits)", block);
        Assert.Contains("Mustermann, Max", block);            // full content now includes identity…
        Assert.Contains("Z000000005", block);                 // …KVNR…
        Assert.Contains("I70.20", block);                     // …Diagnose…
        Assert.Contains("01.01.2026", block);                 // …and the issue date
    }

    [Fact]
    public void Block_is_null_without_a_decodable_barcode()
    {
        Assert.Null(BarcodeSummary.Block(Array.Empty<DecodedBarcode>()));
        Assert.Null(BarcodeSummary.Block(new[] { new DecodedBarcode("CODE_128", "Z000000005", ChecksumValid: true) })); // bare KVNR, not a BFB
        Assert.Null(BarcodeSummary.Block(null));
    }

    [Fact]
    public void LeadWith_prepends_block_then_transcript_and_passes_through_when_no_barcode()
    {
        var led = BarcodeSummary.LeadWith("TRANSCRIPT", Verordnung());
        Assert.StartsWith("— Verordnungsdaten", led);
        Assert.EndsWith("TRANSCRIPT", led);

        // no barcode (e.g. the review→file path rebuilds the transcript without barcodes) → unchanged, no double block
        Assert.Equal("TRANSCRIPT", BarcodeSummary.LeadWith("TRANSCRIPT", Array.Empty<DecodedBarcode>()));
    }

    [Fact]
    public void Block_renders_a_medikationsplan_from_a_datamatrix()
    {
        var dm = new DecodedBarcode("DATA_MATRIX",
            "<MP v=\"027\"><P b=\"19560529\" egk=\"Z000000005\" f=\"Mustermann\" g=\"Max\"/>" +
            "<S><M m=\"1\" p=\"1234567\" r=\"Bluthochdruck\"/></S></MP>", ChecksumValid: false);
        var block = BarcodeSummary.Block(new[] { dm });
        Assert.NotNull(block);
        Assert.StartsWith("— Medikationsplan", block);
        Assert.Contains("PZN 1234567", block);
        Assert.Contains("Bluthochdruck", block);     // the indication is searchable
        Assert.Contains("Mustermann, Max", block);
    }
}
