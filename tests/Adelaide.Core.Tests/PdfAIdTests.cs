using System.Text;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

/// <summary>C10: the PDF/A declaration sniff — both XMP syntaxes, the namespace-only form, and the negatives.
/// Synthetic byte streams only (the sniff is byte-pattern-based by design; veraPDF-grade validation is Phase 3).</summary>
public class PdfAIdTests
{
    private static byte[] Pdf(string xmp) => Encoding.UTF8.GetBytes($"%PDF-1.7\nstream\n{xmp}\nendstream\n%%EOF");

    [Fact]
    public void Attribute_syntax_declares_and_reads()
    {
        var pdf = Pdf("""<rdf:Description xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" pdfaid:part="1" pdfaid:conformance="A"/>""");
        Assert.True(PdfAId.Declares(pdf));
        Assert.True(PdfAId.TryRead(pdf, out var part, out var conf));
        Assert.Equal(1, part);
        Assert.Equal("A", conf);
    }

    [Fact]
    public void Element_syntax_declares_and_reads()
    {
        // Real XMP always declares the pdfaid prefix's namespace (required XML), so the sniff sees both.
        var pdf = Pdf("""xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/"><pdfaid:part>2</pdfaid:part><pdfaid:conformance>B</pdfaid:conformance>""");
        Assert.True(PdfAId.Declares(pdf));
        Assert.True(PdfAId.TryRead(pdf, out var part, out var conf));
        Assert.Equal(2, part);
        Assert.Equal("B", conf);
    }

    [Fact]
    public void Either_marker_alone_does_not_declare()
    {
        // Both markers must co-occur (a real declaration has both; either alone is more likely quoted text —
        // verification finding: the whole-file scan's false-positive direction must stay bounded).
        Assert.False(PdfAId.Declares(Pdf("""xmlns:x="http://www.aiim.org/pdfa/ns/id/" """)));   // namespace only
        Assert.False(PdfAId.Declares(Pdf("a scanned article mentioning pdfaid:part literally"))); // key only, prose context
    }

    [Fact]
    public void Plain_pdf_does_not_declare()
    {
        Assert.False(PdfAId.Declares(Pdf("<x:xmpmeta>plain document, no pdfa identification</x:xmpmeta>")));
        Assert.False(PdfAId.Declares(Encoding.UTF8.GetBytes("%PDF-1.4\nno xmp at all\n%%EOF")));
        Assert.False(PdfAId.TryRead(Encoding.UTF8.GetBytes("%PDF-1.4"), out _, out _));
    }

    [Fact]
    public void Garbage_after_the_key_reads_as_declared_but_unreadable()
    {
        var pdf = Pdf("""xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" pdfaid:part="???" """); // declared but the value is junk
        Assert.True(PdfAId.Declares(pdf));
        Assert.False(PdfAId.TryRead(pdf, out _, out _)); // '?' is not a digit — no false part number
    }
}
