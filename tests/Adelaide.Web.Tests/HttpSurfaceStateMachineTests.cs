using System.Net;

namespace Adelaide.Web.Tests;

/// <summary>
/// State-machine violations over HTTP. Naming contract as in <see cref="HttpSurfaceFindingsTests"/>:
/// OPEN_ pins a current defect, REGRESSION_ pins behavior that is correct and must stay correct.
///
/// Every terminal-state case here is reached with BOTH an arrival PDF and a pending-review sidecar on disk, because
/// /api/review and /api/split each return 404 before consulting state unless the sidecar exists. That is not an
/// artificial setup: it is the deployment's purge-OFF posture, in which a settled document keeps its inputs.
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceStateMachineTests(HttpSurfaceFixture fx)
{
    private static HttpContent BusinessReview(string id) => HttpSurfaceFixture.Json(
        $$"""{"documentId":"{{id}}","contentType":"Rechnung","patientListItemId":0,"fields":{},"isBusiness":true,"zielbereich":"HR"}""");

    private static HttpContent SplitInTwo(string id) => HttpSurfaceFixture.Json(
        $$"""{"documentId":"{{id}}","ranges":[[1,1],[2,2]]}""");

    /// <summary>
    /// REGRESSION. Re-filing a SETTLED document must be refused with a per-state 409, not silently accepted. This is
    /// the guard added by c7c73ac ("guard the whole terminal set on re-file and re-split"); before it, only the
    /// endpoint's own state was checked, so a Superseded or Discarded document could be re-filed from a stale tab,
    /// appending Reviewed+Filed OVER the terminal event and flipping the projection to a live-looking state.
    ///
    /// Discarded is deliberately absent: it has no inbox sidecar in the fixture, so it 404s before reaching the guard
    /// — covered by <see cref="REGRESSION_terminal_documents_without_a_sidecar_are_refused_earlier"/>.
    /// </summary>
    [Theory]
    [InlineData(HttpSurfaceFixture.Filed)]
    [InlineData(HttpSurfaceFixture.Segmented)]
    [InlineData(HttpSurfaceFixture.Superseded)]
    public async Task REGRESSION_review_refuses_a_terminal_document_with_409(string id)
    {
        var r = await fx.AsReviewer(HttpMethod.Post, "/api/review", csrf: true, body: BusinessReview(id));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    /// <summary>REGRESSION. Same guard on the split lane — a settled parent must not be split into fresh children
    /// over its terminal event. The terminal check runs BEFORE the PDF is parsed, which is why a synthetic
    /// (unparseable) PDF still exercises it: a 400 here would mean the guard had been reordered behind the parse.</summary>
    [Theory]
    [InlineData(HttpSurfaceFixture.Filed)]
    [InlineData(HttpSurfaceFixture.Segmented)]
    [InlineData(HttpSurfaceFixture.Superseded)]
    public async Task REGRESSION_split_refuses_a_terminal_document_with_409(string id)
    {
        var r = await fx.AsReviewer(HttpMethod.Post, "/api/split", csrf: true, body: SplitInTwo(id));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    /// <summary>REGRESSION. A terminal document whose inputs are GONE is refused earlier still (404, "no pending
    /// review"). Pinned so that the 409 tests above cannot be satisfied by an accidental 404.</summary>
    [Fact]
    public async Task REGRESSION_terminal_documents_without_a_sidecar_are_refused_earlier()
    {
        var r = await fx.AsReviewer(HttpMethod.Post, "/api/review", csrf: true,
            body: BusinessReview(HttpSurfaceFixture.Discarded));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    /// <summary>REGRESSION. The control for the whole file: a NON-terminal document with the same inputs must get
    /// PAST the terminal guard. It then fails downstream on the synthetic PDF (400, "not readable") — which proves the
    /// 409s above come from the state guard and not from something common to every request in this fixture.</summary>
    [Fact]
    public async Task REGRESSION_a_non_terminal_document_passes_the_terminal_guard()
    {
        var r = await fx.AsReviewer(HttpMethod.Post, "/api/split", csrf: true,
            body: SplitInTwo(HttpSurfaceFixture.NeedsReview));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.DoesNotContain("bereits", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>REGRESSION. Retry is for DeadLetter only; reprocess is for Filed only; the duplicate endpoints are for
    /// duplicate suspects only. Each wrong-state call must be a specific 4xx — never a 500, never a silent success.</summary>
    [Theory]
    [InlineData("/api/retry", HttpSurfaceFixture.Filed, HttpStatusCode.BadRequest)]
    [InlineData("/api/retry", HttpSurfaceFixture.NeedsReview, HttpStatusCode.BadRequest)]
    [InlineData("/api/retry", HttpSurfaceFixture.Unknown, HttpStatusCode.NotFound)]
    [InlineData("/api/reprocess", HttpSurfaceFixture.NeedsReview, HttpStatusCode.BadRequest)]
    [InlineData("/api/reprocess", HttpSurfaceFixture.Unknown, HttpStatusCode.NotFound)]
    [InlineData("/api/duplicate-discard", HttpSurfaceFixture.NeedsReview, HttpStatusCode.BadRequest)]
    [InlineData("/api/duplicate-discard", HttpSurfaceFixture.Filed, HttpStatusCode.BadRequest)]
    [InlineData("/api/duplicate-discard", HttpSurfaceFixture.Unknown, HttpStatusCode.NotFound)]
    [InlineData("/api/duplicate-process", HttpSurfaceFixture.NeedsReview, HttpStatusCode.BadRequest)]
    [InlineData("/api/duplicate-process", HttpSurfaceFixture.Filed, HttpStatusCode.BadRequest)]
    [InlineData("/api/duplicate-process", HttpSurfaceFixture.Unknown, HttpStatusCode.NotFound)]
    public async Task REGRESSION_wrong_state_transitions_fail_with_a_specific_status(string route, string id, HttpStatusCode expected)
    {
        var admin = route == "/api/reprocess"; // reprocess is Admin-only
        var r = admin
            ? await fx.AsAdmin(HttpMethod.Post, $"{route}?id={id}", csrf: true)
            : await fx.AsReviewer(HttpMethod.Post, $"{route}?id={id}", csrf: true);
        Assert.Equal(expected, r.StatusCode);
    }

    /// <summary>REGRESSION. /api/retry on a genuine DeadLetter document must be ACCEPTED — otherwise the wrong-state
    /// assertions above would pass for a route that rejects everything, which proves nothing about the guard.</summary>
    [Fact]
    public async Task REGRESSION_retry_accepts_the_one_state_it_is_for()
    {
        var r = await fx.AsReviewer(HttpMethod.Post, $"/api/retry?id={HttpSurfaceFixture.DeadLetter}", csrf: true);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    /// <summary>GET /api/review refuses SETTLED documents with the per-state 409, STATE FIRST — before the
    /// sidecar check, the order the rotation PUT set (B-4: the state outranks the file). Filed/Segmented/
    /// Superseded carry a surviving sidecar in this fixture (the purge-OFF posture), so a 409 there proves the
    /// state guard fired, not a missing file; Discarded and Purged carry NO sidecar, so their 409 proves the
    /// state is checked first (the old order answered 404). NeedsReview is the pass-through control.</summary>
    [Theory]
    [InlineData(HttpSurfaceFixture.Filed)]
    [InlineData(HttpSurfaceFixture.Segmented)]
    [InlineData(HttpSurfaceFixture.Superseded)]
    [InlineData(HttpSurfaceFixture.Discarded)]
    [InlineData(HttpSurfaceFixture.Purged)]
    public async Task GET_review_refuses_settled_documents_state_before_sidecar(string id)
    {
        var r = await fx.AsReviewer(HttpMethod.Get, $"/api/review?id={id}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task GET_review_still_serves_a_reviewable_document()
    {
        var r = await fx.AsReviewer(HttpMethod.Get, $"/api/review?id={HttpSurfaceFixture.NeedsReview}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}
