using Adelaide.Audit;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Audit.Tests;

// The deletable transcript store: independently erasable per document and in bulk by age, and entirely outside the
// hash chain. Deletion being ordinary and total is the property that makes retention defensible at all.
public sealed class TranscriptStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "adelaide-tstore-" + Guid.NewGuid().ToString("n")[..8]);
    private TranscriptStore Store(string name = "t.db") => new(Path.Combine(_dir, name));
    public TranscriptStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private static string Sha(string s) => TranscriptDigest.Of(s);

    [Fact]
    public async Task Round_trips_a_transcript()
    {
        var s = Store();
        await s.PutAsync("d1", "hello world", Sha("hello world"), DateTimeOffset.UtcNow);
        var got = await s.GetAsync("d1");
        Assert.Equal("hello world", got!.Value.Transcript);
        Assert.Equal(Sha("hello world"), got.Value.Sha256);
    }

    [Fact]
    public async Task An_unretained_document_reads_back_as_null()
        => Assert.Null(await Store().GetAsync("never-stored"));

    // Reprocessing overwrites rather than accumulating — one row per document, so erasure by id is complete.
    [Fact]
    public async Task Storing_the_same_document_twice_replaces_it()
    {
        var s = Store();
        await s.PutAsync("d1", "first", Sha("first"), DateTimeOffset.UtcNow.AddDays(-1));
        await s.PutAsync("d1", "second", Sha("second"), DateTimeOffset.UtcNow);
        Assert.Equal(1, await s.CountAsync());
        Assert.Equal("second", (await s.GetAsync("d1"))!.Value.Transcript);
    }

    [Fact]
    public async Task Deleting_one_document_leaves_the_others()
    {
        var s = Store();
        await s.PutAsync("d1", "a", Sha("a"), DateTimeOffset.UtcNow);
        await s.PutAsync("d2", "b", Sha("b"), DateTimeOffset.UtcNow);
        Assert.True(await s.DeleteAsync("d1"));
        Assert.Null(await s.GetAsync("d1"));
        Assert.NotNull(await s.GetAsync("d2"));
    }

    [Fact]
    public async Task Deleting_an_absent_document_reports_false_and_is_not_an_error()
        => Assert.False(await Store().DeleteAsync("nope"));

    // ---- bulk purge by age ----

    [Fact]
    public async Task Purge_removes_only_rows_past_the_cutoff()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.PutAsync("old", "x", Sha("x"), now.AddDays(-40));
        await s.PutAsync("edge", "x", Sha("x"), now.AddDays(-30).AddMinutes(1)); // just inside a 30-day window
        await s.PutAsync("new", "x", Sha("x"), now);

        var removed = await s.PurgeOlderThanAsync(new TranscriptRetentionPolicy(RetentionDays: 30).CutoffFrom(now));
        Assert.Equal(1, removed);
        Assert.Null(await s.GetAsync("old"));
        Assert.NotNull(await s.GetAsync("edge"));
        Assert.NotNull(await s.GetAsync("new"));
    }

    [Fact]
    public async Task Purge_on_an_empty_store_removes_nothing()
        => Assert.Equal(0, await Store().PurgeOlderThanAsync(DateTimeOffset.UtcNow));

    // ---- attempt marks (the redrive bound's evidence that processing actually began) ----

    // Counted strictly after the anchor (the document's last real ingest), removed by ForgetAsync.
    [Fact]
    public async Task Attempt_marks_count_since_anchor_and_forget_removes_them()
    {
        var s = Store();
        var t0 = DateTimeOffset.UtcNow;
        await s.MarkAttemptStartAsync("d", t0.AddMinutes(-10));
        await s.MarkAttemptStartAsync("d", t0.AddMinutes(-5));
        await s.MarkAttemptStartAsync("other", t0);
        Assert.Equal(2, await s.AttemptStartsSinceAsync("d", DateTimeOffset.MinValue));
        Assert.Equal(1, await s.AttemptStartsSinceAsync("d", t0.AddMinutes(-7)));   // the anchor excludes earlier marks
        Assert.Equal(0, await s.AttemptStartsSinceAsync("d", t0));
        await s.ForgetAsync("d");
        Assert.Equal(0, await s.AttemptStartsSinceAsync("d", DateTimeOffset.MinValue));
        Assert.Equal(1, await s.AttemptStartsSinceAsync("other", DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task The_window_purge_ages_out_attempt_marks()
    {
        var s = Store();
        var now = DateTimeOffset.UtcNow;
        await s.MarkAttemptStartAsync("d", now.AddDays(-40));
        await s.MarkAttemptStartAsync("d", now);
        await s.PurgeOlderThanAsync(new TranscriptRetentionPolicy(RetentionDays: 30).CutoffFrom(now));
        Assert.Equal(1, await s.AttemptStartsSinceAsync("d", DateTimeOffset.MinValue));
    }

    // The store lands beside the audit DB, so retained text shares its volume and residency boundary.
    [Fact]
    public void The_default_path_is_a_sibling_of_the_audit_database()
    {
        var p = TranscriptStore.DefaultPathFor(Path.Combine("C:", "data", "adelaide-audit.db"));
        Assert.Equal(Path.GetFullPath(Path.Combine("C:", "data")), Path.GetDirectoryName(Path.GetFullPath(p)));
        Assert.Equal("adelaide-transcripts.db", Path.GetFileName(p));
    }

    // It must be a DIFFERENT file from the ledger — the separation is the design, not an implementation detail.
    [Fact]
    public void The_store_is_not_the_audit_database()
    {
        var audit = Path.Combine("C:", "data", "adelaide-audit.db");
        Assert.NotEqual(Path.GetFullPath(audit), Path.GetFullPath(TranscriptStore.DefaultPathFor(audit)));
    }
}
