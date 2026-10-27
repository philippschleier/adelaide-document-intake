using System.Net;
using System.Text.Json;

namespace Adelaide.Web.Tests;

/// <summary>
/// GET /api/documents/{id}/history and its DocumentHistory allowlist — the newest surface, and the one whose whole
/// purpose is to decide what a reviewer may see of an append-only stream that predates payload hygiene.
///
/// Naming contract as in <see cref="HttpSurfaceFindingsTests"/>.
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceHistoryTests(HttpSurfaceFixture fx)
{
    private async Task<JsonElement> HistoryOf(string id)
    {
        var r = await fx.AsReviewer(HttpMethod.Get, $"/api/documents/{id}/history");
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>REGRESSION. The core promise: a withheld key's VALUE never leaves the process, and its NAME is
    /// reported so a redacted timeline reads as redacted rather than as an empty one.</summary>
    [Fact]
    public async Task REGRESSION_withheld_keys_are_reported_by_name_and_never_by_value()
    {
        var body = await HistoryOf(HttpSurfaceFixture.HistoryProbe);
        var raw = body.GetRawText();

        Assert.DoesNotContain(HttpSurfaceFixture.WithheldMarker, raw, StringComparison.Ordinal);
        // ...and the caller is told something was withheld, by name.
        Assert.Contains("fileName", raw, StringComparison.Ordinal);
        Assert.Contains("error", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// OPEN. The allowlist is applied ONLY at depth 1. <c>DocumentHistory.Project</c> enumerates the root object and
    /// keeps <c>p.Value.Clone()</c> whole whenever the TOP-LEVEL name is allowed — so every nested object or array
    /// under an allowlisted key is emitted verbatim, and its sub-keys are neither filtered nor listed in
    /// <c>redacted</c>.
    ///
    /// Two consequences, and the second is the one that matters:
    ///  1. an unlisted sub-key is RETURNED (asserted here via a synthetic marker nested under the allowlisted "units");
    ///  2. <c>redacted</c> stays EMPTY for that event, so the response affirmatively reports that nothing was withheld
    ///     while emitting a key nobody vetted. A reviewer of this endpoint reading `redacted: []` concludes the
    ///     payload was fully vetted; for nested content that conclusion is unsupported.
    ///
    /// Severity is bounded and stated plainly: NO payload written today is known to nest patient-derived content under
    /// an allowlisted key — <c>units[].evidence</c> is documented PHI-safe machine codes (Model.cs:54), and the three
    /// signal keys were shape-verified by the endpoint's author. This is a latent property of the filter, not a live
    /// leak. It matters because the allowlist exists precisely to be robust against payloads nobody anticipated,
    /// which is the argument its own docstring makes for choosing an allowlist over a denylist.
    ///
    /// Fix: recurse, or reject/redact any allowlisted key whose value is not a scalar (or a vetted shape). When fixed,
    /// the nested marker no longer appears and this test fails.
    /// </summary>
    [Fact]
    public async Task OPEN_the_history_allowlist_is_depth_1_and_reports_nested_passthrough_as_not_redacted()
    {
        var body = await HistoryOf(HttpSurfaceFixture.HistoryProbe);
        var received = body.GetProperty("events").EnumerateArray()
            .Single(e => e.GetProperty("type").GetString() == "Received");

        // (1) the unvetted nested sub-key and its value come straight through, under the allowlisted "units".
        Assert.Contains(HttpSurfaceFixture.NestedMarker, received.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("unlistedSubKey", received.GetRawText(), StringComparison.Ordinal);

        // (2) ...while the SAME event reports the top-level withheld key, and only that one — so `redacted` describes
        //     depth 1 only and cannot be read as "everything returned here was vetted".
        var redacted = received.GetProperty("redacted").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "fileName" }, redacted);
    }

    /// <summary>REGRESSION. A malformed or non-object Details is withheld WHOLE and says so, rather than being
    /// guessed at or crashing the endpoint. Fail-closed, and the sentinel is visible to the caller.</summary>
    [Fact]
    public async Task REGRESSION_malformed_and_non_object_payloads_fail_closed_with_a_sentinel()
    {
        var body = await HistoryOf(HttpSurfaceFixture.HistoryProbe);
        var events = body.GetProperty("events").EnumerateArray().ToArray();

        var nonObject = events.Single(e => e.GetProperty("type").GetString() == "Filed");
        Assert.Equal(new[] { "(non-object)" }, nonObject.GetProperty("redacted").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Empty(nonObject.GetProperty("details").EnumerateObject());

        var malformed = events.Single(e => e.GetProperty("type").GetString() == "Rotated");
        Assert.Equal(new[] { "(unparseable)" }, malformed.GetProperty("redacted").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Empty(malformed.GetProperty("details").EnumerateObject());
    }

    /// <summary>REGRESSION. An id absent from the PROJECTION is "no such document" (404), and the payload carries the
    /// chain note that stops a reader believing a per-document slice is independently verifiable.
    ///
    /// CORRECTED 2026-08-11 (8e66765). This summary used to say "an unknown id AND AN EVENT-LESS ID are both 'no
    /// history' (404)". The second half stopped being true: an event-less id whose projection row exists now returns
    /// 200 with eventCount 0 and an explanatory chain note. The assertion below is unaffected — it probes `Unknown`,
    /// which the fixture defines as having no ledger row at all — but the prose was describing a contract the code
    /// had stopped honouring. On a REGRESSION_ test that is the worse half to leave wrong: the assertion keeps
    /// passing, so nothing ever forces the sentence to be re-read. See OPEN_A10 for the case that did change.</summary>
    [Fact]
    public async Task REGRESSION_unknown_id_is_404_and_the_payload_disclaims_chain_verifiability()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await fx.AsReviewer(HttpMethod.Get, $"/api/documents/{HttpSurfaceFixture.Unknown}/history")).StatusCode);

        var body = await HistoryOf(HttpSurfaceFixture.HistoryProbe);
        Assert.Contains("/api/audit/verify", body.GetProperty("chain").GetProperty("note").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// REGRESSION. The history route carries the "read" limiter, and it is a DIFFERENT bucket from "expensive" — so
    /// reading a timeline cannot consume the upload/OCR allowance, and exhausting uploads cannot blind a reviewer to
    /// document history. Pinned because the two policies share a partition-key function, which makes an accidental
    /// single-bucket refactor easy and invisible.
    /// </summary>
    [Fact]
    public async Task REGRESSION_read_and_expensive_limiters_are_separate_buckets()
    {
        var oid = "00000000-0000-0000-0000-0000000bkt99";
        var principal = HttpSurfaceFixture.Principal(oid, "Reviewer", "Admin");

        // Exhaust "expensive" (default 30/min) for this identity.
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 31; i++)
            last = (await fx.Client.SendAsync(fx.Req(HttpMethod.Post, $"/api/retry?id={HttpSurfaceFixture.Unknown}", principal, csrf: true))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);

        // The SAME identity must still be able to read history — a shared bucket would 429 here.
        var read = await fx.Client.SendAsync(fx.Req(HttpMethod.Get, $"/api/documents/{HttpSurfaceFixture.HistoryProbe}/history", principal));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }
}
