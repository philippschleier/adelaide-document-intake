using System.Net;
using System.Text;
using Adelaide.Core;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.SharePoint.Tests;

// Locks the strict-vs-best-effort split in SharePointSink.FileAsync (the metadata PATCH):
//   * MEDICAL (target.RequiresPatient == true) writes required columns STRICTLY — a failed /fields PATCH PROPAGATES, so
//     the doc stays in "Filing" for reconcile (a required patient/field column must land). This test pins the else-branch
//     so a future refactor can't silently soften the medical path.
//   * BUSINESS (target.RequiresPatient == false) writes disposable holding metadata BEST-EFFORT — the SAME failure is
//     swallowed and the filing COMPLETES, because the Zielbereich label's system of record is the audit ledger, not the
//     (disposable, throwaway-site) column.
// Drives the REAL FileAsync through a stub Graph handler (same pattern as SupersedeDeleteTests): the whole flow succeeds
// EXCEPT the /fields PATCH, which 400s.
public sealed class FileAsyncFieldStrictnessTests : IDisposable
{
    private readonly string _pdf = Path.Combine(Path.GetTempPath(), $"adelaide-fa-{Guid.NewGuid():N}.pdf");
    public FileAsyncFieldStrictnessTests() => File.WriteAllBytes(_pdf, "%PDF-1.4 test"u8.ToArray());
    public void Dispose() { try { File.Delete(_pdf); } catch { /* best effort — temp file */ } }

    private static SharePointOptions Options() => new("tenant", "client", "secret", "host,site,web");
    private static SharePointSink Sink(StubHandler h) =>
        new(new HttpClient(h), Options(), new FakeCredential(), new FakePdfTextLayer(), NullLogger<SharePointSink>.Instance);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Succeeds through drive-resolve → upload → sharepointIds → (empty) content types, then 400s the /fields PATCH.
    private static StubHandler FailFieldsHandler() => new(req =>
    {
        var url = req.RequestUri!.ToString();
        if (req.Method == HttpMethod.Put && url.Contains("/content"))
            return Json("{\"id\":\"item-1\"}");                                             // upload → driveItem id
        if (req.Method == HttpMethod.Get && url.Contains("/items/item-1"))
            return Json("{\"id\":\"item-1\",\"name\":\"file.pdf\",\"sharepointIds\":{\"listId\":\"list-1\",\"listItemId\":\"7\"}}");
        if (req.Method == HttpMethod.Get && url.Contains("/contentTypes"))
            return Json("{\"value\":[]}");                                                   // no CT match → SetContentType skips (no throw)
        if (req.Method == HttpMethod.Get && url.Contains("/drives"))
            return Json("{\"value\":[{\"id\":\"drv1\",\"name\":\"Patient Records\"},{\"id\":\"drv1\",\"name\":\"Business – Eingang\"}]}");
        if (req.Method == HttpMethod.Patch && url.EndsWith("/fields", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("column not found") };
        return Json("{}");                                                                   // any other request
    });

    [Fact]
    public async Task Medical_field_PATCH_failure_THROWS_so_the_doc_stays_in_Filing()
    {
        // target == null → the sink's MedicalTarget (RequiresPatient == true) → strict SetFieldsAsync: the 400 propagates.
        var spec = new FilingSpec("file.pdf", "Verordnung",
            new Dictionary<string, object?> { ["DocumentDate"] = "2026-05-01T00:00:00Z", ["PatientLookupId"] = 7 }, "volltext", _pdf);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sink(FailFieldsHandler()).FileAsync(spec));
    }

    [Fact]
    public async Task Business_field_PATCH_failure_is_SWALLOWED_so_the_filing_completes()
    {
        // RequiresPatient == false → best-effort SetFieldsBestEffortAsync: the SAME 400 is logged and swallowed, the
        // filing completes and returns the driveItemId (the label survives in the audit ledger, not this column).
        var target = new FilingTarget("site-b", "Business – Eingang", "Document", RequiresPatient: false);
        var spec = SharePointSink.BuildBusinessSpec(
            new IncomingDocument("d", _pdf, SourceChannel.Api, "invoice.pdf"),
            new DocumentUnit("d", 1, 1, new Transcript("md", 1, 1.0, Array.Empty<DecodedBarcode>())),
            target, "Document", "2026-05-01", "HR");
        Assert.NotEmpty(spec.Fields);                                    // guard: there IS a field write to fail (DocumentDate + Zielbereich)
        var filing = await Sink(FailFieldsHandler()).FileAsync(spec);
        Assert.Equal("item-1", filing.DriveItemId);                     // completed despite the failing column write
        Assert.False(filing.PdfADeclared);                              // the stub PDF carries no XMP pdfaid — C10 marks it honestly
        Assert.False(filing.DiRendition);                               // no DI in this harness → original fallback
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private sealed class FakePdfTextLayer : IPdfTextLayer
    {
        public byte[]? Rendition; // null = DI unavailable (the original-fallback path)
        public Task<byte[]?> MakeSearchableAsync(byte[] pdf, Adelaide.Core.CallContext? ctx = null, CancellationToken ct = default) => Task.FromResult(Rendition);
    }

    // C10 true path (verification finding: every other harness leaves searchable == null, so the "sniff the EXACT
    // uploaded bytes" property was untested — a mutant sniffing the ORIGINAL instead of the rendition survived).
    // The rendition declares PDF/A while the on-disk original does NOT: only sniffing the rendition yields true.
    [Fact]
    public async Task FileAsync_sniffs_the_uploaded_rendition_not_the_original()
    {
        var declaringRendition = Encoding.UTF8.GetBytes(
            """%PDF-1.4 stream <rdf:Description xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" pdfaid:part="1" pdfaid:conformance="A"/> endstream %%EOF""");
        var sink = new SharePointSink(new HttpClient(FailFieldsHandler()), Options(), new FakeCredential(),
            new FakePdfTextLayer { Rendition = declaringRendition }, NullLogger<SharePointSink>.Instance);
        var target = new FilingTarget("site-b", "Business – Eingang", "Document", RequiresPatient: false); // best-effort lane: the failing /fields PATCH doesn't abort
        var spec = new FilingSpec("file.pdf", "Document", new Dictionary<string, object?>(), "volltext", _pdf, target);

        var filing = await sink.FileAsync(spec);
        Assert.True(filing.PdfADeclared);   // true ONLY because the RENDITION declares — the original (_pdf) does not
        Assert.True(filing.DiRendition);
        Assert.Equal("item-1", filing.DriveItemId);
    }
}
