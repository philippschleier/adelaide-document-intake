using SkiaSharp;

namespace Adelaide.Ai;

/// <summary>
/// Visual page diff for the duplicate compare view: renders the same page of two PDFs at the same DPI and
/// produces a PNG where matching content is faded to light gray and differing pixels are highlighted red.
/// Pixel comparison is meaningful for same-source documents (identical bytes, re-prints); skewed re-scans will
/// show broad differences — the side-by-side panes stay the primary comparison, this is the "what changed" lens.
/// </summary>
public static class PdfDiff
{
    /// <summary>PNG diff of 1-based page <paramref name="page"/>, or null when either document lacks that page.</summary>
    public static byte[]? DiffPng(string pathA, string pathB, int page, int dpi = 120)
    {
        if (page < 1) return null;
        var pagesA = PdfRenderer.Render(pathA, dpi, page);
        var pagesB = PdfRenderer.Render(pathB, dpi, page);
        try
        {
            if (pagesA.Count < page || pagesB.Count < page) return null;
            var a = pagesA[page - 1];
            using var b = pagesB[page - 1].Resize(
                new SKImageInfo(a.Width, a.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (b is null) return null;

            var pa = a.Pixels;
            var pb = b.Pixels;
            var po = new SKColor[pa.Length];
            for (var i = 0; i < pa.Length; i++)
            {
                var la = Lum(pa[i]);
                var lb = Lum(pb[i]);
                if (Math.Abs(la - lb) > DiffThreshold)
                    po[i] = new SKColor(214, 28, 28);                         // difference → red
                else
                {
                    var g = (byte)(192 + la / 4);                              // match → faded gray ghost of A
                    po[i] = new SKColor(g, g, g);
                }
            }

            using var outBmp = new SKBitmap(a.Width, a.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            outBmp.Pixels = po;
            using var img = SKImage.FromBitmap(outBmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            foreach (var p in pagesA) p.Dispose();
            foreach (var p in pagesB) p.Dispose();
        }
    }

    private const int DiffThreshold = 48; // luminance delta below this counts as "same" (scan noise tolerance)

    private static int Lum(SKColor c) => (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000;
}
