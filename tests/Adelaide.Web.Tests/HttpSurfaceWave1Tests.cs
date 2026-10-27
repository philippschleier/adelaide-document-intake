using System.Net;
using System.Net.Http.Headers;

namespace Adelaide.Web.Tests;

/// <summary>
/// Findings derived from the wave-1 source analysis and then REPRODUCED here. A hypothesis that could not be
/// reproduced is not in this file; it is recorded as refuted in API-SURFACE-AUDIT.md.
/// Naming contract as in <see cref="HttpSurfaceFindingsTests"/>.
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceWave1Tests(HttpSurfaceFixture fx)
{
    // =========================================================================================================
    // A1 — the authorization policy and the actor-stamping code disagree about what an identity is.
    // =========================================================================================================

    /// <summary>
    /// OPEN. The fallback and "Admin" policies require only a ROLE claim (Program.cs:236-243). The Easy Auth header
    /// parser promotes any claim of type "roles"/"role"/role_typ to ClaimTypes.Role (EasyAuth.cs:101-103). But
    /// <c>EasyAuthDefaults.Actor</c> (EasyAuth.cs:45-47) derives the audit actor from the <c>oid</c> claim, and
    /// <c>ActorOf</c> THROWS when it is absent (Program.cs:1515-1516).
    ///
    /// So a principal carrying a role and no oid is fully authorized — it reads every document and passes the
    /// Admin-only gate — and then crashes with an unhandled 500 the moment it tries to mutate anything.
    ///
    /// Two separable defects, and the read half is the one that matters:
    ///  • READ: an identity the app cannot attribute is nevertheless granted the entire read surface, including
    ///    /api/audit/verify. Authorization and attribution should not disagree.
    ///  • WRITE: the refusal is an unhandled exception (500), not a policy decision. It IS fail-closed — nothing is
    ///    written, ActorOf runs before any append — so this is a hygiene defect, not data loss.
    ///
    /// Fix: add the oid requirement to the policies (RequireClaim on the objectidentifier claim, or an assertion that
    /// EasyAuthDefaults.Actor is non-null), so an unattributable principal is refused at the gate with 403.
    /// When fixed, the reads below return 403 and this test fails.
    /// </summary>
    [Fact]
    public async Task OPEN_A1_a_principal_with_a_role_but_no_oid_is_fully_authorized_for_reads()
    {
        var roleOnly = HttpSurfaceFixture.PrincipalRolesOnly("Admin", "Reviewer");

        // The Admin-only chain verifier accepts it.
        var admin = await fx.Client.SendAsync(fx.Req(HttpMethod.Get, "/api/audit/verify", roleOnly));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        // ...and so does the full document list.
        var docs = await fx.Client.SendAsync(fx.Req(HttpMethod.Get, "/api/documents?scope=all", roleOnly));
        Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
    }

    /// <summary>
    /// OPEN. The write half of A1: the same unattributable principal reaches a handler and dies at ActorOf with an
    /// unhandled 500 instead of being refused by the policy. The document must EXIST, otherwise the handler's 404
    /// short-circuits before ActorOf is called — which is why this uses a seeded duplicate suspect.
    /// </summary>
    [Fact]
    public async Task OPEN_A2_the_same_principal_500s_on_a_mutation_instead_of_being_refused()
    {
        var roleOnly = HttpSurfaceFixture.PrincipalRolesOnly("Admin", "Reviewer");
        var r = await fx.Client.SendAsync(fx.Req(HttpMethod.Post,
            $"/api/duplicate-discard?id={HttpSurfaceFixture.DuplicateSuspect}", roleOnly, csrf: true));

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal(0, (await r.Content.ReadAsStringAsync()).Length); // fail-closed and non-disclosing

        // Control: the SAME request with an oid is not a 500 — so the 500 is about attribution, not about the route.
        var ok = await fx.Client.SendAsync(fx.Req(HttpMethod.Post,
            $"/api/duplicate-discard?id={HttpSurfaceFixture.DuplicateSuspect}",
            HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000a201", "Reviewer"), csrf: true));
        Assert.NotEqual(HttpStatusCode.InternalServerError, ok.StatusCode);
    }

    // =========================================================================================================
    // A3 — the split-unit id form is rejected on three of the four compare/diff sides.
    // =========================================================================================================

    /// <summary>
    /// OPEN. <c>IsValidDocId</c> was widened to accept the <c>-u{n}</c> split-unit suffix precisely so that a split
    /// child would stop 400-ing on the viewer routes — the comment at Program.cs:1462-1463 calls that out as "Fix F".
    /// But three id-bearing parameters never moved to it and still apply an alphanumeric-only check:
    /// <c>/api/compare</c>'s <c>other</c>, and BOTH of <c>/api/diff</c>'s <c>a</c> and <c>b</c>.
    ///
    /// Result: a reviewer can open a split unit on the left of a comparison but never on the right, and can never
    /// diff one at all. The asymmetry inside <c>/api/compare</c> is the proof — same route, same request, one
    /// parameter accepts the id form and the other rejects it.
    ///
    /// Consequence is a functional gap, not a security one: split children are exactly the documents most likely to
    /// need a duplicate comparison. Fix: route all four through IsValidDocId. When fixed, these return 404 (unknown
    /// document) rather than 400, and this test fails.
    /// </summary>
    [Theory]
    [InlineData("/api/compare?id=aaaa1111-u1&other=aaaa2222", HttpStatusCode.NotFound)]   // accepted (passes validation)
    [InlineData("/api/compare?id=aaaa1111&other=aaaa2222-u1", HttpStatusCode.BadRequest)] // REJECTED
    [InlineData("/api/diff?a=aaaa1111-u1&b=aaaa2222", HttpStatusCode.BadRequest)]         // REJECTED
    [InlineData("/api/diff?a=aaaa1111&b=aaaa2222-u1", HttpStatusCode.BadRequest)]         // REJECTED
    public async Task OPEN_A3_split_unit_ids_are_rejected_on_three_of_four_compare_and_diff_sides(string url, HttpStatusCode expected)
    {
        var r = await fx.AsReviewer(HttpMethod.Get, url);
        Assert.Equal(expected, r.StatusCode);
    }

    // =========================================================================================================
    // A4 — malformed collection elements in the split body.
    // =========================================================================================================

    /// <summary>
    /// OPEN. <c>/api/split</c> reads its ranges as <c>r.Length == 2</c> (Program.cs:1335) over
    /// <c>IReadOnlyList&lt;int[]&gt;</c>. A JSON <c>null</c> ELEMENT inside the array deserializes to a null
    /// <c>int[]</c>, and <c>r.Length</c> dereferences it — NullReferenceException, unhandled, 500.
    ///
    /// The neighbouring case is the interesting contrast, and it is asserted in the same test: a WRONG-ARITY element
    /// (<c>[1]</c>) is silently DROPPED by that same <c>Where</c>, so a malformed partition is not rejected but
    /// quietly reinterpreted. Here it then fails the coverage check, but the pattern — discard what you do not
    /// understand, then validate what is left — is how a partition can pass while not being the one submitted.
    ///
    /// Fix: validate the shape explicitly (reject null and non-pair elements with a 400) rather than filtering.
    /// When fixed, the null case returns 400 and this test fails.
    /// </summary>
    private Task<HttpResponseMessage> SplitSplittable(string rangesJson) =>
        fx.AsReviewer(HttpMethod.Post, "/api/split", csrf: true, body: HttpSurfaceFixture.Json(
            $$"""{"documentId":"{{HttpSurfaceFixture.Splittable}}","ranges":{{rangesJson}}}"""));

    [Fact]
    public async Task OPEN_A4_a_null_element_in_split_ranges_is_an_unhandled_500()
    {
        var nullElement = await SplitSplittable("[null,[2,4]]");
        Assert.Equal(HttpStatusCode.InternalServerError, nullElement.StatusCode);
        Assert.Equal(0, (await nullElement.Content.ReadAsStringAsync()).Length); // no disclosure, but no 400 either
    }

    /// <summary>
    /// REGRESSION. Every OTHER malformed partition of the 4-page document is a clean 400 — which is what makes the
    /// null element above a genuine outlier rather than a general absence of validation. The partition contract is
    /// "≥2 contiguous ranges covering 1..pageCount with no gaps or overlaps" (Program.cs:1334-1343).
    ///
    /// Note the wrong-arity case: <c>[[1],[2,4]]</c> is not rejected for being malformed — the element is silently
    /// DROPPED by the <c>Length == 2</c> filter, leaving one range, which then fails the "at least two parts" check.
    /// Same status, different reason, and the difference matters: the endpoint validates what survives its filter
    /// rather than what was submitted.
    /// </summary>
    [Theory]
    [InlineData("[]", "at least two parts")]
    [InlineData("[[1,4]]", "single range is not a split")]
    [InlineData("[[1],[2,4]]", "wrong arity silently dropped, then too few parts")]
    [InlineData("[[1,2],[4,4]]", "gap at page 3")]
    [InlineData("[[1,3],[3,4]]", "overlap on page 3")]
    [InlineData("[[0,2],[3,4]]", "page 0 is out of range")]
    [InlineData("[[1,2],[3,9]]", "last page beyond pageCount")]
    [InlineData("[[3,4],[1,2]]", "out of order")]
    [InlineData("[[2,1],[2,4]]", "reversed range")]
    [InlineData("[[1,2],[3,3]]", "does not cover page 4")]
    public async Task REGRESSION_A4b_every_other_malformed_split_partition_is_a_clean_400(string ranges, string why)
    {
        var r = await SplitSplittable(ranges);
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{why}: expected 400, got {(int)r.StatusCode}");
    }

    // =========================================================================================================
    // A5 — a multipart request with no boundary.
    // =========================================================================================================

    /// <summary>
    /// OPEN. <c>POST /api/upload</c> calls <c>ReadFormAsync</c> on a request it has already accepted as a form. A
    /// <c>multipart/form-data</c> Content-Type with NO <c>boundary</c> parameter is well-formed enough to be treated
    /// as a form and malformed enough that the form reader throws — unhandled, 500. Every other malformed-input case
    /// on this surface is a clean 4xx, so this is the outlier.
    ///
    /// Fix: guard <c>ReadFormAsync</c> and return 400. When fixed, this returns 400 and the test fails.
    /// </summary>
    [Fact]
    public async Task OPEN_A5_a_multipart_content_type_without_a_boundary_is_an_unhandled_500()
    {
        var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data"); // deliberately no boundary
        var r = await fx.AsReviewer(HttpMethod.Post, "/api/upload", csrf: true, body: content);
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
    }

    // =========================================================================================================
    // A6 — a CSRF-rejected request still consumes the victim's rate-limit permit.
    // =========================================================================================================

    /// <summary>
    /// OPEN. The middleware order is authorization → rate limiter → endpoint filters (Program.cs:381-383; the CSRF
    /// check is an endpoint filter, so it runs last). A cross-site request therefore CONSUMES a permit from the
    /// victim's per-identity "expensive" bucket before being refused with 403.
    ///
    /// Consequence: a cross-origin page that cannot successfully do anything can still exhaust a signed-in
    /// reviewer's 30/minute upload-and-retry budget with 30 no-op requests — the CSRF defence holds, but it is
    /// converted into a denial-of-service lever against the user it protects.
    ///
    /// Asserted here without the confound of a body, because body binding also precedes the CSRF filter: these
    /// requests carry no content, so the ONLY thing that rejects them is the CSRF filter.
    ///
    /// Fix: place the CSRF check before the limiter (middleware rather than endpoint filter), or do not count
    /// rejected requests. When fixed, the follow-up request is no longer 429 and this test fails.
    /// </summary>
    [Fact]
    public async Task OPEN_A6_a_csrf_rejected_request_consumes_the_victims_rate_limit_permit()
    {
        var victim = HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000cs01", "Reviewer", "Admin");
        var url = $"/api/retry?id={HttpSurfaceFixture.Unknown}";

        // 31 requests WITHOUT the CSRF header: every one is refused 403 by the endpoint filter...
        var refused = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
            refused.Add((await fx.Client.SendAsync(fx.Req(HttpMethod.Post, url, victim, csrf: false))).StatusCode);

        Assert.Contains(HttpStatusCode.Forbidden, refused);
        // ...and they still spend the budget, so the tail of the burst is throttled rather than CSRF-refused.
        Assert.Contains(HttpStatusCode.TooManyRequests, refused);

        // The victim's own legitimate request now fails too.
        var legitimate = await fx.Client.SendAsync(fx.Req(HttpMethod.Post, url, victim, csrf: true));
        Assert.Equal(HttpStatusCode.TooManyRequests, legitimate.StatusCode);
    }

    // =========================================================================================================
    // A7 — "Filing" is not terminal, so an in-doubt filing is re-enterable.
    // =========================================================================================================

    /// <summary>
    /// OPEN. <c>DocumentStates.Settled</c> is {Filed, Discarded, Superseded, Segmented, SeededHistory}. <c>Filing</c>
    /// — the in-doubt state a document sits in between "the upload started" and "the Filed event landed" — is not in
    /// it, and correctly so for the reconcile path, which must be able to complete such a document.
    ///
    /// But the same set is the re-entry guard on /api/review and /api/split. A document whose SharePoint upload is
    /// in flight (or whose filing crashed mid-way, leaving a filing sidecar for reconcile to finish) therefore passes
    /// the terminal guard and can be re-submitted or split while the original filing is still outstanding.
    ///
    /// This test pins the reachability, which is the part that is decidable over HTTP: a Filing document is NOT
    /// refused by the terminal guard — it proceeds past it and fails later, on the synthetic PDF. Whether a
    /// concurrent real upload then produces a double-file is the in-process claim's job (FilingClaims) and is not
    /// decidable from outside; that is recorded as UNVERIFIABLE in the report rather than asserted here.
    ///
    /// Fix: guard re-entry on "terminal OR Filing" (a distinct set from the query predicates' terminal set).
    /// When fixed, this returns 409 and the test fails.
    /// </summary>
    // =========================================================================================================
    // A8 — a split child cannot be split again: the grandchild ids it mints are rejected by every route.
    // =========================================================================================================

    /// <summary>
    /// OPEN. <c>UnitKeys.UnitId</c> composes a split child's id as <c>{parentId}-u{n}</c> (Model.cs:107), and
    /// <c>/api/split</c> uses it for every child (Program.cs:1382). <c>IsValidDocId</c> accepts AT MOST ONE such
    /// suffix (Program.cs:1464). So splitting a document that is ITSELF a split child mints ids of the form
    /// <c>X-u1-u2</c>, which every HTTP route rejects with 400.
    ///
    /// The grandchildren are still fully created before anything notices: their PDFs are written to the inbox, their
    /// Received events are appended to the append-only ledger, and their jobs are enqueued into the pipeline
    /// (Program.cs:1392-1421). They then process, occupy the review queue, and can never be opened, reviewed, filed,
    /// rotated or diffed — every route 400s on the id. They also cannot be erased through /api/capture/erase, which
    /// validates the same way.
    ///
    /// Splitting a child is a normal reviewer action: children re-enter the pipeline with their own review, so a
    /// child in NeedsReview has both the sidecar and the PDF that /api/split requires.
    ///
    /// This test pins the validator asymmetry (the decidable half) and the id composition. Fix: allow repeated
    /// suffixes in the regex, or flatten the child id. When fixed, the two-suffix case returns 404 and this fails.
    /// </summary>
    [Fact]
    public void OPEN_A8_split_composes_a_child_id_that_the_validator_would_reject_if_split_again()
    {
        var child = Adelaide.Core.UnitKeys.UnitId("aaaa1111", 1, 2);
        Assert.Equal("aaaa1111-u1", child);
        var grandchild = Adelaide.Core.UnitKeys.UnitId(child, 2, 2);
        Assert.Equal("aaaa1111-u1-u2", grandchild); // exactly the shape the routes reject
    }

    /// <summary>OPEN. The HTTP half of A8, asserted on BOTH validators so the finding does not rest on either alone:
    /// one split-unit suffix is accepted (404 = passed validation, unknown document), two suffixes are rejected
    /// outright (400) by the legacy <c>IsValidDocId</c> route AND by the strict <c>IsLedgerDocId</c> history route.
    /// A grandchild is therefore unreachable everywhere, not merely on the newest endpoint.</summary>
    [Theory]
    [InlineData("aaaa11110000", HttpStatusCode.NotFound)]
    [InlineData("aaaa11110000-u1", HttpStatusCode.NotFound)]
    [InlineData("aaaa11110000-u12", HttpStatusCode.NotFound)]
    [InlineData("aaaa11110000-u1-u2", HttpStatusCode.BadRequest)]
    public async Task OPEN_A8b_only_one_split_unit_suffix_is_accepted_by_the_routes(string id, HttpStatusCode expected)
    {
        var history = await fx.AsReviewer(HttpMethod.Get, $"/api/documents/{id}/history");
        Assert.Equal(expected, history.StatusCode);

        var legacy = await fx.AsReviewer(HttpMethod.Get, $"/api/file?id={id}");
        Assert.Equal(expected, legacy.StatusCode);
    }

    // =========================================================================================================
    // A10 — the history endpoint is blind to the seeded historical corpus.
    // =========================================================================================================

    /// <summary>
    /// OPEN. <c>GET /api/documents/{id}/history</c> returns 404 when the event list is empty
    /// (Program.cs:789), with the stated rationale that "unknown id and event-less id are indistinguishable — both
    /// are 'no history'". But those are NOT indistinguishable in this ledger: the C9 dedup seed writes the
    /// <c>documents</c> PROJECTION and deliberately never appends an event, so every seeded historical document is a
    /// real, listed, state-bearing row with an empty stream.
    ///
    /// Consequence: the endpoint 404s for exactly the corpus the migration seeded — the documents whose history is
    /// least reconstructible by other means, and the ones a reviewer is most likely to ask about. The same document
    /// appears normally in GET /api/documents, so the reviewer sees a document that exists and a timeline that
    /// reports it does not.
    ///
    /// This test seeds a projection-only row exactly as the migrator does and asserts BOTH halves: the document is
    /// listed, and its history is 404 — which is what separates this from an ordinary unknown id.
    ///
    /// Fix: distinguish the two — 404 only when the projection has no row, and return an empty event list (with the
    /// seeded state) when the row exists. When fixed, the history call returns 200 and this test fails.
    ///
    /// INVERTED 2026-08-12 (was OPEN_A10): 8e66765 fixed it exactly as prescribed above —
    /// `if (await store.GetDocumentAsync(id, ct) is not { } seeded) return Results.NotFound();` — so 404 is now
    /// reserved for an id absent from the PROJECTION too, and the first executed run after install confirmed the
    /// promised failure. The fixed contract for a seeded, projection-only row:
    ///
    ///     200 OK  { documentId, eventCount: 0, events: [], chain: { note: "…" } }
    ///
    /// The `chain.note` is load-bearing and asserted: an empty stream would otherwise read as "nothing happened to
    /// this document", when it means "this document's history was never in this ledger". That distinction is the
    /// entire reason the fix is a 200 and not a 404. The FIRST half — the document is listed — is unchanged: it is
    /// what makes this about a real reviewer-visible document rather than a hypothetical id.
    /// </summary>
    [Fact]
    public async Task REGRESSION_history_answers_200_with_an_empty_annotated_stream_for_a_seeded_projection_only_document()
    {
        var listed = await fx.AsReviewer(HttpMethod.Get, "/api/documents?scope=all");
        listed.EnsureSuccessStatusCode();
        Assert.Contains(HttpSurfaceFixture.SeededProjectionOnly, await listed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var history = await fx.AsReviewer(HttpMethod.Get, $"/api/documents/{HttpSurfaceFixture.SeededProjectionOnly}/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("eventCount").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("events").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("chain").GetProperty("note").GetString()));
    }

    // =========================================================================================================
    // A9 — every principal without an oid shares ONE rate-limit bucket.
    // =========================================================================================================

    /// <summary>
    /// OPEN. <c>RateLimitKey</c> (Program.cs:144-148) partitions on the Entra oid and falls back to
    /// <c>Connection.RemoteIpAddress</c>, then the literal "anon". Finding A1 established that a principal WITHOUT an
    /// oid is fully authorized, so that fallback is reachable by an authorized caller — and every such caller from
    /// one address collapses into a SINGLE shared bucket.
    ///
    /// Measured here: principal #1 (no oid) exhausts the budget; a DIFFERENT no-oid principal is then 429 on its
    /// first request, while an oid-bearing principal gets a fresh bucket. One oid-less caller therefore throttles
    /// every other oid-less caller.
    ///
    /// The deployment amplifies this: no ForwardedHeaders middleware is configured anywhere in the app, so behind
    /// App Service <c>RemoteIpAddress</c> is the platform front-end rather than the client — making the fallback
    /// bucket effectively global rather than per-client.
    ///
    /// Fix: require the oid at the policy (which also fixes A1/A2), and configure ForwardedHeaders if the IP fallback
    /// is meant to be per-client. When fixed, the second principal is no longer 429 and this test fails.
    /// </summary>
    [Fact]
    public async Task OPEN_A9_all_principals_without_an_oid_share_one_rate_limit_bucket()
    {
        var noOidA = HttpSurfaceFixture.PrincipalRolesOnly("Reviewer", "Admin");
        var noOidB = HttpSurfaceFixture.PrincipalRolesOnlyWithName("someone-else", "Reviewer", "Admin");
        var url = $"/api/retry?id={HttpSurfaceFixture.Unknown}";

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
            codes.Add((await fx.Client.SendAsync(fx.Req(HttpMethod.Post, url, noOidA, csrf: true))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);

        // A DIFFERENT oid-less principal is already throttled — same bucket.
        var other = await fx.Client.SendAsync(fx.Req(HttpMethod.Post, url, noOidB, csrf: true));
        Assert.Equal(HttpStatusCode.TooManyRequests, other.StatusCode);

        // ...while an oid-bearing principal is not — so the collapse is specifically the oid fallback.
        var withOid = await fx.Client.SendAsync(fx.Req(HttpMethod.Post, url,
            HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000a901", "Reviewer"), csrf: true));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, withOid.StatusCode);
    }

    /// <summary>
    /// INVERTED 2026-08-12 (was OPEN_A7; the failure-composition A1 fix widened the split entry guard and this
    /// failed on exactly its not-409 assertion). A parent in <c>Filing</c> — the state a failed filing leaves
    /// behind, with reconcile committed to completing it from the sidecar — is now REFUSED by /api/split: splitting
    /// it would land it Segmented underneath the in-doubt filing, outside every recovery arm.
    /// </summary>
    [Fact]
    public async Task REGRESSION_A7_a_document_in_the_in_flight_Filing_state_is_refused_by_the_split_guard()
    {
        // READ THE STATE FIRST — this assertion is the whole test.
        //
        // An earlier version asserted only the response code, which holds for ANY non-terminal state and never
        // established that the document was in `Filing` at all. That mattered: `Filing` is the one fixture document
        // that AuditQueryPredicates.ReconcileWhere selects, and UploadProcessor runs a FULL reconcile at startup, so
        // the document can be re-enqueued and dead-lettered before the first test runs. Without the pre-condition
        // the finding (and now the regression pin) would rest on a vacuous measurement.
        // Establish `Filing` AFTER the host's startup reconcile, which would otherwise re-drive and dead-letter it.
        await fx.AppendEventAsync(HttpSurfaceFixture.FilingLate, "Filing");
        Assert.Equal("Filing", await StateOf(HttpSurfaceFixture.FilingLate));

        var r = await fx.AsReviewer(HttpMethod.Post, "/api/split", csrf: true, body: HttpSurfaceFixture.Json(
            $$"""{"documentId":"{{HttpSurfaceFixture.FilingLate}}","ranges":[[1,1],[2,2]]}"""));

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);      // refused: the filing is in doubt, reconcile owns it

        // ...and it is STILL Filing — the refusal changed nothing.
        Assert.Equal("Filing", await StateOf(HttpSurfaceFixture.FilingLate));
    }

    /// <summary>The projection state of one document, read over the same HTTP surface under test.</summary>
    private async Task<string?> StateOf(string id)
    {
        var r = await fx.AsReviewer(HttpMethod.Get, "/api/documents?scope=all");
        r.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        foreach (var d in doc.RootElement.EnumerateArray())
            if (d.TryGetProperty("documentId", out var did) && did.GetString() == id)
                return d.TryGetProperty("state", out var s) ? s.GetString() : null;
        return null;
    }
}
