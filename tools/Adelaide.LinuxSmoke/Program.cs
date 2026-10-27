using System.Runtime.InteropServices;
using System.Text;
using Adelaide.Ai;
using Adelaide.Audit;
using Adelaide.Core;

// ─────────────────────────────────────────────────────────────────────────────
// Linux native-load smoke. A Windows `dotnet publish -r linux-x64` proves the native .so files were COPIED into the
// output — it cannot prove they dlopen + execute on Linux (the Windows box never dlopens them). This exercises the
// actual code paths so a CI run ON THE DEPLOY IMAGE earns "proven". It touches ALL THREE bundled natives:
//   • libpdfium.so + libSkiaSharp.so — PDF render → SKBitmap → JPEG encode (also the libfontconfig trip-wire)
//   • libe_sqlite3.so               — open the audit store, append, VerifyChainAsync (the audit native — easy to forget)
// Exit 0 = all three loaded and ran; exit 1 = a failure (printed). Run it on mcr.microsoft.com/dotnet/aspnet:10.0
// (the image you ship) for the authoritative result.
// ─────────────────────────────────────────────────────────────────────────────

int rc = 0;
Console.WriteLine("== Adelaide Linux native smoke ==");
Console.WriteLine($"runtime: {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");

// 1) Imaging — dlopens libpdfium.so + libSkiaSharp.so (and trips libfontconfig if .NoDependencies needs it).
try
{
    var pdf = Path.Combine(Path.GetTempPath(), $"smoke-{Guid.NewGuid():N}.pdf");
    File.WriteAllBytes(pdf, MinimalPdf());
    var pages = PdfRenderer.Render(pdf, dpi: 72, maxPages: 1);
    if (pages.Count == 0 || pages[0].Width <= 0) throw new InvalidOperationException("PDFium returned no usable page");
    var jpeg = PdfRenderer.Jpeg(pages[0]);
    if (jpeg.Length == 0) throw new InvalidOperationException("SkiaSharp produced no JPEG bytes");
    foreach (var p in pages) p.Dispose();
    try { File.Delete(pdf); } catch { }
    Console.WriteLine($"  imaging  OK   (libpdfium + libSkiaSharp): {pages.Count} page, {jpeg.Length} JPEG bytes");
}
catch (Exception ex)
{
    rc = 1;
    Console.WriteLine($"  imaging  FAIL (libpdfium/libSkiaSharp/libfontconfig): {ex.GetType().Name}: {ex.Message}");
}

// 2) SQLite + audit chain — dlopens libe_sqlite3.so and exercises the hash chain (the component we protect hardest).
try
{
    var db = Path.Combine(Path.GetTempPath(), $"smoke-{Guid.NewGuid():N}.db");
    using (var store = new SqliteAuditStore(db))
    {
        await store.AppendAsync(new AuditEntry("smoke-doc", "SmokeTest", "ci"));
        var v = await store.VerifyChainAsync();
        if (!v.Intact) throw new InvalidOperationException($"hash chain not intact (firstBroken={v.FirstBrokenSeq})");
        Console.WriteLine($"  sqlite   OK   (libe_sqlite3): chain intact, {v.EventCount} event(s)");
    }
    foreach (var f in new[] { db, db + "-wal", db + "-shm" }) { try { File.Delete(f); } catch { } }
}
catch (Exception ex)
{
    rc = 1;
    Console.WriteLine($"  sqlite   FAIL (libe_sqlite3): {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine(rc == 0 ? "RESULT: all three Linux natives loaded and executed" : "RESULT: FAILURE — see above");
return rc;

// A 1-page blank PDF with a correct, programmatically-computed xref (ASCII, so char count == byte offset) — always
// valid for PDFium without hand-counting offsets.
static byte[] MinimalPdf()
{
    string[] objs =
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 120 120] >>",
    };
    var sb = new StringBuilder().Append("%PDF-1.4\n");
    var off = new int[objs.Length + 1];
    for (int i = 0; i < objs.Length; i++)
    {
        off[i + 1] = sb.Length;
        sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
    }
    int xref = sb.Length;
    sb.Append("xref\n0 ").Append(objs.Length + 1).Append('\n').Append("0000000000 65535 f \n");
    for (int i = 1; i <= objs.Length; i++) sb.Append(off[i].ToString("D10")).Append(" 00000 n \n");
    sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
    return Encoding.ASCII.GetBytes(sb.ToString());
}
