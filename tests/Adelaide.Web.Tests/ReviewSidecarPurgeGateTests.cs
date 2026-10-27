using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

// The review sidecar carries the TRANSCRIPT. Deleting it at filing was the one path the evidence-preservation
// kill-switch did not cover, so PurgeGate preserved the PDF while the transcript went anyway — August 2026 filings
// kept 100% of PDFs and 5% of transcripts. These pin the corrected behaviour.
//
// PurgeGate reads its environment once at static init, so these assert the posture the process actually starts with:
// with the switch unset (the shipped default) the transcript must survive filing.
public sealed class ReviewSidecarPurgeGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "adelaide-sidecar-" + Guid.NewGuid().ToString("n")[..8]);
    public ReviewSidecarPurgeGateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private string MakeSidecar(string id)
    {
        var p = Path.Combine(_dir, $"{id}.review.json");
        File.WriteAllText(p, """{"documentId":"d1","markdown":"synthetic transcript text"}""");
        return p;
    }

    [Fact]
    public void With_purging_disabled_the_transcript_survives_filing()
    {
        Assert.False(PurgeGate.Enabled, "this test asserts the shipped default; ADELAIDE_PURGE_ENABLED must be unset");
        var path = MakeSidecar("d1");
        UploadProcessor.DeleteReviewSidecarIfPurgeEnabled(path, "d1", NullLogger.Instance);
        Assert.True(File.Exists(path), "the review sidecar holds the only copy of the transcript — it must not be deleted while purging is off");
    }

    // The filing sidecar is a recovery payload with no transcript, so it is NOT gated and must still be dropped.
    // Pinning both together is the point: the two sidecars had the same lifecycle and should not have.
    [Fact]
    public void The_filing_sidecar_is_still_deleted_because_it_carries_no_transcript()
    {
        var pdf = Path.Combine(_dir, "d2.pdf");
        File.WriteAllText(pdf, "%PDF-1.4 synthetic");
        var filing = UploadProcessor.FilingSidecarPath(pdf);
        File.WriteAllText(filing, """{"spec":{}}""");
        UploadProcessor.DeleteFilingSidecar(pdf);
        Assert.False(File.Exists(filing));
    }

    [Fact]
    public void Deleting_an_absent_sidecar_is_not_an_error()
        => UploadProcessor.DeleteReviewSidecarIfPurgeEnabled(Path.Combine(_dir, "nope.review.json"), "nope", NullLogger.Instance);

    // A4 fold-in (2026-08-16): the sidecar rides the SAME grace deferral as the PDF. Armed-with-grace used to
    // keep the PDF for misfile repair while burning the sidecar beside it — a sequencing oversight (the gate
    // arrived 08-06, the deferral 08-08 and covered only the PDF). Gate-explicit overload: the statics are
    // read-once, so tests inject the decisions, exactly like the PurgeFiledInputsAsync matrix.
    [Fact]
    public void Armed_with_grace_defers_the_sidecar_to_the_aged_sweep()
    {
        var path = MakeSidecar("d4");
        UploadProcessor.DeleteReviewSidecarIfPurgeEnabled(path, "d4", NullLogger.Instance, purgeEnabled: true, graceDeferred: true);
        Assert.True(File.Exists(path), "with a grace window the sidecar is released by the sweep WITH the PDF — one repair window, one release");
    }

    [Fact]
    public void Armed_without_grace_still_deletes_at_filing()
    {
        var path = MakeSidecar("d5");
        UploadProcessor.DeleteReviewSidecarIfPurgeEnabled(path, "d5", NullLogger.Instance, purgeEnabled: true, graceDeferred: false);
        Assert.False(File.Exists(path), "no window configured → the pre-C5 purge-at-filing behavior, sidecar included");
    }

    // (The saved-orientation-at-split pins moved to HttpSurfaceSplitWindowTests: /api/split no longer deletes
    // anything under ANY flag posture — the endpoint appends only, so the gated helper these tests exercised
    // is gone, and the surviving property is pinned end-to-end through the real endpoint instead.)
}
