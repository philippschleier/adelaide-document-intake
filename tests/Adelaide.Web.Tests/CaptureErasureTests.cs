using Adelaide.Audit;
using Adelaide.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

// C3e — the erasure operation behind POST /api/capture/erase, tested at the seam (the endpoint is a thin adapter:
// validation + Admin policy + this call). Scope semantics belong to the store contract; what THIS pins is the
// operation's composition: the right store method per scope, the erasure-log record for every act, and the honest
// Result when parts fail.
public sealed class CaptureErasureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"adelaide-erase-{Guid.NewGuid():N}");
    private TranscriptStore Store() { Directory.CreateDirectory(_dir); return new TranscriptStore(Path.Combine(_dir, "e.db")); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private static async Task Seed(ITranscriptStore s, string id)
    {
        var t0 = DateTimeOffset.UtcNow;
        await s.PutAsync(id, "text", "sha", t0);
        await s.PutCorpusAsync(new CorpusRow(id, t0, ModelContentType: "Verordnung"));
    }

    [Fact]
    public async Task Content_scope_removes_text_keeps_measurement_and_logs()
    {
        var s = Store();
        await Seed(s, "d1");
        var r = await CaptureErasure.ExecuteAsync(s, "d1", "content", "user:admin", NullLogger.Instance, CancellationToken.None);
        Assert.True(r.Removed);
        Assert.True(r.Logged);
        Assert.Null(await s.GetAsync("d1"));
        Assert.Equal(1, await s.CorpusRowCountAsync());   // measurement survives a content erasure
    }

    [Fact]
    public async Task Full_scope_removes_measurement_too()
    {
        var s = Store();
        await Seed(s, "d1");
        var r = await CaptureErasure.ExecuteAsync(s, "d1", "full", "user:admin", NullLogger.Instance, CancellationToken.None);
        Assert.True(r.Removed);
        Assert.Equal(0, await s.CorpusRowCountAsync());
    }

    [Fact]
    public async Task Erasing_an_unknown_document_reports_removed_false_but_still_logs_the_act()
    {
        var s = Store();
        var r = await CaptureErasure.ExecuteAsync(s, "nope", "content", "user:admin", NullLogger.Instance, CancellationToken.None);
        Assert.False(r.Removed);
        Assert.True(r.Logged);   // the attempt is a recorded act even when there was nothing to remove
    }
}
