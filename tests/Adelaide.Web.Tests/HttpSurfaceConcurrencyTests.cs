using System.Net;
using Adelaide.Audit;

namespace Adelaide.Web.Tests;

/// <summary>
/// Concurrency at the HTTP boundary. Naming contract as in <see cref="HttpSurfaceFindingsTests"/>.
///
/// SCOPE NOTE, stated because the honest limit matters more than the coverage. The interesting races in this app —
/// two review submits racing to file one document, a submit racing the aged-purge sweep — are guarded by IN-PROCESS
/// claims (FilingClaims, DocGate/KeyGate) whose LOSING side is only observable when the winner is still inside its
/// critical section. The winner's critical section is a SharePoint upload, which this harness deliberately cannot
/// perform, so it completes in microseconds and the loser usually finds no contention at all. A test that asserted
/// "the second caller gets 409" would therefore be timing-dependent, and a flaky test is worse than no test.
///
/// So the tests here assert INVARIANTS that can only fail if the guard is genuinely broken ("never more than one
/// winner"), never the timing-dependent converse ("the loser observes a 409"). What is NOT verifiable by this method
/// is recorded as UNVERIFIABLE in API-SURFACE-AUDIT.md with the reason, rather than being asserted weakly.
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceConcurrencyTests(HttpSurfaceFixture fx)
{
    /// <summary>
    /// REGRESSION. The single-redrive invariant: N concurrent /api/retry calls for ONE DeadLetter document must
    /// re-enqueue it AT MOST ONCE PER DEAD-LETTER WINDOW. Two winners for one window would mean the same PDF enters
    /// the paid pipeline twice off a single failure, which is double spend plus a corrupted attempt history.
    ///
    /// Two independent guards make this hold — the per-document <c>retryGate</c> (a ConcurrentDictionary.TryAdd
    /// shared with /api/reprocess, held across read→append→enqueue and released in a finally) and the state check
    /// that a re-driven document is no longer DeadLetter.
    ///
    /// WHY THIS IS ASSERTED AGAINST THE LEDGER AND NOT THE RESPONSE COUNT, recorded because the obvious version of
    /// this test is FLAKY and failed the first time CI ever ran it (Linux, 2 accepted of 8; Windows, 1). An accepted
    /// retry enqueues the document, and in this harness the pipeline fails INSTANTLY — every outbound seam is
    /// blocked, so transcription throws in microseconds and the worker appends DeadLetter again. That legitimately
    /// re-opens a second retry window inside the burst, and a later request in the same burst is then CORRECTLY
    /// accepted. "Exactly one of eight" was therefore a statement about how fast the runner is, not about the guard —
    /// precisely the timing-dependent assertion this class's own scope note forbids. (In production the window
    /// cannot re-open mid-burst: the pipeline it enqueues into makes real vision/DI calls that take seconds.)
    ///
    /// So the invariant is checked where it is actually decidable — the append-only chain: walking the event stream,
    /// every <c>Received{channel:"Retry"}</c> must consume a DeadLetter that preceded it, and no two may consume the
    /// same one. A broken gate produces two consecutive Retry events with no DeadLetter between them and fails here
    /// with the offending sequence in the message, on any runner, at any speed.
    ///
    /// Deliberately uses its own document so no other test can move its state first.
    /// </summary>
    [Fact]
    public async Task REGRESSION_C1_concurrent_retries_of_one_document_produce_at_most_one_redrive_per_dead_letter()
    {
        var principal = HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000cc01", "Reviewer", "Admin");
        var url = $"/api/retry?id={HttpSurfaceFixture.ConcurrentRetry}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fx.Client.SendAsync(fx.Req(HttpMethod.Post, url, principal, csrf: true))));

        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.True(accepted >= 1, "no retry was accepted at all — the burst proved nothing about the guard");

        // Every loser must be a CLEAN refusal — a specific 4xx, never a 500 and never a silent 2xx.
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.True(r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests,
                $"unexpected loser status {(int)r.StatusCode}"));

        // THE INVARIANT, from the chain the host actually wrote (WAL, so a second reader is safe while it runs).
        var store = new SqliteAuditStore(fx.LedgerPath, "test");
        var events = await store.GetEventsAsync(HttpSurfaceFixture.ConcurrentRetry);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var sequence = string.Join(" → ", events.Select(e => e.Type == "Received" && e.Details.Contains("\"Retry\"", StringComparison.Ordinal) ? "Received(Retry)" : e.Type));
        var openWindow = false;   // a DeadLetter no retry has consumed yet
        var retries = 0;
        foreach (var e in events)
        {
            if (e.Type == "DeadLetter") { openWindow = true; continue; }
            if (e.Type != "Received" || !e.Details.Contains("\"Retry\"", StringComparison.Ordinal)) continue;
            retries++;
            Assert.True(openWindow, $"two retries were accepted for ONE DeadLetter — the redrive guard let both through. Chain: {sequence}");
            openWindow = false;   // this retry consumed the window
        }

        // The ledger and the HTTP answers must agree: one accepted retry, one Retry event, no silent extras.
        Assert.Equal(accepted, retries);
    }

    /// <summary>
    /// REGRESSION. Concurrent review submits for one document are all answered with a CLEAN, SPECIFIC status — the
    /// per-document claim's 409 or the filing failure's 502 — and never with an unhandled 500 or a silent success.
    ///
    /// The name is deliberately narrower than "never two winners". An earlier version of this test asserted that and
    /// was VACUOUS: in this harness no submit can reach a successful filing at all (TestMode blocks the SharePoint
    /// call), so "no 200 appeared" was guaranteed by the harness rather than by the claim. What IS decidable, and
    /// what this now asserts, is that the losing path is a clean refusal rather than a crash — with 500 excluded from
    /// the accepted set, which is what makes the assertion bite.
    ///
    /// The document is NeedsReview (non-terminal) with both inputs on disk, so a 409 here can only come from the
    /// per-document claim, never from the terminal guard. Whether the claim actually prevents a DOUBLE FILE is not
    /// decidable from outside a host that can file; that is recorded as UNVERIFIABLE in API-SURFACE-AUDIT.md.
    /// </summary>
    [Fact]
    public async Task REGRESSION_C2_concurrent_review_submits_are_all_clean_refusals_never_a_500()
    {
        var principal = HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000cc02", "Reviewer", "Admin");
        var body = $$"""{"documentId":"{{HttpSurfaceFixture.NeedsReview}}","contentType":"Rechnung","patientListItemId":0,"fields":{},"isBusiness":true,"zielbereich":"HR"}""";

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            fx.Client.SendAsync(fx.Req(HttpMethod.Post, "/api/review", principal, csrf: true, HttpSurfaceFixture.Json(body)))));

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.True((int)r.StatusCode is 409 or 502,
            $"expected a clean 409/502 from a concurrent review submit, got {(int)r.StatusCode}"));
    }

    // REMOVED, deliberately: a test named "retry and reprocess share a per-document gate".
    //
    // It asserted only that no call returned 200 against a Filed document — but /api/retry ALWAYS returns 400 there
    // (wrong state) and /api/reprocess ALWAYS returns 502 there (this harness blocks the SharePoint delete), each
    // independently and every time. The shared `retryGate` was never the reason for any refusal: deleting the gate
    // outright would have left the test green. It proved nothing that its name claimed, so it is gone rather than
    // left to give false assurance.
    //
    // The gate IS real (Program.cs: one ConcurrentDictionary consulted by both routes) but its losing branch is only
    // observable while the winner is inside a critical section that this harness makes instantaneous. Recorded as
    // UNVERIFIABLE in API-SURFACE-AUDIT.md, with the source evidence, rather than pinned by a test that cannot fail.
}
