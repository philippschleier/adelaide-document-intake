namespace Adelaide.Core;

/// <summary>PDF/A DECLARATION sniff (migration plan C10 — verify-and-pin, not convert). The DI rendition — the
/// record of file under R7 — declares PDF/A-1a in its XMP metadata (verified empirically); this asserts that
/// declaration on the exact bytes being uploaded, so a filing whose artifact does NOT declare (a DI-fallback
/// original, a DI regression) is MARKED on its Filed event rather than silently indistinguishable.
///
/// A declaration sniff, deliberately NOT validation: veraPDF-grade conformance checking runs on synthetic
/// artifacts in Phase 3 (real filed artifacts never leave the machine). The sniff scans the raw bytes and can err
/// in BOTH directions, each bounded: a producer that compresses its XMP stream reads as NOT declaring (a
/// conservative false negative — flags for the census, never blocks filing), and a file that happens to carry
/// BOTH the identification namespace AND the part key outside a real XMP packet (a document quoting PDF/A markup
/// verbatim) reads as declaring — requiring both makes that vanishingly rare, and the independent DiRendition
/// boolean on the same Filed event still marks the DI-fallback class regardless.</summary>
public static class PdfAId
{
    private static ReadOnlySpan<byte> Namespace => "http://www.aiim.org/pdfa/ns/id/"u8;
    private static ReadOnlySpan<byte> PartKey => "pdfaid:part"u8;
    private static ReadOnlySpan<byte> ConformanceKey => "pdfaid:conformance"u8;

    /// <summary>True when the bytes carry the XMP PDF/A identification schema — BOTH the namespace and the
    /// pdfaid:part key must be present (any real XMP declaration has both: the prefix requires its xmlns; either
    /// alone in isolation is more likely quoted text than a declaration).</summary>
    public static bool Declares(ReadOnlySpan<byte> pdf) =>
        pdf.IndexOf(Namespace) >= 0 && pdf.IndexOf(PartKey) >= 0;

    /// <summary>Best-effort read of the declared part + conformance (e.g. 1, "A") for census display. Handles both
    /// XMP syntaxes — attribute (<c>pdfaid:part="1"</c>) and element (<c>&lt;pdfaid:part&gt;1&lt;/…&gt;</c>) — by
    /// taking the first digit/letter within a few bytes after the key. False when absent or unparseable.</summary>
    public static bool TryRead(ReadOnlySpan<byte> pdf, out int part, out string conformance)
    {
        part = 0;
        conformance = "";
        var p = ValueAfter(pdf, PartKey);
        if (p is not { } partChar || partChar is < (byte)'1' or > (byte)'9') return false;
        part = partChar - '0';
        if (ValueAfter(pdf, ConformanceKey) is { } confChar && char.IsAsciiLetterUpper((char)confChar))
            conformance = ((char)confChar).ToString();
        return true;
    }

    /// <summary>The first alphanumeric byte within a short window after the key — tolerant of ="…", &gt;…, whitespace.</summary>
    private static byte? ValueAfter(ReadOnlySpan<byte> pdf, ReadOnlySpan<byte> key)
    {
        var at = pdf.IndexOf(key);
        if (at < 0) return null;
        var start = at + key.Length;
        var end = Math.Min(start + 8, pdf.Length);
        for (var i = start; i < end; i++)
            if (char.IsAsciiLetterOrDigit((char)pdf[i]))
                return pdf[i];
        return null;
    }
}
