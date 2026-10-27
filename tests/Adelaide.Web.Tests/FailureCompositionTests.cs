using System.Net;

namespace Adelaide.Web.Tests;

/// <summary>
/// Pinning tests for the FAILURE-COMPOSITION investigation — what happens when lifecycle operations interleave, or
/// when one fails halfway through.
///
/// NAMING CONTRACT, inherited from the HTTP-surface audit and load-bearing here:
///  • <c>OPEN_</c> pins a CURRENT DEFECT. It asserts today's behaviour, so it PASSES on an unfixed tree and FAILS once
///    the defect is fixed — that failure is the signal to delete or invert it. Each one names the fix that breaks it.
///  • <c>REGRESSION_</c> pins behaviour that is correct today. A failure here is a real regression.
/// Nothing is marked Skip: a self-skipping test that still reports "passed" proves nothing.
///
/// Each test starts its OWN host (see <see cref="FaultHost"/>) because the fault seams are read from the child
/// process environment at composition time and cannot be varied on a shared host.
/// </summary>
public sealed class FailureCompositionTests
{
    // A 12-lowercase-hex id, the exact shape the ledger mints (Guid("N")[..12]). Synthetic; no real document.
    private const string Doc = "bbbb00000001";

    private static HttpContent BusinessReview(string id) => FaultHost.Json(
        $$"""{"documentId":"{{id}}","contentType":"Rechnung","patientListItemId":0,"fields":{},"isBusiness":true,"zielbereich":"HR"}""");

    /// <summary>
    /// REGRESSION — INVERTED 2026-08-12 (was OPEN_C1, the investigation's cleanest BAND 1; the fix made it fail
    /// exactly as its docstring promised, via the sidecar assertion).
    ///
    /// The rotation bake swaps the PDF bytes and appends the <c>Rotated</c> event AFTERWARDS. When that append fails,
    /// the review submit's inline catches used to call <c>DeleteRotateSidecar</c> — destroying the crash-safe
    /// recovery intent — which left a PHI document whose bytes were changed with NO event anywhere in the append-only
    /// chain and NO intent for any sweep to find, permanently (the bake's idempotence guard never re-records an
    /// already-rotated file). The fix keeps the intent in both inline catches (mirroring the reconcile rotate pass),
    /// and <c>DriveRotateAsync</c>'s moot branch now drops an intent only when its event is in the chain or the bytes
    /// never changed. This pins the fixed contract: the byte change may precede its event by a failure, but the
    /// INTENT survives it, so the change is pending — never silently permanent.
    /// </summary>
    [Fact]
    public async Task REGRESSION_C1_a_failed_rotation_append_keeps_the_recovery_intent_for_reconcile()
    {
        await using var h = await FaultHost.StartAsync(
            new() { ["ADELAIDE_TEST_APPEND_FAIL"] = "Rotated" },
            async host =>
            {
                host.SeedInbox(Doc);
                await host.AppendAsync(Doc, "NeedsReview", """{"reasonCodes":["synthetic"]}""");
            });

        var before = h.InboxPdf(Doc);

        // The reviewer saves an orientation (this writes .rotation.json only — it does not touch the PDF).
        var put = await h.AsReviewer(HttpMethod.Put, $"/api/documents/{Doc}/rotation", FaultHost.Json("""{"angle":90}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(before, h.InboxPdf(Doc)); // control: the PUT itself bakes nothing

        // Submitting the review bakes the saved angle, then fails to record it — the submit must abort.
        var post = await h.AsReviewer(HttpMethod.Post, "/api/review", BusinessReview(Doc));

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);

        // 1. The bytes on disk DID change — the failure came after the atomic swap.
        Assert.NotEqual(before, h.InboxPdf(Doc));

        // 2. The chain does not have the event YET (the append is the injected failure)...
        Assert.DoesNotContain(await h.EventsAsync(Doc), e => e.Type == "Rotated");

        // 3. ...but the INTENT SURVIVES, so the change is recoverable: the reconcile rotate pass (or the next
        //    submit's pending-rotation branch) re-drives it and appends the true H0→H1 transform late. This
        //    assertion is the inversion of C-1: it was Assert.False, and deleting the intent in the catches
        //    would make it fail again.
        Assert.True(h.InboxHas($"{Doc}.rotate.json"),
            "the rotate intent was destroyed on a failed append — C-1 has regressed: the byte change is now permanent and un-audited");
    }

    /// <summary>
    /// REGRESSION / CONTROL for the whole file. The injected append fault must be TARGETED: with only "Rotated"
    /// nominated, every other append still lands. Without this control, C-1's "no Rotated event" assertion could be
    /// satisfied by a globally broken store, and the finding would be an artefact of the harness.
    /// </summary>
    [Fact]
    public async Task REGRESSION_the_injected_append_fault_is_targeted_not_global()
    {
        await using var h = await FaultHost.StartAsync(
            new() { ["ADELAIDE_TEST_APPEND_FAIL"] = "Rotated" },
            async host =>
            {
                host.SeedInbox(Doc);
                await host.AppendAsync(Doc, "NeedsReview", """{"reasonCodes":["synthetic"]}""");
            });

        var events = await h.EventsAsync(Doc);
        Assert.Contains(events, e => e.Type == "NeedsReview");
        Assert.Equal("NeedsReview", (await h.DocAsync(Doc))?.State);
    }

    /// <summary>
    /// REGRESSION — INVERTED 2026-08-12 (was OPEN_A1; the widened entry guard made it fail exactly as its docstring
    /// promised, returning 409).
    ///
    /// <c>/api/split</c> used to guard TERMINALITY only, and <c>Filing</c> is not terminal — it is the state a 502
    /// from the filing step leaves behind, with the <c>.filing.json</c> intent still on disk, and reconcile WILL
    /// complete that filing from the sidecar. Splitting such a parent landed it <c>Segmented</c> underneath the
    /// in-doubt filing, excluding it from reconcile twice (the state arm as terminal, the orphan-sidecar arm as
    /// not-NeedsReview): an item already uploaded to SharePoint stayed live with no Filed event anywhere. The entry
    /// guard now refuses BOTH signals of doubt — the <c>Filing</c> state, and a surviving <c>.filing.json</c>
    /// regardless of the state label. The per-document claim closes only the CONCURRENT window; this pins the
    /// SEQUENTIAL one.
    ///
    /// SEEDING TRAP, kept from the OPEN_ version: <c>Filing</c> is the ONE state
    /// <c>AuditQueryPredicates.ReconcileWhere</c> selects, and the host runs a FULL reconcile at startup — so the
    /// <c>Filing</c> event is appended AFTER the host is live, and the pre-condition is asserted rather than
    /// assumed: if the state is not <c>Filing</c> at the moment of the split, this fails as a broken FIXTURE with a
    /// clear message rather than as a finding.
    /// </summary>
    [Fact]
    public async Task REGRESSION_A1_split_refuses_a_parent_whose_filing_is_in_doubt()
    {
        await using var h = await FaultHost.StartAsync(
            new(),
            async host =>
            {
                host.SeedInbox(Doc);
                await host.AppendAsync(Doc, "NeedsReview", """{"reasonCodes":["synthetic"]}""");
            });

        // AFTER startup: the state a failed filing leaves behind, established so it is true for this request.
        await h.AppendAsync(Doc, "Filing");
        Assert.Equal("Filing", (await h.DocAsync(Doc))?.State); // fixture pre-condition, not the finding

        var r = await h.AsReviewer(HttpMethod.Post, "/api/split",
            FaultHost.Json($$"""{"documentId":"{{Doc}}","ranges":[[1,2],[3,4]]}"""));

        // The guard refuses; nothing about the parent changed and no child exists.
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("Filing", (await h.DocAsync(Doc))?.State);
        Assert.DoesNotContain(await h.EventsAsync(Doc), e => e.Type == "Segmented");
        Assert.False(h.InboxHas($"{Doc}-u1.pdf"));
    }

    /// <summary>
    /// REGRESSION — INVERTED 2026-08-12 (was OPEN_C3; the reordered commit made it fail exactly as its docstring
    /// promised, via the child-PDF assertion).
    ///
    /// <c>/api/split</c> used to append its commit event <c>Segmented</c> LAST, with no intent sidecar — the only
    /// multi-step mutation without an outbox member. A failed commit left children written and recorded as units of
    /// a split that never committed, the parent still re-filable, and nothing ever re-attempting <c>Segmented</c>:
    /// the same pages filable n+1 times. Now the split writes a <c>.split.json</c> fan-out intent, appends
    /// <c>Segmented</c> BEFORE the children materialise (the commit names every child, so the children derive from
    /// the commit), and fans out under <c>CancellationToken.None</c>; reconcile's split pass completes a committed
    /// fan-out from the recorded ranges and drops an uncommitted intent. This pins the failed-commit contract:
    /// NOTHING durable exists — no child bytes, no child events, the parent untouched — and the intent survives
    /// for reconcile to adjudicate the torn case.
    /// </summary>
    [Fact]
    public async Task REGRESSION_C3_a_failed_split_commit_leaves_nothing_durable_and_keeps_the_intent()
    {
        await using var h = await FaultHost.StartAsync(
            new() { ["ADELAIDE_TEST_APPEND_FAIL"] = "Segmented" },
            async host =>
            {
                host.SeedInbox(Doc);
                await host.AppendAsync(Doc, "NeedsReview", """{"reasonCodes":["synthetic"]}""");
            });

        var r = await h.AsReviewer(HttpMethod.Post, "/api/split",
            FaultHost.Json($$"""{"documentId":"{{Doc}}","ranges":[[1,2],[3,4]]}"""));

        // The commit failed → the endpoint says so, and NOTHING durable happened.
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.DoesNotContain(await h.EventsAsync(Doc), e => e.Type == "Segmented");
        Assert.Equal("NeedsReview", (await h.DocAsync(Doc))?.State);      // the parent is untouched and actionable
        Assert.False(h.InboxHas($"{Doc}-u1.pdf"), "a child PDF was carved before the commit landed — the C-3 ordering has regressed");
        Assert.Null(await h.DocAsync($"{Doc}-u1"));                       // no child was recorded

        // The intent is deliberately KEPT: if the append actually landed and only the response path failed, the
        // recovery pass completes the fan-out; if it genuinely did not land (this case), the pass sees no Segmented
        // event and drops it.
        Assert.True(h.InboxHas($"{Doc}.split.json"));
    }

    /// <summary>
    /// OPEN — FINDING A4. Splitting a split child mints <c>{id}-u1-u1</c>, but every id validator allows ONE unit
    /// suffix at most — <c>IsValidDocId</c> is <c>^[a-zA-Z0-9]+(-u[0-9]+)?$</c> (<c>Program.cs:1597</c>) and the
    /// history route's <c>IsLedgerDocId</c> is stricter still. The worker validates nothing, so such a document
    /// processes normally; but if it routes to review it is permanently unactionable — every endpoint 400s — and the
    /// stalled-document gauge cannot see it either, because it excludes <c>NeedsReview</c> by construction.
    ///
    /// A 400 (not a 404) is the assertion: 404 would mean "no such document", 400 means "that id is unrepresentable".
    ///
    /// FIXED BY: allowing repeated unit suffixes in both validators, or refusing to split a child in the first place.
    /// </summary>
    [Fact]
    public async Task OPEN_A4_a_second_generation_split_id_is_rejected_by_the_id_validators()
    {
        await using var h = await FaultHost.StartAsync(new());

        var r = await h.AsReviewer(HttpMethod.Get, $"/api/documents/{Doc}-u1-u1/history");

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    /// <summary>
    /// REGRESSION — INVERTED 2026-08-12 (was OPEN_rotation_put; the terminal guard made it fail exactly as its
    /// docstring promised, returning 409).
    ///
    /// The rotation PUT had NO document-state check of any kind: it validated the id, normalised the angle, and
    /// required only that the arrival PDF still exist. Under the purge-OFF posture a settled document keeps its
    /// inputs, so a stale browser tab could save an orientation onto an already FILED document and be told it
    /// succeeded — the reachability precondition for C-1 on a terminal document. The PUT now carries the terminal
    /// guard its sibling routes have (B-4), and the dashboard's `orientable` gate was reconciled with it in the
    /// same commit.
    /// </summary>
    [Fact]
    public async Task REGRESSION_rotation_put_refuses_a_terminal_document()
    {
        await using var h = await FaultHost.StartAsync(
            new(),
            async host =>
            {
                host.SeedInbox(Doc);
                await host.AppendAsync(Doc, "Filed", $$"""{"driveItemId":"{{FaultHost.SyntheticDriveItemId}}"}""");
            });

        var r = await h.AsReviewer(HttpMethod.Put, $"/api/documents/{Doc}/rotation", FaultHost.Json("""{"angle":180}"""));

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.False(h.InboxHas($"{Doc}.rotation.json")); // and no orientation sidecar was written onto the settled doc
    }
}
