using System.Threading.Channels;
using Adelaide.Audit;
using Adelaide.Core;
using Adelaide.SharePoint;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

/// <summary>C5 (grace-window purge): the pure gate parse, and the aged sweep core — the ledger decides (Filed +
/// driveItemId + older than the window), SharePoint presence is re-verified before any delete, and a claim held by
/// a live path keeps the sweep out. Uses the shared inbox-env collection: ADELAIDE_INBOX is process-global.</summary>
[Collection("inbox-env")]
public class FiledPurgeGraceTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-grace-{Guid.NewGuid():N}.db");
    private readonly string _inbox = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"adelaide-grace-in-{Guid.NewGuid():N}")).FullName;
    private readonly string? _priorInbox = Environment.GetEnvironmentVariable("ADELAIDE_INBOX");

    public FiledPurgeGraceTests() => Environment.SetEnvironmentVariable("ADELAIDE_INBOX", _inbox);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ADELAIDE_INBOX", _priorInbox);
        try { File.Delete(_db); } catch { }
        try { Directory.Delete(_inbox, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData(" 14 ", 14)]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("abc", 0)]
    [InlineData("2.5", 0)]
    public void DaysFrom_accepts_positive_integer_days_only(string? value, int expected) =>
        Assert.Equal(expected, FiledPurgeGrace.DaysFrom(value));

    private (UploadProcessor Proc, SqliteAuditStore Store, FilingClaims Claims, ExistenceFiler Filer) Build(bool registerFiler = false)
    {
        var store = new SqliteAuditStore(_db);
        var claims = new FilingClaims();
        var filer = new ExistenceFiler();
        var services = new ServiceCollection();
        if (registerFiler) services.AddSingleton<IFiler>(filer); // only the gate-explicit sweep WRAPPER resolves IFiler from the scope
        var sp = services.BuildServiceProvider();
        var proc = new UploadProcessor(Channel.CreateUnbounded<UploadJob>(), sp.GetRequiredService<IServiceScopeFactory>(),
            store, store, claims, NullLogger<UploadProcessor>.Instance);
        return (proc, store, claims, filer);
    }

    private async Task<string> SeedFiledDocAsync(SqliteAuditStore store, string docId, string driveItemId = "drv-1")
    {
        var pdf = Path.Combine(_inbox, $"{docId}.pdf");
        await File.WriteAllBytesAsync(pdf, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));
        await File.WriteAllTextAsync(Path.ChangeExtension(pdf, ".review.json"), "{}");
        await store.AppendAsync(new AuditEntry(docId, "Received", "test", Details: """{"channel":"Upload","sha256":"aa"}"""));
        await store.AppendAsync(new AuditEntry(docId, "Filed", "sharepoint",
            Details: $$"""{"contentType":"Verordnung","disposition":"AutoFile","driveItemId":"{{driveItemId}}"}"""));
        return pdf;
    }

    [Fact]
    public async Task Aged_filed_doc_is_purged_with_its_sidecar()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedFiledDocAsync(store, "aged00000001");
            // The append stamped LastUpdated = now, so age the DOC by moving "now" forward past the window
            // (backdating a ledger append is impossible by design — the store stamps time itself).
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.False(File.Exists(pdf));
            Assert.False(File.Exists(Path.ChangeExtension(pdf, ".review.json")));
            Assert.Equal("drv-1", Assert.Single(filer.ExistenceChecks)); // verified against SharePoint before the delete
        }
    }

    [Fact]
    public async Task Young_filed_doc_is_kept()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedFiledDocAsync(store, "young0000001");
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(File.Exists(pdf)); // filed minutes ago — well inside the window
        }
    }

    [Fact]
    public async Task Non_filed_doc_is_kept_even_when_old()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var pdf = Path.Combine(_inbox, "review000001.pdf");
            await File.WriteAllBytesAsync(pdf, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));
            await store.AppendAsync(new AuditEntry("review000001", "NeedsReview", "gate"));
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.True(File.Exists(pdf));               // the ledger says it is not Filed — never touched
            Assert.Empty(filer.ExistenceChecks);         // and SharePoint was never even consulted
        }
    }

    [Fact]
    public async Task Unverified_in_sharepoint_is_kept()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedFiledDocAsync(store, "gone00000001");
            filer.ExistsResult = false; // the item is NOT confirmable in SharePoint — verify-before-delete keeps the copy
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.True(File.Exists(pdf));
        }
    }

    [Fact]
    public async Task Claimed_doc_is_left_to_the_live_path()
    {
        var (proc, store, claims, filer) = Build();
        using (store)
        {
            var pdf = await SeedFiledDocAsync(store, "held00000001");
            using var held = claims.Acquire("doc:held00000001"); // a live path (reclassify/filing) owns it right now
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.True(File.Exists(pdf));
            Assert.Empty(filer.ExistenceChecks);
        }
    }

    /// <summary>The mutation-killer for the ledger-age clause (verification finding: deleting
    /// `aged.LastUpdated > cutoff` left the whole file green): an OLD file mtime defeats the prefilter, so the
    /// ledger clause is the SOLE keeper of a doc that sat in review for weeks and was filed minutes ago — exactly
    /// the misfile-repair window C5 exists to provide.</summary>
    [Fact]
    public async Task Recently_filed_doc_with_an_old_file_mtime_is_kept_by_the_ledger_clause_alone()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedFiledDocAsync(store, "oldmtime0001");
            File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddDays(-40)); // captured long ago; filed JUST NOW (LastUpdated = now)
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(File.Exists(pdf));               // LastUpdated > cutoff — the filing is inside its window
            Assert.Empty(filer.ExistenceChecks);         // and SharePoint was never consulted
        }
    }

    [Fact]
    public void Shipped_default_posture_is_purge_at_filing()
    {
        // The suite runs without ADELAIDE_FILED_PURGE_GRACE_DAYS: the window must be OFF by default (pre-C5 behavior).
        Assert.Equal(0, FiledPurgeGrace.Days);
        Assert.False(FiledPurgeGrace.Deferred);
    }

    /// <summary>The gate matrix through the gate-explicit overloads (the statics are read-once, so tests inject
    /// the decisions — production resolves them from PurgeGate/FiledPurgeGrace via the parameterless wrappers).</summary>
    [Fact]
    public async Task Inline_purge_gate_matrix()
    {
        var (_, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedFiledDocAsync(store, "gatematrix01");

            // Kill-switch closed → nothing verified, nothing deleted.
            await UploadProcessor.PurgeFiledInputsAsync(filer, "gatematrix01", pdf, "drv-1", null, NullLogger.Instance, CancellationToken.None, purgeEnabled: false, graceDeferred: false);
            Assert.True(File.Exists(pdf));
            Assert.Empty(filer.ExistenceChecks);

            // Armed but grace-deferred → the inline purge must NO-OP (the aged sweep owns deletion).
            await UploadProcessor.PurgeFiledInputsAsync(filer, "gatematrix01", pdf, "drv-1", null, NullLogger.Instance, CancellationToken.None, purgeEnabled: true, graceDeferred: true);
            Assert.True(File.Exists(pdf));
            Assert.Empty(filer.ExistenceChecks);

            // Armed, no grace → the verify-and-delete core runs.
            await UploadProcessor.PurgeFiledInputsAsync(filer, "gatematrix01", pdf, "drv-1", null, NullLogger.Instance, CancellationToken.None, purgeEnabled: true, graceDeferred: false);
            Assert.False(File.Exists(pdf));
            Assert.Equal("drv-1", Assert.Single(filer.ExistenceChecks));
        }
    }

    [Fact]
    public async Task Sweep_wrapper_gate_matrix()
    {
        var (proc, store, _, filer) = Build(registerFiler: true);
        using (store)
        {
            var pdf = await SeedFiledDocAsync(store, "sweepgate0001");
            File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddDays(-40));

            await proc.PurgeAgedFiledInputsAsync(purgeEnabled: false, graceDays: 30, CancellationToken.None); // kill-switch closed
            await proc.PurgeAgedFiledInputsAsync(purgeEnabled: true, graceDays: 0, CancellationToken.None);   // no window configured
            Assert.True(File.Exists(pdf));
            Assert.Empty(filer.ExistenceChecks);
        }
    }

    [Fact]
    public async Task Core_reports_only_verified_deletes()
    {
        var (_, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedFiledDocAsync(store, "count00000001");
            filer.ExistsResult = false; // unverifiable → the core must refuse AND say so (the sweep's release count depends on it)
            Assert.False(await UploadProcessor.PurgeFiledInputsCoreAsync(filer, "count00000001", pdf, "drv-1", null, NullLogger.Instance, CancellationToken.None));
            Assert.True(File.Exists(pdf));

            filer.ExistsResult = true;
            Assert.True(await UploadProcessor.PurgeFiledInputsCoreAsync(filer, "count00000001", pdf, "drv-1", null, NullLogger.Instance, CancellationToken.None));
            Assert.False(File.Exists(pdf));
        }
    }

    /// <summary>Superseded docs' grace-deferred inputs join the AGED SWEEP (verification finding + design decision):
    /// a supersede is a human correcting a mistake — exactly the event class the grace window insures — so a
    /// supersede-in-error keeps its arrival copy for the same window an ordinary misfile gets. After the window the
    /// sweep releases it, verified via the SUCCESSOR's driveItemId (the old item is intentionally gone).</summary>
    [Fact]
    public async Task Superseded_docs_inputs_get_the_window_then_release_via_the_successor()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var oldPdf = await SeedFiledDocAsync(store, "oldsuper0001", driveItemId: "drv-old");
            var newPdf = Path.Combine(_inbox, "newsuper0001.pdf");
            await File.WriteAllBytesAsync(newPdf, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));

            // The supersede itself deletes only the SharePoint item — never the deferred arrival copy.
            var intent = new UploadProcessor.SupersedeIntent("oldsuper0001", "drv-old", "newsuper0001", "reviewer", "vom Nutzer ersetzt");
            await UploadProcessor.DriveSupersedeAsync(filer, store, newPdf, intent, NullLogger.Instance, CancellationToken.None);
            Assert.Equal("Superseded", (await store.GetDocumentAsync("oldsuper0001"))!.State);
            Assert.Equal("drv-old", Assert.Single(filer.DeleteCalls));
            Assert.True(File.Exists(oldPdf)); // the insurance copy survives the supersede

            // Inside the window: the sweep keeps it (the supersede-in-error repair period).
            File.SetLastWriteTimeUtc(oldPdf, DateTime.UtcNow.AddDays(-40)); // defeat the mtime prefilter — the ledger decides
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(File.Exists(oldPdf));

            // Aged past the window WITH a Filed successor: released, verified against the successor's item.
            await store.AppendAsync(new AuditEntry("newsuper0001", "Received", "test", Details: """{"channel":"Upload","sha256":"bb"}"""));
            await store.AppendAsync(new AuditEntry("newsuper0001", "Filed", "sharepoint", Details: """{"contentType":"Verordnung","disposition":"AutoFile","driveItemId":"drv-new"}"""));
            File.Delete(newPdf); // the successor's own input is out of the sweep's way (its own purge is separate)
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.False(File.Exists(oldPdf));
            Assert.Contains("drv-new", filer.ExistenceChecks); // verified via the SUCCESSOR, not the deleted old item
            Assert.DoesNotContain("drv-old", filer.ExistenceChecks);
        }
    }

    [Fact]
    public async Task Chained_supersede_releases_via_the_transitive_filed_successor()
    {
        // A→B→C: A's direct successor B was itself superseded — the sweep must walk to C (the Filed end of the
        // chain) rather than strand A's input forever (second verification round).
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var aPdf = await SeedFiledDocAsync(store, "chainaaa0001", driveItemId: "drv-a");
            await store.AppendAsync(new AuditEntry("chainaaa0001", "Superseded", "reviewer",
                Details: """{"supersededBy":"chainbbb0001","driveItemIdDeleted":"drv-a"}"""));
            await store.AppendAsync(new AuditEntry("chainbbb0001", "Received", "test", Details: """{"channel":"Upload","sha256":"bb"}"""));
            await store.AppendAsync(new AuditEntry("chainbbb0001", "Filed", "sharepoint", Details: """{"contentType":"Verordnung","driveItemId":"drv-b"}"""));
            await store.AppendAsync(new AuditEntry("chainbbb0001", "Superseded", "reviewer",
                Details: """{"supersededBy":"chainccc0001","driveItemIdDeleted":"drv-b"}"""));
            await store.AppendAsync(new AuditEntry("chainccc0001", "Received", "test", Details: """{"channel":"Upload","sha256":"cc"}"""));
            await store.AppendAsync(new AuditEntry("chainccc0001", "Filed", "sharepoint", Details: """{"contentType":"Verordnung","driveItemId":"drv-c"}"""));

            File.SetLastWriteTimeUtc(aPdf, DateTime.UtcNow.AddDays(-40));
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.False(File.Exists(aPdf));
            Assert.Contains("drv-c", filer.ExistenceChecks); // verified against the END of the chain
        }
    }

    [Fact]
    public async Task Superseded_doc_without_a_filed_successor_is_kept()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var oldPdf = await SeedFiledDocAsync(store, "orphsuper001", driveItemId: "drv-old");
            await store.AppendAsync(new AuditEntry("orphsuper001", "Superseded", "reviewer",
                Details: """{"supersededBy":"missingdoc01","driveItemIdDeleted":"drv-old"}"""));
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.True(File.Exists(oldPdf));    // no verifiable successor → the copy stays (fail-safe)
            Assert.Empty(filer.ExistenceChecks);
        }
    }

    // ---- Segmented branch (split-parent windowing, 2026-08-16): window-only release, no verify ----

    /// <summary>A committed split parent with the full artifact set: PDF, review sidecar, saved orientation,
    /// and a stray carried pre-transcript — everything the Segmented branch must release together.</summary>
    private async Task<string> SeedSegmentedParentAsync(SqliteAuditStore store, string docId)
    {
        var pdf = Path.Combine(_inbox, $"{docId}.pdf");
        await File.WriteAllBytesAsync(pdf, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));
        await File.WriteAllTextAsync(Path.ChangeExtension(pdf, ".review.json"), "{}");
        await File.WriteAllTextAsync(Path.ChangeExtension(pdf, ".rotation.json"), """{"pages":{"1":90}}""");
        await File.WriteAllTextAsync(Path.ChangeExtension(pdf, ".pretranscript.json"), "{}");
        await store.AppendAsync(new AuditEntry(docId, "NeedsReview", "gate"));
        await store.AppendAsync(new AuditEntry(docId, "Segmented", "user:reviewer", Details: """{"units":2,"unitCount":2}"""));
        return pdf;
    }

    private static string[] ParentArtifacts(string pdf) =>
        [pdf, Path.ChangeExtension(pdf, ".review.json"), Path.ChangeExtension(pdf, ".rotation.json"), Path.ChangeExtension(pdf, ".pretranscript.json")];

    [Fact]
    public async Task Aged_segmented_parent_is_released_window_only_and_sharepoint_is_never_consulted()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedSegmentedParentAsync(store, "segaged00001");
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.All(ParentArtifacts(pdf), p => Assert.False(File.Exists(p)));  // all four released together
            Assert.Empty(filer.ExistenceChecks);                                  // NO verify — nothing durable exists to verify against
        }
    }

    [Fact]
    public async Task A_surviving_split_intent_keeps_everything_the_recovery_pass_needs()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedSegmentedParentAsync(store, "segintent001");
            await File.WriteAllTextAsync(Path.ChangeExtension(pdf, ".split.json"), """{"ranges":[[1,1],[2,2]]}""");
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.All(ParentArtifacts(pdf), p => Assert.True(File.Exists(p)));   // an in-doubt fan-out derives children from this PDF
            Assert.True(File.Exists(Path.ChangeExtension(pdf, ".split.json")));
        }
    }

    [Fact]
    public async Task In_window_segmented_parent_is_kept()
    {
        var (proc, store, _, filer) = Build();
        using (store)
        {
            var pdf = await SeedSegmentedParentAsync(store, "segfresh0001");
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.All(ParentArtifacts(pdf), p => Assert.True(File.Exists(p)));   // the repair window is the whole point
        }
    }

    [Fact]
    public async Task A_held_claim_keeps_the_sweep_off_a_segmented_parent()
    {
        var (proc, store, claims, filer) = Build();
        using (store)
        {
            var pdf = await SeedSegmentedParentAsync(store, "segclaim0001");
            using var held = claims.Acquire("doc:segclaim0001");
            Assert.True(held.Acquired);
            await proc.PurgeAgedFiledInputsCoreAsync(filer, null, graceDays: 30, now: DateTimeOffset.UtcNow.AddDays(40), CancellationToken.None);
            Assert.All(ParentArtifacts(pdf), p => Assert.True(File.Exists(p)));
        }
    }

    [Fact]
    public async Task Segmented_release_is_inert_while_the_kill_switch_is_closed()
    {
        var (proc, store, _, filer) = Build(registerFiler: true);
        using (store)
        {
            var pdf = await SeedSegmentedParentAsync(store, "segdark00001");
            File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddDays(-40));
            await proc.PurgeAgedFiledInputsAsync(purgeEnabled: false, graceDays: 30, CancellationToken.None);
            Assert.All(ParentArtifacts(pdf), p => Assert.True(File.Exists(p)));
        }
    }

    private sealed class ExistenceFiler : IFiler
    {
        public bool ExistsResult = true;
        public List<string> ExistenceChecks { get; } = new();
        public List<string> DeleteCalls { get; } = new();
        public Task<FilingResult> FileAsync(FilingSpec spec, CancellationToken ct = default) => throw new InvalidOperationException("these paths never file");
        public Task<bool> ExistsByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
        {
            ExistenceChecks.Add(driveItemId);
            return Task.FromResult(ExistsResult);
        }
        public Task<bool> TryDeleteByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
        {
            DeleteCalls.Add(driveItemId); // only the supersede path may call this; sweep tests assert it stays empty
            return Task.FromResult(true);
        }
    }
}
