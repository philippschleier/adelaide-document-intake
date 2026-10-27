using System.Net;
using System.Text;
using Adelaide.Core;
using Adelaide.SharePoint;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.SharePoint.Tests;

// Stage III: filed-PDF retrieval is BY driveItemId ONLY. DownloadFiledPdfAsync fetches /drives/{driveId}/items/{id}/content
// (never a /root:/{name} path); a 404 → null (deleted-original). Same StubHandler pattern as GraphPatientDirectoryTests.
public class FiledPdfByIdTests
{
    private static SharePointOptions Options() => new("tenant", "client", "secret", "host,site,web");
    private const string Drives = "{\"value\":[{\"id\":\"drv1\",\"name\":\"Patient Records\"}]}";
    private static SharePointSink Sink(StubHandler h) =>
        new(new HttpClient(h), Options(), new FakeCredential(), new FakePdfTextLayer(), NullLogger<SharePointSink>.Instance);
    private static HttpResponseMessage DriveList() =>
        new(HttpStatusCode.OK) { Content = new StringContent(Drives, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Fetches_by_id_from_the_items_content_path()
    {
        string? fetched = null;
        var h = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/content")) { fetched = url; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("%PDF-1.4"u8.ToArray()) }; }
            return DriveList();
        });

        var bytes = await Sink(h).DownloadFiledPdfAsync("drv-123");

        Assert.NotNull(bytes);
        Assert.Contains("/drives/drv1/items/drv-123/content", fetched); // BY ID — never /root:/{name}
        Assert.DoesNotContain("/root:/", fetched);
    }

    [Fact]
    public async Task Id_404_returns_null_for_the_deleted_original_placeholder()
    {
        var h = new StubHandler(req => req.RequestUri!.ToString().Contains("/content")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : DriveList());

        Assert.Null(await Sink(h).DownloadFiledPdfAsync("drv-gone"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(responder(request));
    }
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }
    private sealed class FakePdfTextLayer : IPdfTextLayer
    {
        public Task<byte[]?> MakeSearchableAsync(byte[] pdf, Adelaide.Core.CallContext? ctx = null, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }
}
