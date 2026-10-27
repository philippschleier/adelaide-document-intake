using System.Net;

namespace Adelaide.Web.Tests;

/// <summary>
/// Rate-limiter coverage and partitioning. Naming contract as in <see cref="HttpSurfaceFindingsTests"/>.
///
/// METHOD NOTE, because "no 429 appeared" is a claim that is easy to get wrong: every unlimited-route test below runs
/// a POSITIVE CONTROL in the same test, against the same fixture, with the same identity — a burst on /api/retry,
/// which IS limited. If the control does not 429, the test fails as inconclusive rather than reporting an absent
/// limiter. That is what distinguishes "this route has no limiter" from "the burst never reached the limiter".
///
/// The control also disposes of the obvious objection that a rejected request might not consume a permit: /api/retry
/// answers 404 for these ids and still counts, which is exactly why the burst reaches 429.
/// </summary>
[Collection("http-surface")]
public sealed class HttpSurfaceRateLimitTests(HttpSurfaceFixture fx)
{
    private const int ExpensiveLimit = 30; // ADELAIDE_RATE_LIMIT_PER_MINUTE default

    /// <summary>
    /// Burst size chosen to be WINDOW-BOUNDARY-PROOF, not merely "more than the limit".
    /// The limiter is a FIXED window: if a burst straddles a replenish, two windows are in play and up to
    /// 2 x ExpensiveLimit requests are admitted. At 2 x limit + 5, at least five requests must be shed no matter where
    /// the boundary falls — so "a 429 appears" cannot flake on timing, only on the limiter being absent.
    /// This is deliberate: one unexplained failure was observed across the audit's full-solution runs and never
    /// reproduced, and a fixed-window straddle under load was the leading candidate.
    /// </summary>
    private const int Burst = (ExpensiveLimit * 2) + 5;

    private async Task<HttpStatusCode[]> BurstAsync(HttpMethod method, string url, string principal, bool csrf)
    {
        var codes = new HttpStatusCode[Burst];
        for (var i = 0; i < Burst; i++)
            codes[i] = (await fx.Client.SendAsync(fx.Req(method, url, principal, csrf))).StatusCode;
        return codes;
    }

    /// <summary>Assert the limiter demonstrably works for this identity — otherwise an absent-limiter claim is
    /// unfounded. Uses a burst on the known-limited /api/retry.</summary>
    private async Task AssertControlLimiterEngages(string principal)
    {
        var control = await BurstAsync(HttpMethod.Post, $"/api/retry?id={HttpSurfaceFixture.Unknown}", principal, csrf: true);
        Assert.Contains(HttpStatusCode.TooManyRequests, control);
    }

    /// <summary>
    /// OPEN. Four routes carry NO rate limiter at all. Each is listed with why it is expensive or sensitive:
    ///
    ///  • POST /api/duplicate-process — RE-ENQUEUES THE DOCUMENT INTO THE PAID PIPELINE (Program.cs:576 writes an
    ///    UploadJob), driving OCR/vision/LLM spend per call. Its sibling /api/retry does exactly the same thing and IS
    ///    limited. This is the sharpest of the four: the limiter's stated purpose is "each upload/retry/reprocess
    ///    drives paid OCR/LLM + native rendering" and this route was simply not annotated.
    ///  • GET  /api/page — drives a NATIVE PDF RENDER per request (up to 240 DPI, 3000px edge) — CPU, not spend.
    ///  • GET  /api/documents?scope=all — returns up to 5000 projection rows per call, each carrying the arrival
    ///    filename (patient name + insurance number).
    ///  • GET  /api/patients — an identity-directory search that calls Microsoft Graph once per request.
    ///
    /// The last two are the read surfaces the prior audit flagged as the highest-identity reads in the app; this test
    /// pins the rate-limit half of that, which is what makes them enumerable.
    ///
    /// Fix: add .RequireRateLimiting("expensive") to /api/duplicate-process, and .RequireRateLimiting("read") to the
    /// three read paths. When fixed, a 429 appears in the burst and this test fails.
    /// </summary>
    /// <summary>
    /// Burst for the no-limiter cases. It must exceed the LARGER policy's budget, not the smaller one: at 65 requests
    /// a route carrying the "read" policy (default 120/min) is indistinguishable from a route carrying nothing, so
    /// the test would still pass after the very fix it prescribes. Sized above 120 so "no 429 appeared" can only mean
    /// no limiter at all. Wave 2 caught this; the original burst of 40 proved less than it claimed.
    /// </summary>
    private const int AboveEveryPolicyBurst = 130;

    [Theory]
    [InlineData("POST", "/api/duplicate-process?id=" + HttpSurfaceFixture.NeedsReview, true)]
    [InlineData("GET", "/api/page?id=" + HttpSurfaceFixture.NeedsReview + "&page=1", false)]
    [InlineData("GET", "/api/documents?scope=all", false)]
    [InlineData("GET", "/api/patients?q=zzsynthetic", false)]
    public async Task OPEN_R1_these_routes_have_no_rate_limiter(string method, string url, bool csrf)
    {
        // A distinct identity per case: the limiter partitions per Entra oid, so a shared one would leak permits
        // between cases and make the control meaningless.
        var oid = "00000000-0000-0000-0000-" + Math.Abs(url.GetHashCode()).ToString("D12")[..12];
        var principal = HttpSurfaceFixture.Principal(oid, "Reviewer", "Admin");

        var codes = new HttpStatusCode[AboveEveryPolicyBurst];
        for (var i = 0; i < AboveEveryPolicyBurst; i++)
            codes[i] = (await fx.Client.SendAsync(fx.Req(new HttpMethod(method), url, principal, csrf))).StatusCode;
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, codes);

        await AssertControlLimiterEngages(principal); // inconclusive-run guard
    }

    /// <summary>
    /// REGRESSION. The "expensive" limiter admits exactly 30 requests per identity per minute and sheds the 31st.
    /// Pins both the count and the fact that a non-2xx response still consumes a permit (these ids 404).
    /// </summary>
    [Fact]
    public async Task REGRESSION_R2_expensive_limiter_admits_exactly_the_configured_budget()
    {
        var principal = HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000ex01", "Reviewer", "Admin");
        var codes = await BurstAsync(HttpMethod.Post, $"/api/retry?id={HttpSurfaceFixture.Unknown}", principal, csrf: true);

        // The budget is at least the configured limit: the first N are always admitted. (A fresh identity means the
        // fixed window opens on this test's own first request, so no earlier traffic has spent it.)
        Assert.All(codes[..ExpensiveLimit], c => Assert.NotEqual(HttpStatusCode.TooManyRequests, c));

        // ...and it is BOUNDED. Stated as a range rather than an exact count purely to survive a window replenish
        // mid-burst; the lower bound is the real assertion and it is what a raised/removed limit would break.
        var admitted = codes.Count(c => c != HttpStatusCode.TooManyRequests);
        Assert.InRange(admitted, ExpensiveLimit, ExpensiveLimit * 2);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }

    /// <summary>
    /// REGRESSION. The limiter partitions PER IDENTITY (Entra oid), not globally — one reviewer exhausting their
    /// budget must not lock out another. This is the property that makes the limiter an abuse control rather than a
    /// denial-of-service amplifier.
    /// </summary>
    [Fact]
    public async Task REGRESSION_R3_the_limiter_partitions_per_identity_not_globally()
    {
        var a = HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000pa01", "Reviewer", "Admin");
        var b = HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000pb02", "Reviewer", "Admin");

        var exhausted = await BurstAsync(HttpMethod.Post, $"/api/retry?id={HttpSurfaceFixture.Unknown}", a, csrf: true);
        Assert.Contains(HttpStatusCode.TooManyRequests, exhausted);

        var other = await fx.Client.SendAsync(fx.Req(HttpMethod.Post, $"/api/retry?id={HttpSurfaceFixture.Unknown}", b, csrf: true));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
    }

    /// <summary>
    /// OPEN (minor). The 429 carries NO Retry-After header and an EMPTY body. A well-behaved client — notably the
    /// on-prem drain and any bulk uploader — has nothing to back off against and can only guess, which turns a
    /// throttle into a retry storm. The rejection is also not audited anywhere, so an operator cannot see that a
    /// reviewer was throttled mid-batch.
    ///
    /// Fix: set OnRejected to write a Retry-After from the fixed window. When fixed, this test fails.
    /// </summary>
    [Fact]
    public async Task OPEN_R4_the_429_carries_no_retry_after_and_no_body()
    {
        var principal = HttpSurfaceFixture.Principal("00000000-0000-0000-0000-00000000ra01", "Reviewer", "Admin");
        var codes = await BurstAsync(HttpMethod.Post, $"/api/retry?id={HttpSurfaceFixture.Unknown}", principal, csrf: true);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);

        var rejected = await fx.Client.SendAsync(fx.Req(HttpMethod.Post, $"/api/retry?id={HttpSurfaceFixture.Unknown}", principal, csrf: true));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Null(rejected.Headers.RetryAfter);
        Assert.Equal(0, (await rejected.Content.ReadAsByteArrayAsync()).Length);
    }
}
