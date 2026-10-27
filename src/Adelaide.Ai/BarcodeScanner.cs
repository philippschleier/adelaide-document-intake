using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Adelaide.Core;
using SkiaSharp;
using ZXingCpp;

namespace Adelaide.Ai;

/// <summary>
/// Free, local, self-hosted barcode decode over rendered page images, using the native zxing-cpp engine (ships
/// linux-x64 + win-x64 binaries). It is markedly stronger than the previous ZXing.Net decoder on the real-world
/// scanned/faxed DataMatrix codes this pipeline sees — measured ~9× more document-boundary codes recovered on the
/// review-tail corpus, matching a commercial reader on all but genuine photos. zxing-cpp handles rotation, inversion
/// and multi-scale internally; a region-crop + upscale fallback is kept for small/degraded codes. The
/// <see cref="DecodedBarcode.ChecksumValid"/> flag reports whether a checksum-valid KVNR is present (informational);
/// patient matching itself goes through <see cref="Adelaide.Core.BarcodeIdentity.StructuralKvnr"/>.
/// </summary>
public static class BarcodeScanner
{
    public static IReadOnlyList<DecodedBarcode> Scan(IEnumerable<SKBitmap> pages)
    {
        var result = new List<DecodedBarcode>();
        var seen = new HashSet<string>();
        foreach (var page in pages)
            foreach (var (format, text, _) in DecodePage(page))
            {
                var kvnr = KvnrOf(text);
                // Keep the FULL payload: the Muster 12 BFB barcode carries the entire prescription and the BMP the
                // medication plan; only the structural parsers (BfbBarcode/BmpBarcode) ever consume it.
                if (seen.Add(format + "|" + text))
                    result.Add(new DecodedBarcode(format, text, ChecksumValid: kvnr is not null));
            }
        return result;
    }

    /// <summary>Debug: every decoded barcode with its FULL payload and byte length (Scan() keeps only the flag).</summary>
    public static IReadOnlyList<(string Format, string Text, int Bytes)> ScanRaw(IEnumerable<SKBitmap> pages)
    {
        var result = new List<(string, string, int)>();
        var seen = new HashSet<string>();
        foreach (var page in pages)
            foreach (var bc in DecodePage(page))
                if (seen.Add(bc.Format + "|" + bc.Text))
                    result.Add(bc);
        return result;
    }

    /// <summary>The checksum-valid KVNR embedded in a barcode payload (an egk="…" attribute or a letter+9-digit
    /// token), upper-cased, or null. NOTE: to DRIVE patient matching, use
    /// <see cref="Adelaide.Core.BarcodeIdentity.StructuralKvnr"/> — it trusts a KVNR only from a structural KBV/BMP
    /// payload, so a franking/routing code (now decodable) cannot inject a wrong-patient key. This looser scan
    /// remains only for the informational per-barcode ChecksumValid flag.</summary>
    public static string? KvnrOf(string payload)
    {
        var egk = Regex.Match(payload, "egk=\"([^\"]+)\"");
        if (egk.Success && Kvnr.IsValid(egk.Groups[1].Value)) return egk.Groups[1].Value.ToUpperInvariant();
        foreach (Match m in Regex.Matches(payload, "[A-Za-z][0-9]{9}"))
            if (Kvnr.IsValid(m.Value)) return m.Value.ToUpperInvariant();
        return null;
    }

    // ---- decode core (zxing-cpp) ----

    // AllReadable: try every symbology. Downstream trusts only STRUCTURAL parses (BfbBarcode/BmpBarcode) for identity
    // and content, so any 1D false positives are inert. (BarcodeFormat exposes no bitwise-or, so a narrower set can't
    // be built as one value; AllReadable is a safe superset and the engine is fast.)
    private static BarcodeReader NewReader() => new()
    {
        Formats = BarcodeFormat.AllReadable,
        TryHarder = true,
        TryRotate = true,
        TryInvert = true,
        TryDownscale = true,
    };

    // BMP DataMatrix / Muster-12 PDF417 tend to sit in a corner or a right-side band. Whole-page decode catches the
    // clean majority; these crops (each upscaled) run ONLY as a fallback when whole-page found no boundary code.
    private static readonly (float x0, float y0, float x1, float y1)[] Regions =
    {
        (0.50f, 0.60f, 1.00f, 1.00f), (0.00f, 0.60f, 0.50f, 1.00f),
        (0.50f, 0.00f, 1.00f, 0.45f), (0.00f, 0.00f, 0.50f, 0.45f),
        (0.66f, 0.74f, 1.00f, 1.00f), (0.66f, 0.00f, 1.00f, 0.30f),
        (0.00f, 0.74f, 0.34f, 1.00f), (0.00f, 0.00f, 0.34f, 0.26f),
    };

    private static IEnumerable<(string Format, string Text, int Bytes)> DecodePage(SKBitmap page)
    {
        var reader = NewReader();
        var found = new Dictionary<string, (string Format, string Text, int Bytes)>();
        void Collect(SKBitmap img)
        {
            var (gray, w, h) = ToLuminance(img);
            // GEOMETRY INVARIANT: the IntPtr ImageView ctor has no length parameter, so the native side can no longer
            // bounds-check the buffer (the ReadOnlySpan ctor did, via ZXing_ImageView_new_checked(…, data.Length, …),
            // which THREW on a mismatch). Restore that check here — w*h must equal the Lum buffer length — before
            // handing a raw pointer to native code. Throw (not skip): it matches the behaviour the swap removed, cannot
            // occur on valid input (ToLuminance sizes gray = w*h; crops are ≥1px), so a hit is a genuine bug, and the
            // managed exception is caught by the pipeline's DeadLetter handler (a native AV would not be). Dims only.
            if (w <= 0 || h <= 0 || (long)w * h != gray.Length)
                throw new InvalidOperationException($"Barcode luminance geometry invariant violated: w={w} h={h} len={gray.Length}.");
            // MEMORY SAFETY (do not simplify): ZXingCpp's ImageView is a NON-OWNING view — its byte[]/ReadOnlySpan ctor
            // passes `ref MemoryMarshal.GetReference(data)` to the native side and retains ONLY that raw pointer, with no
            // pin and no managed reference (verified by decompiling ZXingCpp 0.5.2). So under LOH-compacting server GC a
            // `gray` that goes dead after the ctor can be MOVED or FREED during ZXing_ReadBarcodes — an access violation
            // that kills the process, OR (worse) a live buffer relocated into gray's vacated slot yields a clean decode
            // of the WRONG image: valid ECC, wrong KVNR, no error. Both were reproduced deterministically in a
            // dedicated stress harness (not part of the public repo); pinning eliminated both. Fix: pin `gray` for
            // the entire native read via the IntPtr ctor over pinned memory, and keep the view + reader rooted across it
            // (ImageView has a finalizer and is NOT IDisposable; ReaderOptions/BarcodeReader also have finalizers).
            var handle = GCHandle.Alloc(gray, GCHandleType.Pinned);
            try
            {
                var iv = new ImageView(handle.AddrOfPinnedObject(), w, h, ImageFormat.Lum, w, 1);
                var results = reader.From(iv); // Read is eager: the whole native decode completes inside this call
                GC.KeepAlive(iv);
                GC.KeepAlive(reader);
                foreach (var b in results)
                {
                    if (!b.IsValid) continue;
                    var fmt = b.Format.ToString();
                    var text = b.Text ?? "";
                    var bytes = b.Bytes?.Length ?? System.Text.Encoding.UTF8.GetByteCount(text);
                    var key = fmt + "|" + (text.Length > 0 ? text : Convert.ToBase64String(b.Bytes ?? Array.Empty<byte>()));
                    found.TryAdd(key, (fmt, text, bytes));
                }
            }
            finally { handle.Free(); }
        }
        Collect(page); // whole page — zxing-cpp does rotation / inversion / multi-scale itself
        if (!found.Values.Any(v => IsBoundary(v.Format, v.Bytes)))
            foreach (var rg in Regions)
            {
                using var crop = CropScale(page, rg.x0, rg.y0, rg.x1, rg.y1, 3);
                if (crop is not null) Collect(crop);
            }
        return found.Values;
    }

    // A "boundary" code marks a document start: a long DataMatrix (BMP medication plan) or a PDF417 (Muster-12 BFB).
    private static bool IsBoundary(string format, int bytes) =>
        (format.Contains("Matrix", StringComparison.Ordinal) && bytes > 80) || format.Contains("PDF417", StringComparison.Ordinal);

    private static SKBitmap? CropScale(SKBitmap src, float fx0, float fy0, float fx1, float fy1, int scale)
    {
        int x0 = Math.Clamp((int)(fx0 * src.Width), 0, src.Width - 1);
        int y0 = Math.Clamp((int)(fy0 * src.Height), 0, src.Height - 1);
        int x1 = Math.Clamp((int)(fx1 * src.Width), 1, src.Width);
        int y1 = Math.Clamp((int)(fy1 * src.Height), 1, src.Height);
        if (x1 <= x0 || y1 <= y0) return null;
        using var sub = new SKBitmap();
        if (!src.ExtractSubset(sub, new SKRectI(x0, y0, x1, y1))) return null;
        var info = new SKImageInfo((x1 - x0) * scale, (y1 - y0) * scale, SKColorType.Bgra8888, SKAlphaType.Premul);
        return sub.Resize(info, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
    }

    // 8-bit grayscale (Lum) buffer for zxing-cpp's ImageView (rowStride = width, pixStride = 1).
    private static (byte[] gray, int width, int height) ToLuminance(SKBitmap bmp)
    {
        SKBitmap bgra = bmp.ColorType == SKColorType.Bgra8888 ? bmp : bmp.Copy(SKColorType.Bgra8888);
        var px = bgra.GetPixelSpan();
        int n = bgra.Width * bgra.Height;
        var gray = new byte[n];
        for (int i = 0; i < n; i++) { int o = i * 4; gray[i] = (byte)((px[o] + px[o + 1] + px[o + 2]) / 3); }
        int w = bgra.Width, h = bgra.Height;
        if (!ReferenceEquals(bgra, bmp)) bgra.Dispose();
        return (gray, w, h);
    }
}
