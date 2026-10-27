using System.Net;
using System.Text;
using Adelaide.Core;
using Adelaide.SharePoint;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.SharePoint.Tests;

// The supersede delete-half seam: SharePointSink.TryDeleteByIdAsync deletes by driveItemId ONLY and folds a 404 into
// success (idempotent for reconcile re-drives), while a real error still throws. Same StubHandler pattern as
// GraphPatientDirectoryTests. The first GET resolves the drive; the DELETE is the method under test.
public class SupersedeDeleteTests
{
    private static SharePointOptions Options() => new("tenant", "client", "secret", "host,site,web");
    private const string Drives = "{\"value\":[{\"id\":\"drv1\",\"name\":\"Patient Records\"}]}";

    private static SharePointSink Sink(StubHandler h) =>
        new(new HttpClient(h), Options(), new FakeCredential(), new FakePdfTextLayer(), NullLogger<SharePointSink>.Instance);

    private static HttpResponseMessage DriveList() =>
        new(HttpStatusCode.OK) { Content = new StringContent(Drives, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Delete_by_id_treats_404_as_success() // already gone (e.g. a reconcile re-drive) ⇒ true, no throw
    {
        var h = new StubHandler(req => req.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : DriveList());

        Assert.True(await Sink(h).TryDeleteByIdAsync("drv-gone"));
    }

    [Fact]
    public async Task Delete_by_id_succeeds_on_204_and_targets_the_id_path() // no name lookup on the delete path
    {
        string? deletedUrl = null;
        var h = new StubHandler(req =>
        {
            if (req.Method == HttpMethod.Delete) { deletedUrl = req.RequestUri!.ToString(); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            return DriveList();
        });

        Assert.True(await Sink(h).TryDeleteByIdAsync("drv-123"));
        Assert.Contains("/drives/drv1/items/drv-123", deletedUrl); // deleted BY ID — never a /root:/{name} path
    }

    [Fact]
    public async Task Delete_by_id_throws_on_500() // a real error is NOT swallowed — only 404 is success
    {
        var h = new StubHandler(req => req.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }
            : DriveList());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sink(h).TryDeleteByIdAsync("drv-x"));
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
        public Task<byte[]?> MakeSearchableAsync(byte[] pdf, Adelaide.Core.CallContext? ctx = null, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }
}
