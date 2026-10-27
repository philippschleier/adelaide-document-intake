using System.Net;

namespace Adelaide.Web.Tests;

/// <summary>
/// Proves the audit harness itself before any finding is allowed to lean on it. If these fail, no other
/// HttpSurface* result means anything.
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceHarnessTests(HttpSurfaceFixture fx)
{
    [Fact]
    public async Task Harness_serves_the_real_app_on_an_isolated_port()
    {
        Assert.DoesNotContain(fx.Port, HttpSurfaceFixture.ForbiddenPorts);
        var r = await fx.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Harness_cannot_reach_the_network__a_Graph_backed_route_dies_at_the_socket()
    {
        // /api/patients is the only route that calls Graph on every request. Under ADELAIDE_TEST_MODE it must fail
        // at the blocked handler, and the child's log must say so — that is the PROOF of the no-outbound guarantee,
        // not an inference from "the test passed".
        var r = await fx.AsReviewer(HttpMethod.Get, "/api/patients?q=zzsynthetic");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Contains("outbound call blocked", fx.ProcessLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_seeded_a_document_in_every_state_it_claims_to_have_seeded()
    {
        var r = await fx.AsReviewer(HttpMethod.Get, "/api/documents?scope=all");
        r.EnsureSuccessStatusCode();
        var body = await r.Content.ReadAsStringAsync();
        foreach (var id in new[]
                 {
                     HttpSurfaceFixture.Filed, HttpSurfaceFixture.Discarded, HttpSurfaceFixture.Superseded,
                     HttpSurfaceFixture.Segmented, HttpSurfaceFixture.SeededHistory, HttpSurfaceFixture.DeadLetter,
                     HttpSurfaceFixture.NeedsReview, HttpSurfaceFixture.Filing, HttpSurfaceFixture.DuplicateSuspect,
                 })
            Assert.Contains(id, body, StringComparison.Ordinal);
        Assert.DoesNotContain(HttpSurfaceFixture.Unknown, body, StringComparison.Ordinal);
    }
}
