using System.Net;
using System.Net.Http.Headers;

namespace Adelaide.Web.Tests;

/// <summary>
/// The intake ceilings on POST /api/upload. Added late, after wave 2 pointed out that the harness justified being
/// out-of-process on Kestrel's body limits and then never measured one — the audit's largest unforced omission.
///
/// Three ceilings are declared in the composition root: MaxUploadRequestBytes (60 MB, enforced by Kestrel before the
/// body is buffered), MaxUploadFileBytes (25 MB per file) and MaxFilesPerRequest (20). The last two are handler-side
/// and are what a real scanner batch actually hits; they are measured here. The 60 MB Kestrel ceiling is deliberately
/// NOT measured — pushing 60 MB over loopback per run is a poor trade for a limit whose enforcement is the
/// framework's, not this code's — and that omission is recorded in API-SURFACE-AUDIT.md rather than left implicit.
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceUploadLimitTests(HttpSurfaceFixture fx)
{
    private static readonly byte[] TinyPdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic\n");

    private static MultipartFormDataContent Files(params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(part, "files", name);
        }
        return form;
    }

    /// <summary>REGRESSION. More than 20 files in one request is a clean 400 that names the ceiling — not a 413, not
    /// a 500, and not a partial acceptance.</summary>
    [Fact]
    public async Task REGRESSION_U1_more_than_twenty_files_is_a_clean_400()
    {
        var files = Enumerable.Range(0, 21).Select(i => ($"synthetic-{i}.pdf", TinyPdf)).ToArray();
        var r = await fx.AsReviewer(HttpMethod.Post, "/api/upload", csrf: true, body: Files(files));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("20", body, StringComparison.Ordinal);          // the ceiling is stated to the caller
        Assert.DoesNotContain("synthetic-", body, StringComparison.Ordinal); // ...but no filename is echoed back
    }

    /// <summary>
    /// REGRESSION. A single file over the 25 MB per-file ceiling is a clean 400 whose message states the limit and —
    /// deliberately — does NOT contain the filename, because an arrival filename is patient name plus insurance
    /// number. That omission is an explicit comment at the check; this pins it.
    /// </summary>
    [Fact]
    public async Task REGRESSION_U2_an_oversize_file_is_a_400_that_never_echoes_the_filename()
    {
        var oversize = new byte[26 * 1024 * 1024];
        TinyPdf.CopyTo(oversize, 0); // valid magic bytes, so the size check is what rejects it

        var r = await fx.AsReviewer(HttpMethod.Post, "/api/upload", csrf: true,
            body: Files(("SYNTHETIC-FILENAME-MARKER.pdf", oversize)));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SYNTHETIC-FILENAME-MARKER", body, StringComparison.Ordinal);
    }

    /// <summary>REGRESSION. A file whose bytes are not a PDF is refused at the trust boundary — before anything is
    /// written to the inbox or handed to the native renderer.</summary>
    [Fact]
    public async Task REGRESSION_U3_a_non_pdf_payload_is_refused()
    {
        var notAPdf = System.Text.Encoding.ASCII.GetBytes("MZ this is not a pdf at all");
        var r = await fx.AsReviewer(HttpMethod.Post, "/api/upload", csrf: true,
            body: Files(("synthetic.pdf", notAPdf)));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    /// <summary>
    /// OPEN. A zero-length file is silently DROPPED: never size-checked, never signature-checked, never written,
    /// never recorded in the ledger, and never added to the response's `jobs` array. The request returns 200 with an
    /// empty array, so a client that uploaded one file gets a success carrying no evidence that nothing happened.
    /// The other two intake paths reject or dead-letter such a file.
    ///
    /// Fix: report it like every other rejection. When fixed, this returns 400 (or a 200 naming the skipped file)
    /// and this test fails.
    /// </summary>
    [Fact]
    public async Task OPEN_U4_a_zero_length_file_is_silently_dropped_with_a_200()
    {
        var r = await fx.AsReviewer(HttpMethod.Post, "/api/upload", csrf: true,
            body: Files(("synthetic-empty.pdf", Array.Empty<byte>())));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        // The response carries an empty jobs array — a success that reports nothing accepted and nothing refused.
        Assert.DoesNotContain("synthetic-empty", body, StringComparison.Ordinal);
        Assert.Contains("[]", body.Replace(" ", ""), StringComparison.Ordinal);
    }
}
