using Adelaide.Core;
using Adelaide.Web;
using Xunit;

namespace Adelaide.Web.Tests;

// The deployment-policy half of the stalled-document gauge: the threshold and the /api/stats shaping. The store half
// (which documents count as stalled, and why human-pending ones never do) is pinned in Adelaide.Audit.Tests.
public class StallGaugeTests
{
    [Fact]
    public void The_threshold_default_and_its_bounds_hold()
    {
        // 60 minutes for a document being worked on, 1440 for one queued behind the intake channel — the two
        // populations measured three orders of magnitude apart (see AuditQueryPredicates.Queued). Both overrides
        // are BOUNDED so a typo cannot quietly disable the gauge: 0 or a negative would place the cutoff at or
        // after "now" and mark every live document stalled; an enormous value would place it in the distant past
        // and mark none ever stalled. Both fail silently — the endpoint keeps answering, just uselessly — which is
        // exactly why the parse refuses them instead of clamping.
        var prior = Environment.GetEnvironmentVariable("ADELAIDE_STALL_MINUTES");
        var priorQueued = Environment.GetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES");
        try
        {
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_MINUTES", null);
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES", null);
            Assert.Equal(60, StallGauge.Minutes);
            Assert.Equal(1440, StallGauge.QueuedMinutes);

            // The tight default MUST stay below the loose one, or the two-tier shape is inverted and a queued
            // document would alarm sooner than a mid-pipeline one — the exact opposite of what the data says.
            Assert.True(StallGauge.Minutes < StallGauge.QueuedMinutes);

            Environment.SetEnvironmentVariable("ADELAIDE_STALL_MINUTES", "15");
            Assert.Equal(15, StallGauge.Minutes);
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_MINUTES", "10080");
            Assert.Equal(10080, StallGauge.Minutes);   // the upper bound itself is allowed

            foreach (var bad in new[] { "0", "-5", "10081", "99999", "soon", "" })
            {
                Environment.SetEnvironmentVariable("ADELAIDE_STALL_MINUTES", bad);
                Assert.Equal(60, StallGauge.Minutes);
            }
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_MINUTES", null);

            Environment.SetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES", "240");
            Assert.Equal(240, StallGauge.QueuedMinutes);
            foreach (var bad in new[] { "0", "-5", "10081", "later", "" })
            {
                Environment.SetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES", bad);
                Assert.Equal(1440, StallGauge.QueuedMinutes);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_MINUTES", prior);
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES", priorQueued);
        }
    }

    [Fact]
    public async Task The_snapshot_reports_the_thresholds_its_cutoffs_were_computed_from()
    {
        // A gauge whose numbers and whose thresholds could disagree is unreadable, and under two tiers doubly so:
        // the same age means "stalled" in one lane and "still queued" in another, so a count published beside the
        // wrong pair is actively misleading. Each is read ONCE and used for both its cutoff and its published
        // value, so a mid-request env change cannot split them.
        var prior = Environment.GetEnvironmentVariable("ADELAIDE_STALL_MINUTES");
        var priorQueued = Environment.GetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES");
        try
        {
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_MINUTES", "45");
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES", "600");
            var now = DateTimeOffset.UtcNow;
            var store = new CutoffRecordingStore();

            var snap = await StallGauge.SnapshotAsync(store, now, default);

            Assert.Equal(now.AddMinutes(-45), store.ActiveCutoff);
            Assert.Equal(now.AddMinutes(-600), store.QueuedCutoff);
            Assert.Equal(45, Field<int>(snap, "activeThresholdMinutes"));
            Assert.Equal(600, Field<int>(snap, "queuedThresholdMinutes"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_MINUTES", prior);
            Environment.SetEnvironmentVariable("ADELAIDE_STALL_QUEUED_MINUTES", priorQueued);
        }
    }

    [Fact]
    public async Task An_empty_gauge_publishes_nulls_rather_than_a_zero_age()
    {
        // Nothing stalled must not read as "something stalled zero minutes ago". Null is the honest absence.
        var snap = await StallGauge.SnapshotAsync(new CutoffRecordingStore(), DateTimeOffset.UtcNow, default);

        Assert.Equal(0, Field<int>(snap, "count"));
        Assert.Null(Field<DateTimeOffset?>(snap, "oldestUtc"));
        Assert.Null(Field<string>(snap, "oldestState"));
        Assert.Null(Field<int?>(snap, "oldestAgeMinutes"));
        Assert.Empty(Field<IReadOnlyList<string>>(snap, "sample"));
    }

    [Fact]
    public async Task The_published_age_is_measured_from_the_same_now_as_the_cutoff()
    {
        var oldest = DateTimeOffset.UtcNow.AddMinutes(-500);
        var now = DateTimeOffset.UtcNow;
        var store = new CutoffRecordingStore(new StalledDocuments(
            2, new Dictionary<string, int> { ["Transcribed"] = 2 }, oldest, "Transcribed",
            ["abc123def456", "789ghi012jkl"]));

        var snap = await StallGauge.SnapshotAsync(store, now, default);

        Assert.Equal(2, Field<int>(snap, "count"));
        Assert.Equal(oldest, Field<DateTimeOffset?>(snap, "oldestUtc"));
        Assert.Equal("Transcribed", Field<string>(snap, "oldestState"));
        Assert.Equal((int)(now - oldest).TotalMinutes, Field<int?>(snap, "oldestAgeMinutes"));
        Assert.Equal(["abc123def456", "789ghi012jkl"], Field<IReadOnlyList<string>>(snap, "sample"));
    }

    private static T Field<T>(object anon, string name) =>
        (T)anon.GetType().GetProperty(name)!.GetValue(anon)!;

    /// <summary>Captures the cutoff the gauge computed, so the threshold→cutoff arithmetic is asserted DIRECTLY
    /// rather than inferred from a count that a wrong-by-an-hour cutoff would often still produce.
    ///
    /// Every other member throws rather than returning a default, which also pins something worth pinning: building
    /// this block costs the store exactly ONE query. /api/stats is polled by the dashboard, so a future edit that
    /// quietly added a second round trip here should break a test, not just get slower.</summary>
    private sealed class CutoffRecordingStore(StalledDocuments? result = null) : IAuditStore
    {
        private readonly StalledDocuments _result = result ?? StalledDocuments.None;
        public DateTimeOffset? ActiveCutoff { get; private set; }
        public DateTimeOffset? QueuedCutoff { get; private set; }

        public Task<StalledDocuments> CountStalledAsync(DateTimeOffset activeOlderThan, DateTimeOffset queuedOlderThan,
            CancellationToken ct = default)
        {
            (ActiveCutoff, QueuedCutoff) = (activeOlderThan, queuedOlderThan);
            return Task.FromResult(_result);
        }

        private static Task<T> No<T>([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
            throw new NotSupportedException($"the stall gauge must not call {member}");

        public Task<AuditEvent> AppendAsync(AuditEntry e, CancellationToken ct = default) => No<AuditEvent>();
        public Task<DocumentAudit?> GetDocumentAsync(string id, CancellationToken ct = default) => No<DocumentAudit?>();
        public Task<IReadOnlyList<DocumentAudit>> ListDocumentsAsync(int limit = 5000, CancellationToken ct = default) => No<IReadOnlyList<DocumentAudit>>();
        public Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string id, CancellationToken ct = default) => No<IReadOnlyList<AuditEvent>>();
        public Task<ChainVerification> VerifyChainAsync(CancellationToken ct = default) => No<ChainVerification>();
        public Task<IReadOnlyList<DocumentAudit>> ListActiveAsync(int limit = 300, CancellationToken ct = default) => No<IReadOnlyList<DocumentAudit>>();
        public Task<IReadOnlyList<DocumentAudit>> ListReconcilableAsync(int limit = 100_000, CancellationToken ct = default) => No<IReadOnlyList<DocumentAudit>>();
        public Task<IReadOnlyList<DocumentAudit>> ListByStateAsync(string state, int limit = 5000, CancellationToken ct = default) => No<IReadOnlyList<DocumentAudit>>();
        public Task<IReadOnlyList<DocumentAudit>> ListDuplicatesAsync(int limit = 5000, CancellationToken ct = default) => No<IReadOnlyList<DocumentAudit>>();
        public Task<int> CountDuplicatesAsync(CancellationToken ct = default) => No<int>();
        public Task<IReadOnlyDictionary<string, int>> CountsByStateAsync(CancellationToken ct = default) => No<IReadOnlyDictionary<string, int>>();
        public Task<DocumentAudit?> FindBySha256Async(string sha, string exclude, CancellationToken ct = default) => No<DocumentAudit?>();
        public Task<DocumentAudit?> FindFiledBySha256Async(string sha, string exclude, CancellationToken ct = default) => No<DocumentAudit?>();
        public Task<DocumentAudit?> FindBySourceRecordIdAsync(string srid, string exclude, CancellationToken ct = default) => No<DocumentAudit?>();
        public Task<DocumentAudit?> FindFiledByFileNamePrefixAsync(string prefix, string exclude, CancellationToken ct = default) => No<DocumentAudit?>();
        public Task<string?> FindDocumentIdByDetailAsync(string needle, CancellationToken ct = default) => No<string?>();
        public Task<string?> FindDocumentIdByNameAsync(string needle, CancellationToken ct = default) => No<string?>();
        public Task<int> CountDocumentsWithEventSinceAsync(string type, DateTimeOffset since, CancellationToken ct = default) => No<int>();
        public Task<int> CountReviewedThenFiledSinceAsync(DateTimeOffset since, CancellationToken ct = default) => No<int>();
    }
}
