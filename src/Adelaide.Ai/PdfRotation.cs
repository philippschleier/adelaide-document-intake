using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Adelaide.Ai;

/// <summary>
/// Lossless per-page rotation via the PDF <c>/Rotate</c> page attribute (0/90/180/270). The page CONTENT bytes are
/// untouched — <c>/Rotate</c> is a non-destructive display attribute — so the pre-rotation document is inherently
/// reconstructible by zeroing <c>/Rotate</c> (WORM-friendly: no separate original copy to keep). Orientations are
/// ABSOLUTE targets, not cumulative deltas, so re-applying the same rotation is idempotent (no double-rotate).
///
/// Pure-managed (PdfSharp) — no native dependency, so none of the native-bits / linux-x64 risk. Throws on
/// a PDF PdfSharp cannot open/parse; callers MUST catch, leave the original untouched, and surface an error — a
/// rotation must never emit a corrupted filed artifact.
/// </summary>
public static class PdfRotation
{
    /// <summary>A copy of <paramref name="pdf"/> with each listed page's <c>/Rotate</c> set to its ABSOLUTE target
    /// angle (1-based page → 0/90/180/270). Pages absent from the map keep their current /Rotate. Throws if the PDF
    /// cannot be parsed.</summary>
    public static byte[] WithRotations(byte[] pdf, IReadOnlyDictionary<int, int> pageAngles)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (var (page1Based, angle) in pageAngles)
            if (page1Based >= 1 && page1Based <= doc.PageCount)
                doc.Pages[page1Based - 1].Rotate = Normalize(angle); // absolute set of /Rotate (not a delta)
        using var output = new MemoryStream();
        doc.Save(output);
        return output.ToArray();
    }

    /// <summary>The current absolute <c>/Rotate</c> of each page (1-based page → 0/90/180/270). Throws if the PDF
    /// cannot be parsed — the same fail-safe boundary as <see cref="WithRotations"/>.</summary>
    public static IReadOnlyDictionary<int, int> ReadRotations(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify); // Modify (not the obsolete/unimplemented ReadOnly); we only read .Rotate, never Save
        var map = new Dictionary<int, int>(doc.PageCount);
        for (var i = 0; i < doc.PageCount; i++) map[i + 1] = Normalize(doc.Pages[i].Rotate);
        return map;
    }

    // /Rotate must be an orthogonal multiple of 90 in [0,360); snap anything else to the nearest legal value.
    private static int Normalize(int angle)
    {
        var a = ((angle % 360) + 360) % 360;
        return a switch { >= 45 and < 135 => 90, >= 135 and < 225 => 180, >= 225 and < 315 => 270, _ => 0 };
    }
}
