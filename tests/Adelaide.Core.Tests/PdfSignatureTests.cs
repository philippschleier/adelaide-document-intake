using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// FINDING 1a: upload content validation must accept real PDFs (signature at offset 0 or within the first ~1 KB) and
// reject anything else — a renamed .html/.exe must not be persisted, rendered, or filed.
public class PdfSignatureTests
{
    [Fact]
    public void Header_at_offset_zero_is_accepted() =>
        Assert.True(PdfSignature.LooksLikePdf("%PDF-1.7\n1 0 obj\n"u8));

    [Fact]
    public void Signature_within_the_first_kilobyte_is_accepted()
    {
        var bytes = new byte[700];
        "%PDF-1.4"u8.CopyTo(bytes.AsSpan(200)); // a few stray leading bytes, as some scanners/faxes produce
        Assert.True(PdfSignature.LooksLikePdf(bytes));
    }

    [Fact]
    public void Html_masquerading_as_pdf_is_rejected() =>
        Assert.False(PdfSignature.LooksLikePdf("<html><script>alert(1)</script></html>"u8));

    [Fact]
    public void Signature_beyond_the_search_window_is_rejected()
    {
        var bytes = new byte[2048];
        "%PDF-1.4"u8.CopyTo(bytes.AsSpan(1500)); // past the 1 KB scan window → treated as not-a-PDF
        Assert.False(PdfSignature.LooksLikePdf(bytes));
    }

    [Fact]
    public void Empty_input_is_rejected() => Assert.False(PdfSignature.LooksLikePdf(System.ReadOnlySpan<byte>.Empty));
}
