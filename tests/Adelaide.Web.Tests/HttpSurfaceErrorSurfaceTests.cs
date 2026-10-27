using System.Net;

namespace Adelaide.Web.Tests;

/// <summary>
/// Error surfaces: what a response body may contain when something goes wrong. The standard is no stack traces, no
/// SQL text, no filesystem paths, and no patient-derived strings. Naming contract as in
/// <see cref="HttpSurfaceFindingsTests"/>.
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceErrorSurfaceTests(HttpSurfaceFixture fx)
{
    /// <summary>Markers of a leaked internal: stack frames, paths, SQL, and the process's own directories.</summary>
    private static void AssertNoInternals(string body)
    {
        foreach (var marker in new[]
                 {
                     "   at ",            // stack frame
                     "System.",           // CLR type names / stack
                     ".cs:line",          // source location
                     "C:\\",              // absolute Windows path
                     "SELECT ", "INSERT ", "SQLite", "Microsoft.Data",
                     "StackTrace", "InnerException",
                 })
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// REGRESSION — REPINNED 2026-08-12 to the reclassify-outbox contract; the third contract drift the staged
    /// annotations missed (they were written by reading Program.cs, not by running it — this one only surfaced on the
    /// first executed run after install). As originally written this pinned the PRE-outbox behaviour: a failing
    /// inline item delete surfaced as 502 with <c>SanitizeError(ex)</c> in the body. The write-ahead + reclassify
    /// outbox changed the endpoint's answer deliberately: the intent sidecar is on disk before the delete is
    /// attempted, an inline failure is SWALLOWED (reconcile re-drives the removal), and the route answers 200 with
    /// <c>removalPending: true</c> — "the reclassify is under way", not "the reclassify failed".
    ///
    /// What survives of the original intent: the response body must still be free of internals. The exception text
    /// no longer reaches the body at all on this path — structured JSON replaced the interpolated message, which is
    /// strictly stronger than sanitisation.
    /// </summary>
    [Fact]
    public async Task REGRESSION_a_failing_sharepoint_delete_is_swallowed_and_the_reclassify_reports_removal_pending()
    {
        var r = await fx.AsAdmin(HttpMethod.Post, $"/api/reprocess?id={HttpSurfaceFixture.Filed}", csrf: true);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadAsStringAsync();
        AssertNoInternals(body);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("sharePointItemRemoved").GetBoolean()); // the harness blocks Graph, so the inline delete cannot land
        Assert.True(doc.RootElement.GetProperty("removalPending").GetBoolean());         // …and the outbox owns the removal from here
    }

    /// <summary>
    /// REGRESSION. An UNHANDLED exception must not produce a developer exception page or any body at all. No
    /// UseExceptionHandler / UseDeveloperExceptionPage is registered, and the host runs outside Development, so the
    /// response is a bare 500. Safe for disclosure — and pinned here so that adding a diagnostics middleware later
    /// cannot silently start returning stack traces to reviewers.
    ///
    /// /api/patients is the vehicle: it calls Graph on every request, which the harness blocks at the socket.
    /// </summary>
    [Fact]
    public async Task REGRESSION_an_unhandled_exception_yields_a_bare_500_with_no_body()
    {
        var r = await fx.AsReviewer(HttpMethod.Get, "/api/patients?q=zzsynthetic");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);

        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(0, body.Length);
        AssertNoInternals(body);
    }

    /// <summary>
    /// REGRESSION. Malformed, wrong-shaped and wrong-typed requests must fail as a clean 4xx with an EMPTY body —
    /// never a 500, and never a framework diagnostic naming the expected type. This is the input-hygiene sweep: each
    /// case is a distinct way of confusing the JSON binder or the query binder.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/review", "", "application/json", 400)]
    [InlineData("POST", "/api/review", "null", "application/json", 400)]
    [InlineData("POST", "/api/review", "[1,2]", "application/json", 400)]
    [InlineData("POST", "/api/review", "{\"documentId\":\"a\"}", "text/plain", 415)]
    [InlineData("POST", "/api/review", "{\"documentId\":\"aaaa1111\",\"fields\":\"notanobject\"}", "application/json", 400)]
    [InlineData("POST", "/api/split", "{\"documentId\":\"aaaa1111\",\"ranges\":5}", "application/json", 400)]
    [InlineData("PUT", "/api/documents/aaaa1111/rotation", "{\"angle\":\"ninety\"}", "application/json", 400)]
    public async Task REGRESSION_malformed_requests_are_clean_4xx_with_no_body(string method, string url, string payload, string contentType, int expected)
    {
        var content = new StringContent(payload, System.Text.Encoding.UTF8, contentType);
        var r = await fx.AsReviewer(new HttpMethod(method), url, csrf: true, body: content);

        Assert.Equal(expected, (int)r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(0, body.Length);
    }

    /// <summary>
    /// REGRESSION. Query-parameter binding failures and out-of-range values on the render path are clean 400s, and the
    /// documented bounds hold: page is 1..400 and w is clamped rather than trusted. An absurd width must not reach the
    /// native renderer as-is.
    /// </summary>
    [Theory]
    [InlineData("/api/page?id=aaaa1111&page=notanumber", 400)]
    [InlineData("/api/page?id=aaaa1111&page=-1", 400)]
    [InlineData("/api/page?id=aaaa1111&page=0", 400)]
    [InlineData("/api/page?id=aaaa1111&page=401", 400)]
    [InlineData("/api/page?id=aaaa1111&page=1&w=notanumber", 400)]
    public async Task REGRESSION_render_query_parameters_are_bounded(string url, int expected)
    {
        var r = await fx.AsReviewer(HttpMethod.Get, url);
        Assert.Equal(expected, (int)r.StatusCode);
    }

    /// <summary>
    /// REGRESSION. A render failure is a 404, not a 500, and carries no body — the synthetic PDF in the fixture is
    /// deliberately unparseable, so this exercises the catch at Program.cs:686 which logs only the exception TYPE.
    /// </summary>
    [Fact]
    public async Task REGRESSION_an_unrenderable_pdf_is_a_404_not_a_500()
    {
        var r = await fx.AsReviewer(HttpMethod.Get, $"/api/page?id={HttpSurfaceFixture.NeedsReview}&page=1");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(0, (await r.Content.ReadAsStringAsync()).Length);
    }

    /// <summary>
    /// REGRESSION. The security response headers are on ordinary responses — success AND handled 4xx — including the
    /// anonymous endpoints. A header set that only applies to 200s is a common and invisible regression.
    /// </summary>
    [Theory]
    [InlineData("/health")]        // anonymous
    [InlineData("/api/documents")] // 200
    [InlineData("/api/documents/aaaa9999/history")] // handled 404
    public async Task REGRESSION_security_headers_are_present_on_success_and_handled_errors(string url)
    {
        var r = await fx.AsReviewer(HttpMethod.Get, url);
        Assert.Equal("nosniff", string.Join("", r.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", string.Join("", r.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", string.Join("", r.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("frame-ancestors 'none'", string.Join("", r.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }

    /// <summary>
    /// OPEN. An UNHANDLED exception loses EVERY security response header. The header middleware (Program.cs:290-310)
    /// sets them on the way IN, before <c>await next()</c>; when an exception propagates past it, the server discards
    /// the buffered response headers and emits a bare 500, so the 500 carries only Content-Length, Date and Server —
    /// no <c>X-Content-Type-Options</c>, no <c>Content-Security-Policy</c>, no <c>X-Frame-Options</c>,
    /// no <c>Strict-Transport-Security</c>.
    ///
    /// The comment above that middleware says the headers are "on EVERY response … set early so nothing escapes them".
    /// That is true of every response the pipeline produces and false of the one class it does not: the error path.
    ///
    /// Consequence is bounded and should not be overstated — the 500 body is EMPTY, so there is no content to sniff or
    /// frame, and TLS is enforced at the platform edge independently of HSTS. It matters as the general shape: the
    /// failure mode of the header middleware is silent omission on exactly the responses nobody inspects, and any
    /// future error handler that returns a BODY would inherit that omission.
    ///
    /// Fix: register UseExceptionHandler (which also replaces the bare 500 with a controlled response), or set the
    /// headers via an OnStarting callback so they survive the reset. When fixed, this test fails.
    ///
    /// INVERTED 2026-08-12 (was OPEN_E1): 8e66765 applied BOTH remedies prescribed above, and the first executed run
    /// after install confirmed the failure the OPEN_ contract promised. Now pinned as the fixed contract, with the two
    /// details the in-place annotation warned about honoured deliberately:
    ///
    ///  1. HSTS IS CONDITIONAL — `hstsEnabled = !app.Environment.IsDevelopment()` (never poison a localhost https
    ///     cache). Only the four unconditional headers are asserted; Strict-Transport-Security is deliberately NOT.
    ///  2. The OnStarting middleware is unconditional while UseExceptionHandler is Production-only, so the headers are
    ///     environment-independent but the body is not: assert the 500 and the headers, nothing about the body.
    /// </summary>
    [Fact]
    public async Task REGRESSION_an_unhandled_exception_response_still_carries_the_unconditional_security_headers()
    {
        var r = await fx.AsReviewer(HttpMethod.Get, "/api/patients?q=zzsynthetic");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);

        Assert.True(r.Headers.Contains("X-Content-Type-Options"));
        Assert.True(r.Headers.Contains("X-Frame-Options"));
        Assert.True(r.Headers.Contains("Content-Security-Policy"));
        Assert.True(r.Headers.Contains("Referrer-Policy"));
        // Strict-Transport-Security is deliberately unasserted: it is !IsDevelopment()-gated and this fixture does
        // not pin the child's environment tightly enough to own that branch.
    }
}
