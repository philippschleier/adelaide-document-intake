using System.Linq;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

/// <summary>
/// Gate 2 for the zxing-cpp decoder upgrade: a barcode KVNR is trusted for patient matching ONLY when it comes from
/// a structurally-valid KBV payload — a Muster 12 BFB or a BMP. A checksum-valid, KVNR-shaped token sitting inside a
/// franking / routing / arbitrary code (which the stronger decoder now surfaces, e.g. on Krankenkasse letters) must
/// NOT be trusted — otherwise the document would file under the wrong patient. Synthetic KVNR only (Z000000005).
/// </summary>
public class BarcodeIdentityTests
{
    private const string ValidKvnr = "Z000000005";   // checksum-valid synthetic KVNR (as used across the BFB/BMP tests)

    // Tab-delimited Muster 12 BFB payload with the KVNR in field 09 (>= 21 fields so BfbBarcode.Parse accepts it).
    private static string BfbPayload(string kvnr)
    {
        var arr = Enumerable.Repeat("", 21).ToArray();
        arr[0] = "12";        // field 01 = Formularcode
        arr[2] = "12";        // field 03 = Versionsnummer
        arr[3] = "Mustermann"; arr[4] = "Max"; arr[5] = "19600101";
        arr[8] = kvnr;        // field 09 = Versichertennummer
        return string.Join('\t', arr);
    }

    [Fact]
    public void Bfb_payload_supplies_its_kvnr()
        => Assert.Equal(ValidKvnr, BarcodeIdentity.StructuralKvnr(BfbPayload(ValidKvnr)));

    [Fact]
    public void Bmp_payload_supplies_its_egk_kvnr()
        => Assert.Equal(ValidKvnr, BarcodeIdentity.StructuralKvnr($"<MP><P g=\"Max\" f=\"Mustermann\" b=\"19600101\" egk=\"{ValidKvnr}\"/></MP>"));

    // THE gate: a checksum-valid KVNR token inside a NON-structural payload (franking / routing / bare token) is
    // refused — even though the token validates and the looser BarcodeScanner.KvnrOf would happily extract it.
    [Theory]
    [InlineData("Z000000005")]                                   // bare KVNR (e.g. an eGK / routing sticker)
    [InlineData("  DEUTSCHE POST FRANKING  Z000000005  0815 ")]  // franking / routing code carrying the token
    [InlineData("A0001 Z000000005 R99")]                          // short admin / Leitcode-style payload
    public void Kvnr_token_in_non_structural_payload_is_refused(string payload)
        => Assert.Null(BarcodeIdentity.StructuralKvnr(payload));

    // A well-formed Muster 12 whose field-09 KVNR fails the checksum is refused (never guess a patient key).
    [Fact]
    public void Bfb_payload_with_checksum_invalid_kvnr_is_refused()
        => Assert.Null(BarcodeIdentity.StructuralKvnr(BfbPayload("Z000000000")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some scanned text with no barcode at all")]
    public void Empty_or_arbitrary_payloads_return_null(string? payload)
        => Assert.Null(BarcodeIdentity.StructuralKvnr(payload));
}
