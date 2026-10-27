namespace Adelaide.Core;

/// <summary>Content-based PDF detection (magic bytes) for upload validation (FINDING 1a). Both upload entry points write
/// attacker-controlled bytes to a <c>.pdf</c> path and hand them to a NATIVE renderer (PDFium) and on to SharePoint, so
/// a renamed <c>.html</c>/<c>.exe</c>/script must be rejected at the trust boundary — the file extension and the
/// client-supplied MIME are never trusted. A conformant PDF begins with <c>%PDF-</c>; some scanners/faxes prepend a few
/// stray bytes, so we scan the first ~1&#160;KB (readers do the same) rather than insisting on offset 0.</summary>
public static class PdfSignature
{
    private static ReadOnlySpan<byte> Magic => "%PDF-"u8;

    /// <summary>True if the PDF signature appears within the first <paramref name="searchWindow"/> bytes.</summary>
    public static bool LooksLikePdf(ReadOnlySpan<byte> bytes, int searchWindow = 1024) =>
        bytes[..Math.Min(bytes.Length, searchWindow)].IndexOf(Magic) >= 0;
}
