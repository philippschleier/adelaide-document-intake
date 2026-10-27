using PDFtoImage;
using SkiaSharp;

namespace Adelaide.Ai;

/// <summary>Renders PDF pages to images via PDFium (PDFtoImage). Caller disposes the returned bitmaps.</summary>
public static class PdfRenderer
{
    public static IReadOnlyList<SKBitmap> Render(string path, int dpi = 300, int maxPages = 15, int maxEdgePixels = 5000)
    {
        using var stream = File.OpenRead(path);
        var safeDpi = SafeDpi(stream, dpi, maxPages, maxEdgePixels);
        stream.Position = 0;
#pragma warning disable CA1416 // PDFium (PDFtoImage) supports every server target we deploy to (Windows + Linux); this server-side path is never reached on browser/wasm
        return Conversion.ToImages(stream, options: new RenderOptions(Dpi: safeDpi)).Take(maxPages).ToList();
#pragma warning restore CA1416
    }

    /// <summary>Render a SINGLE 1-based page to a bitmap — the thumbnail path (O(1) per page, unlike <see cref="Render"/>
    /// which rasterizes the whole prefix). Caller disposes. Same PDF-bomb DPI clamp as <see cref="Render"/>.</summary>
    public static SKBitmap RenderPage(string path, int page, int dpi = 100, int maxEdgePixels = 2000)
        => RenderPage(File.ReadAllBytes(path), page, dpi, maxEdgePixels);

    /// <summary>Render a SINGLE 1-based page from PDF BYTES — used when the caller has already applied a rotation
    /// (<see cref="PdfRotation.WithRotations"/>) so the rendered page matches what the browser viewer shows.</summary>
    public static SKBitmap RenderPage(byte[] pdf, int page, int dpi = 100, int maxEdgePixels = 2000)
    {
        if (page < 1) page = 1;
        using var stream = new MemoryStream(pdf, writable: false);
        var safeDpi = SafeDpi(stream, dpi, page, maxEdgePixels);
        stream.Position = 0;
#pragma warning disable CA1416 // PDFium supports every server target we deploy to (Windows + Linux); never reached on browser/wasm
        return Conversion.ToImage(stream, page: page - 1, options: new RenderOptions(Dpi: safeDpi));
#pragma warning restore CA1416
    }

    // FINDING 4: a SMALL file can declare an ENORMOUS page canvas (a dimension "PDF bomb"); at a fixed DPI that renders
    // to a giant bitmap → memory exhaustion. Clamp the render DPI so the longest page edge stays within maxEdgePixels,
    // bounding EACH bitmap's allocation regardless of the PDF's declared MediaBox. Page sizes come back in points
    // (72/inch); pixels = points / 72 * dpi. Normal A4 (≤ 842 pt) is unaffected at 300 DPI; only abnormally large pages
    // are down-sampled (and a page rendered illegibly small routes to human review — never silently mis-filed).
    private static int SafeDpi(Stream pdf, int requestedDpi, int maxPages, int maxEdgePixels)
    {
        try
        {
#pragma warning disable CA1416
            float maxEdgePoints = 0;
            foreach (var s in Conversion.GetPageSizes(pdf, leaveOpen: true).Take(maxPages))
                maxEdgePoints = Math.Max(maxEdgePoints, Math.Max(s.Width, s.Height));
#pragma warning restore CA1416
            if (maxEdgePoints <= 0) return requestedDpi;
            var cap = (int)(maxEdgePixels * 72.0 / maxEdgePoints);
            return Math.Max(1, Math.Min(requestedDpi, cap));
        }
        catch { return requestedDpi; } // sizes unreadable → fall back; the page-count cap + JPEG downscale still bound cost
    }

    /// <summary>Encode a page to JPEG, downscaling to <paramref name="maxWidth"/> so the vision payload/token cost stays
    /// bounded. JPEG is for the AI VISION payload only (GPT-4o accepts it and token cost dominates fidelity) — NOT the
    /// on-screen/record page image, which must not carry DCT ringing around text (see <see cref="Webp"/>).</summary>
    public static byte[] Jpeg(SKBitmap bmp, int quality = 80, int maxWidth = 1500)
    {
        var use = bmp;
        var owns = false;
        if (bmp.Width > maxWidth)
        {
            var h = (int)(bmp.Height * (maxWidth / (double)bmp.Width));
            use = bmp.Resize(new SKImageInfo(maxWidth, h, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            owns = true;
        }
        using var img = SKImage.FromBitmap(use);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        var bytes = data.ToArray();
        if (owns) use.Dispose();
        return bytes;
    }

    /// <summary>How a page image will be used — picks the WebP quality in <see cref="Webp"/>.</summary>
    public enum PageImageKind
    {
        /// <summary>The interactive reading / viewport page image. These WebPs are EPHEMERAL DISPLAY renders — NOT the
        /// archived record (that is the FILED PDF, with <c>/Rotate</c> baked in). So they use HIGH-QUALITY LOSSY (q85):
        /// visually clean on text/greyscale scans but a fraction of the encode time + bytes of lossless — which matters
        /// because the zoomed/HiDPI reader re-requests ~2000px-wide pages on every rotation and zoom step.</summary>
        Reading,
        /// <summary>A small rail/overview thumbnail — aggressively lossy, low quality, optimised for the smallest bytes.</summary>
        Thumbnail,
    }

    /// <summary>Encode a rendered page to WebP for the viewer — NEVER JPEG. These are scans (text on white); JPEG's DCT
    /// rings artefacts around every glyph, while WebP handles hard black/white edges cleanly. Both kinds are EPHEMERAL
    /// display renders (the record is the filed PDF, not these), so both are lossy: the reading view at high quality
    /// (q85), thumbnails aggressively small (q45). Downscales to <paramref name="maxWidth"/> first. Returns raw WebP
    /// bytes (serve as <c>image/webp</c>).</summary>
    public static byte[] Webp(SKBitmap bmp, int maxWidth, PageImageKind kind)
    {
        var use = bmp;
        var owns = false;
        if (bmp.Width > maxWidth)
        {
            var h = (int)(bmp.Height * (maxWidth / (double)bmp.Width));
            use = bmp.Resize(new SKImageInfo(maxWidth, h, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            owns = true;
        }
        try
        {
            // Ephemeral display renders (the archived record is the filed PDF), so lossy for BOTH: the reading view at q85
            // — clean on text/greyscale edges, but far cheaper in time + bytes than lossless at ~2000px on every
            // rotate/zoom — and the overview thumbnail aggressively small at q45.
            return EncodeWebp(use, SKWebpEncoderCompression.Lossy, kind == PageImageKind.Thumbnail ? 45 : 85);
        }
        finally { if (owns) use.Dispose(); }
    }

    private static byte[] EncodeWebp(SKBitmap bmp, SKWebpEncoderCompression compression, int quality)
    {
        using var pm = bmp.PeekPixels();
        if (pm != null)
        {
            using var ms = new MemoryStream();
            using var w = new SKManagedWStream(ms);
            if (pm.Encode(w, new SKWebpEncoderOptions(compression, quality))) { w.Flush(); return ms.ToArray(); }
        }
        // Fallback (should not happen): the simple lossy encoder still beats JPEG for text edges.
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Webp, quality);
        return data.ToArray();
    }
}
