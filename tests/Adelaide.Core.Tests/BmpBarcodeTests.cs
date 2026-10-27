using System;
using System.Linq;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

/// <summary>BMP (Bundeseinheitlicher Medikationsplan) DataMatrix decoding. Payloads are SYNTHETIC — no real KVNRs,
/// names or medications — built to the BMP XML shape (root MP, patient P, medication lines M).</summary>
public class BmpBarcodeTests
{
    private const string Sample =
        "<MP U=\"X\" l=\"de-DE\" v=\"027\">" +
        "<P b=\"19560529\" egk=\"Z000000005\" f=\"Mustermann\" g=\"Max\"/>" +
        "<A c=\"Teststadt\" n=\"Dr. Test\"/>" +
        "<S><M m=\"1\" p=\"1234567\" r=\"Bluthochdruck\" v=\"1\"/>" +
        "<M du=\"s\" m=\"1/2\" p=\"7654321\" r=\"Diabetes\"/></S></MP>";

    [Fact]
    public void Parses_patient_header_and_medications()
    {
        var mp = BmpBarcode.Parse(Sample);
        Assert.NotNull(mp);
        Assert.Equal("Mustermann", mp!.LastName);
        Assert.Equal("Max", mp.FirstName);
        Assert.Equal(new DateOnly(1956, 5, 29), mp.BirthDate);
        Assert.Equal("Z000000005", mp.Kvnr);
        Assert.Equal(2, mp.Medikamente.Count);

        var m0 = mp.Medikamente[0];
        Assert.Equal("1234567", m0.Pzn);
        Assert.Equal("Bluthochdruck", m0.Reason);
        Assert.StartsWith("1-0-0-0", m0.Dosing);

        var m1 = mp.Medikamente[1];
        Assert.Equal("7654321", m1.Pzn);
        Assert.Equal("Diabetes", m1.Reason);
        Assert.Contains("1/2", m1.Dosing);
        Assert.Contains("s", m1.Dosing);              // dosing unit appended
    }

    [Fact]
    public void Repairs_utf8_read_as_latin1()
    {
        // "Entwässerung" mojibaked the way ZXing returns a UTF-8 DataMatrix: the ä's two UTF-8 bytes (0xC3 0xA4) read
        // as Latin-1 become U+00C3 U+00A4. FixEncoding must turn that back into a real ä.
        var payload = "<MP v=\"027\"><P egk=\"Z000000005\"/><S><M p=\"1\" r=\"EntwÃ¤sserung\"/></S></MP>";
        var mp = BmpBarcode.Parse(payload);
        Assert.NotNull(mp);
        Assert.Equal("Entwässerung", mp!.Medikamente.Single().Reason);
    }

    [Fact]
    public void Non_bmp_payloads_return_null()
    {
        Assert.Null(BmpBarcode.Parse("Z000000005"));            // bare KVNR
        Assert.Null(BmpBarcode.Parse("12\ta\t12\tFoo"));        // a Muster 12 BFB payload
        Assert.Null(BmpBarcode.Parse("<MP><P/></MP><junk"));    // malformed XML
        Assert.Null(BmpBarcode.Parse(""));
        Assert.Null(BmpBarcode.Parse(null));
    }
}
