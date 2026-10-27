using System.Linq;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

/// <summary>Muster 12 BFB barcode decoding. Payloads are SYNTHETIC (no real KVNRs/names) — built from the KBV
/// field layout so the indices stay pinned even though real prescriptions are never committed to the repo.</summary>
public class BfbBarcodeTests
{
    // Build a tab-delimited payload from (1-based field NR -> value); intermediate fields are empty strings.
    private static string Payload(params (int Nr, string Val)[] fields)
    {
        var max = fields.Length == 0 ? 1 : fields.Max(f => f.Nr);
        var arr = Enumerable.Repeat("", max).ToArray();
        foreach (var (nr, val) in fields) arr[nr - 1] = val;
        return string.Join('\t', arr);
    }

    // Minimal valid Muster 12 header (form code 12, version 12) + synthetic identity.
    private static (int, string)[] Header() => new (int, string)[]
    {
        (1, "12"), (2, "a"), (3, "12"),
        (4, "Mustermann"), (5, "Max"), (6, "19600101"),
        (9, "Z000000005"), (15, "20260101"), (16, "I70.20"),
    };

    [Fact]
    public void Folgeverordnung_with_beidseits_compression_is_decoded()
    {
        var p = Payload(Header().Concat(new (int, string)[]
        {
            (17, "f"), (20, "260101"), (21, "260331"),
            (49, "b"), (50, "1"), (51, "1"),
        }).ToArray());
        var b = BfbBarcode.Parse(p);
        Assert.NotNull(b);
        Assert.Equal("Folgeverordnung", b!.PrescriptionOrder);
        Assert.Equal(new DateOnly(2026, 1, 1), b.ValidFrom);
        Assert.Equal(new DateOnly(2026, 3, 31), b.ValidUntil);
        Assert.Equal("beidseits", b.KompressionSide);
        Assert.Contains("Kompressionsstrümpfe anziehen (beidseits)", b.Leistungen);
        Assert.Contains("Kompressionsstrümpfe ausziehen (beidseits)", b.Leistungen);
        Assert.Equal("Mustermann", b.LastName);
        Assert.Equal(new DateOnly(1960, 1, 1), b.BirthDate);
        Assert.Equal("Z000000005", b.Kvnr);
    }

    [Fact]
    public void Erst_flag_maps_to_Erstverordnung_with_measure()
    {
        var p = Payload(Header().Concat(new (int, string)[] { (17, "e"), (20, "260504"), (21, "260518"), (73, "1") }).ToArray());
        var b = BfbBarcode.Parse(p);
        Assert.Equal("Erstverordnung", b!.PrescriptionOrder);
        Assert.Contains("Wundversorgung akut", b.Leistungen);
    }

    [Fact]
    public void Injektionen_route_is_appended()
    {
        var p = Payload(Header().Concat(new (int, string)[] { (17, "e"), (34, "1"), (36, "s") }).ToArray());
        Assert.Contains("Injektionen (subkutan)", BfbBarcode.Parse(p)!.Leistungen);
    }

    [Fact]
    public void Trailing_fields_may_be_trimmed() // payload that stops at field 21
    {
        var p = Payload(Header().Concat(new (int, string)[] { (17, "f"), (20, "260101"), (21, "260131") }).ToArray());
        var b = BfbBarcode.Parse(p);
        Assert.NotNull(b);
        Assert.Equal("Folgeverordnung", b!.PrescriptionOrder);
        Assert.Empty(b.Leistungen);
        Assert.Null(b.KompressionSide);
    }

    [Fact]
    public void Non_Muster12_payloads_return_null()
    {
        Assert.Null(BfbBarcode.Parse("Z000000005"));                          // bare KVNR
        Assert.Null(BfbBarcode.Parse("<MP><P egk=\"Z000000005\"/></MP>"));    // Medikationsplan XML (no tabs)
        Assert.Null(BfbBarcode.Parse("13\ta\t12\tFoo\tBar\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t\t")); // a different Muster (code 13)
        Assert.Null(BfbBarcode.Parse(""));
        Assert.Null(BfbBarcode.Parse(null));
    }

    [Fact]
    public void Partial_or_invalid_dates_become_null()
    {
        var p = Payload(Header().Concat(new (int, string)[] { (6, "19600000"), (17, "e"), (20, "260101"), (21, "999999") }).ToArray());
        var b = BfbBarcode.Parse(p);
        Assert.Null(b!.BirthDate);                          // JJJJMM00 partial -> null
        Assert.Null(b.ValidUntil);                          // 999999 invalid -> null
        Assert.Equal(new DateOnly(2026, 1, 1), b.ValidFrom);
    }
}
