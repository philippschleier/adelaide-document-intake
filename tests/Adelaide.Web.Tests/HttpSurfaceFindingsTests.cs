using System.Net;

namespace Adelaide.Web.Tests;

/// <summary>
/// HTTP-surface audit findings, pinned as tests. See API-SURFACE-AUDIT.md.
///
/// NAMING CONTRACT for whoever runs the build:
///   • <c>OPEN_</c>  — pins a CURRENT DEFECT. It asserts the behavior that exists today, so it PASSES on an unfixed
///                     tree. When the defect is fixed this test FAILS, and that failure is the signal to delete or
///                     invert it. Each one names the fix that should break it.
///   • <c>REGRESSION_</c> — pins behavior that is CORRECT today and must stay correct. A failure here is a real
///                     regression.
/// Nothing here is marked Skip: a self-skipping test that still reports "passed" proves nothing, which this repo's
/// own Phase 1 verification called out (§5.14).
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceFindingsTests(HttpSurfaceFixture fx)
{
    // ---------------------------------------------------------------------------------------------------------
    // F1 — IsValidDocId accepts a TRAILING NEWLINE.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// OPEN. <c>IsValidDocId</c> is <c>Regex.IsMatch(id, "^[a-zA-Z0-9]+(-u[0-9]+)?$")</c>. In .NET, <c>$</c> matches
    /// at the end of the string OR IMMEDIATELY BEFORE A TRAILING <c>\n</c> — so "aaaa00000001\n" is accepted as a
    /// valid document id by every endpoint that calls this validator (/api/file, /api/page, /api/review, /api/split,
    /// /api/retry, /api/reprocess, /api/capture/erase, the duplicate routes, and the rotation route).
    ///
    /// Probed on <c>/api/file</c>, which uses this validator. The distinguishing evidence is the CONTRAST inside this
    /// one test: a traversal attempt and a non-ASCII digit are both rejected with 400, while the newline variant sails
    /// past the validator and is answered by the lookup (404). That difference is what proves the newline is passing
    /// VALIDATION rather than being rejected somewhere else.
    ///
    /// The newer <c>/api/documents/{id}/history</c> route does NOT use this validator — it applies the stricter
    /// <c>IsLedgerDocId</c> (<c>^[0-9a-f]{12}(-u[0-9]+)?$</c>, length-bounded), which rejects the newline correctly.
    /// So the fix already exists in the codebase; it is simply not the one the other routes use.
    ///
    /// Fix: anchor with <c>\A...\z</c> and add a length bound, or route the legacy endpoints through IsLedgerDocId.
    /// When fixed, the newline case returns 400 and this test fails.
    /// </summary>
    [Theory]
    [InlineData("aaaa00000001%0A", HttpStatusCode.NotFound)]    // trailing \n  — ACCEPTED as valid (the defect)
    [InlineData("aaaa00000001%0D%0A", HttpStatusCode.BadRequest)] // \r\n       — rejected (\r is not matched)
    [InlineData("..%2F..%2Fetc", HttpStatusCode.BadRequest)]    // traversal    — rejected
    [InlineData("%D9%A1%D9%A2", HttpStatusCode.BadRequest)]     // Arabic-Indic digits — rejected (ASCII-only class)
    public async Task OPEN_F1_IsValidDocId_accepts_a_trailing_newline(string encodedId, HttpStatusCode expected)
    {
        var r = await fx.AsReviewer(HttpMethod.Get, $"/api/file?id={encodedId}");
        Assert.Equal(expected, r.StatusCode);
    }

    /// <summary>
    /// OPEN. The same input is judged by TWO different validators depending on which route it reaches: the strict
    /// <c>IsLedgerDocId</c> on /api/documents/{id}/history, and the loose <c>IsValidDocId</c> everywhere else. This
    /// test pins the divergence directly — one id, two routes, two answers.
    ///
    /// A mixed-case id and an over-long id are accepted by every legacy route and refused by the history route. That
    /// is the safe direction (the new route is the strict one), but it means the codebase contains a validator its
    /// author wrote precisely because the loose one "diverges by substrate" — Azure SQL's case-insensitive collation
    /// would match a mis-cased id where SQLite's BINARY would not — and the routes that actually WRITE still use the
    /// loose one. The reasoning that justified the strict validator applies at least as strongly to the write paths.
    /// </summary>
    [Theory]
    [InlineData("AAAA00000001")]      // mis-cased: CI collation on Azure SQL would match, SQLite BINARY would not
    [InlineData("aaaa000000011111111111111111")] // over-long
    public async Task OPEN_F1b_the_loose_and_strict_doc_id_validators_disagree_on_the_same_input(string id)
    {
        var legacy = await fx.AsReviewer(HttpMethod.Get, $"/api/file?id={id}");
        Assert.NotEqual(HttpStatusCode.BadRequest, legacy.StatusCode);   // accepted by IsValidDocId

        var strict = await fx.AsReviewer(HttpMethod.Get, $"/api/documents/{id}/history");
        Assert.Equal(HttpStatusCode.BadRequest, strict.StatusCode);      // refused by IsLedgerDocId
    }

    // ---------------------------------------------------------------------------------------------------------
    // F2 — rotation is gated on DOCUMENT STATE first, file existence second. (Inverted from the finding.)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// INVERTED 2026-08-12 (was OPEN_F2; the failure-composition B-4 fix gave the PUT the terminal guard its
    /// sibling routes have, and this failed on exactly its 200 assertion). The PUT used to be gated on
    /// <c>File.Exists</c> alone — no gate at all under the purge-OFF posture, where a FILED document keeps its
    /// arrival PDF and was freely re-orientable by any Reviewer (and the saved angle was C-1's reach onto terminal
    /// documents). The guard is now the STATE: <c>DocumentStates.IsSettled</c> answers the standard per-state 409
    /// BEFORE the file check, so the old control (a terminal doc without its file 404s) deliberately flips to 409
    /// too — the state outranks the file, which is the entire point. The file gate still exists behind it, pinned
    /// via an id with no ledger row and no file.
    /// </summary>
    [Fact]
    public async Task REGRESSION_F2_rotation_refuses_a_terminal_document_by_state_before_the_file_check()
    {
        var filed = await fx.AsReviewer(HttpMethod.Put, $"/api/documents/{HttpSurfaceFixture.Filed}/rotation",
            csrf: true, body: HttpSurfaceFixture.Json("""{"angle":90}"""));
        Assert.Equal(HttpStatusCode.Conflict, filed.StatusCode);   // settled → refused, even though its PDF survives (purge-OFF)

        // A terminal doc WITHOUT its file is refused by state too — the state gate runs first.
        var noFile = await fx.AsReviewer(HttpMethod.Put, $"/api/documents/{HttpSurfaceFixture.Discarded}/rotation",
            csrf: true, body: HttpSurfaceFixture.Json("""{"angle":90}"""));
        Assert.Equal(HttpStatusCode.Conflict, noFile.StatusCode);

        // The file gate still exists behind the state gate: no ledger row, no file → 404.
        var unknown = await fx.AsReviewer(HttpMethod.Put, $"/api/documents/{HttpSurfaceFixture.Unknown}/rotation",
            csrf: true, body: HttpSurfaceFixture.Json("""{"angle":90}"""));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------------------
    // F3 — filter ordering: authorization runs BEFORE the CSRF endpoint filter. (Correct today.)
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// REGRESSION. Endpoint filters run inside the endpoint, after the authorization middleware, so an unauthenticated
    /// caller is challenged (401) before the CSRF filter can turn the request into a 403. That ordering is the right
    /// one — it means the CSRF filter never becomes an oracle that distinguishes "no session" from "no header" — and
    /// it is worth pinning because reordering CSRF into middleware would silently change it.
    /// </summary>
    [Fact]
    public async Task REGRESSION_F3_authorization_precedes_the_csrf_filter()
    {
        var anon = await fx.Anonymous(HttpMethod.Post, "/api/review");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var authedNoCsrf = await fx.AsReviewer(HttpMethod.Post, "/api/review");
        Assert.Equal(HttpStatusCode.Forbidden, authedNoCsrf.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------------------
    // F4 — the Admin-only routes reject a Reviewer, and the fallback accepts Admin alone.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// REGRESSION. Two directions of the role split, because both can break independently:
    /// a Reviewer must not reach an Admin route (403), and an Admin WITHOUT the Reviewer role must still reach the
    /// ordinary reviewer surface — <c>RequireRole(Reviewer, Admin)</c> is an OR, and a handler that additionally
    /// assumed "Reviewer" would lock out administrators.
    /// </summary>
    [Fact]
    public async Task REGRESSION_F4_admin_routes_refuse_a_reviewer_and_the_fallback_accepts_admin_alone()
    {
        foreach (var adminRoute in new[] { "/api/audit/verify" })
            Assert.Equal(HttpStatusCode.Forbidden, (await fx.AsReviewer(HttpMethod.Get, adminRoute)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await fx.AsAdmin(HttpMethod.Get, "/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.AsAdmin(HttpMethod.Get, "/api/audit/verify")).StatusCode);
    }

    /// <summary>
    /// REGRESSION. Every /api route must refuse an anonymous caller. This is the fallback policy's whole job, and the
    /// test enumerates the surface so that ADDING a route without an annotation cannot quietly create an open one.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/health")]
    [InlineData("GET", "/api/documents")]
    [InlineData("GET", "/api/documents?scope=all")]
    [InlineData("GET", "/api/stats")]
    [InlineData("GET", "/api/patients?q=x")]
    [InlineData("GET", "/api/review?id=x")]
    [InlineData("GET", "/api/audit/verify")]
    [InlineData("GET", "/api/pagecount?id=aaaa1111")]
    [InlineData("GET", "/api/compare?id=aaaa1111&other=aaaa2222")]
    [InlineData("GET", "/api/diff?a=aaaa1111&b=aaaa2222")]
    [InlineData("GET", "/api/file?id=aaaa1111")]
    [InlineData("GET", "/api/documents/aaaa1111/history")]
    [InlineData("POST", "/api/upload")]
    [InlineData("POST", "/api/retry?id=aaaa1111")]
    [InlineData("POST", "/api/reprocess?id=aaaa1111")]
    [InlineData("POST", "/api/capture/erase?id=aaaa1111&scope=content")]
    [InlineData("POST", "/api/duplicate-discard?id=aaaa1111")]
    [InlineData("POST", "/api/duplicate-process?id=aaaa1111")]
    [InlineData("POST", "/api/review")]
    [InlineData("POST", "/api/split")]
    [InlineData("PUT", "/api/documents/aaaa1111/rotation")]
    public async Task REGRESSION_F5_every_api_route_refuses_an_anonymous_caller(string method, string url)
    {
        var r = await fx.Anonymous(new HttpMethod(method), url);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    /// <summary>
    /// REGRESSION. The anonymous exemptions are exactly /health and /version — nothing else. Both must stay free of
    /// document-derived content: they are the only surface reachable without a role.
    /// </summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/version")]
    public async Task REGRESSION_F6_the_anonymous_endpoints_are_reachable_and_carry_no_document_data(string url)
    {
        var r = await fx.Anonymous(HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(HttpSurfaceFixture.Filed, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inbox", body, StringComparison.OrdinalIgnoreCase);
    }
}
