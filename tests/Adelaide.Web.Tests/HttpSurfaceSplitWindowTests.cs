using System.Net;
using Adelaide.Audit;
using Xunit;

namespace Adelaide.Web.Tests;

// Split-parent windowing: POST /api/split APPENDS ONLY — the parent's PDF, review sidecar and saved
// orientation are the only mis-split repair source (no durable copy exists; the children carry the content
// but not the bytes), so every one of them must survive the transition to Segmented, under any flag posture.
// Pinned end-to-end through the real host with a REAL committed split (the fixture's dedicated, mutating
// SplitWindowParent specimen). The release of these artifacts after the repair window belongs to the C5 aged
// sweep, pinned in FiledPurgeGraceTests.
[Collection("http-surface")]
public sealed class HttpSurfaceSplitWindowTests(HttpSurfaceFixture fx)
{
    [Fact]
    public async Task A_committed_split_leaves_every_parent_artifact_on_disk()
    {
        const string id = HttpSurfaceFixture.SplitWindowParent;
        string P(string ext) => Path.Combine(fx.InboxPath, id + ext);
        Assert.True(File.Exists(P(".pdf")) && File.Exists(P(".review.json")) && File.Exists(P(".rotation.json")),
            "fixture precondition: the parent starts with all three artifacts");

        var r = await fx.AsReviewer(HttpMethod.Post, "/api/split", csrf: true,
            body: HttpSurfaceFixture.Json($$"""{"documentId":"{{id}}","ranges":[[1,2],[3,4]]}"""));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // The transition committed…
        var store = new SqliteAuditStore(fx.LedgerPath, "test");
        var doc = await store.GetDocumentAsync(id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal("Segmented", doc!.State);

        // …and destroyed nothing: all three parent artifacts survive for the repair window, while the spent
        // split INTENT is correctly consumed (outbox lifecycle — it is working state, not evidence).
        Assert.True(File.Exists(P(".pdf")), "the parent PDF is the mis-split repair source");
        Assert.True(File.Exists(P(".review.json")), "the review sidecar is part of the repair material");
        Assert.True(File.Exists(P(".rotation.json")), "re-deriving a mis-split needs the bytes AND the reviewed angle");
        Assert.False(File.Exists(P(".split.json")), "the fan-out completed — the intent is spent");
    }
}
