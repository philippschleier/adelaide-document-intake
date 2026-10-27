using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Adelaide.Ai;

/// <summary>
/// Lossless PDF page operations via PdfSharp — page objects are IMPORTED (not re-encoded), so image quality and any
/// per-page /Rotate are preserved. <see cref="ExtractRange"/> is the split primitive (1 file → N units); a merge
/// primitive will join here for slice 3. Pure-managed (no native dependency). Throws on a PDF PdfSharp cannot parse;
/// callers MUST catch, leave the source untouched, and surface an error — never emit a corrupt artifact.
/// </summary>
public static class PdfPageOps
{
    /// <summary>Page count of <paramref name="pdf"/>.</summary>
    public static int PageCount(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    /// <summary>A new PDF containing pages [<paramref name="first"/>..<paramref name="last"/>] (1-based, inclusive) of
    /// the source, in order. Throws if the range is empty or the PDF can't be parsed.</summary>
    public static byte[] ExtractRange(byte[] pdf, int first, int last)
    {
        if (first < 1) first = 1;
        using var input = new MemoryStream(pdf, writable: false);
        using var src = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        using var dst = new PdfDocument();
        for (var p = first; p <= last && p <= src.PageCount; p++) dst.AddPage(src.Pages[p - 1]); // imports the page object, lossless
        if (dst.PageCount == 0) throw new InvalidOperationException($"empty page range {first}-{last} for a {src.PageCount}-page document");
        using var output = new MemoryStream();
        dst.Save(output);
        return output.ToArray();
    }
}
