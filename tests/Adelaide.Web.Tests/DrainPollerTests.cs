using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Adelaide.Audit;
using Adelaide.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

// C7′ — the in-app staging-share poller, tested at its seams: a fake staging source + the real ingestion core +
// real (temp SQLite) stores. What these pin (several demanded by adversarial verification): capture-BEFORE-delete
// as an order, not just an end state; invalid input becoming a poison record that OWNS its bytes (retry works);
// malware staying quarantined at the source; the half-written-record heal; the settle guard; listing-size oversize
// handling; SAS-free Describe(); and the health gauge's honesty about per-file and global failures.
[Collection("inbox-env")] // serialized with OutboxInvariantTests: both swap the process-wide ADELAIDE_INBOX env var
public sealed class DrainPollerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"adelaide-poller-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly string? _prevInbox;

    public DrainPollerTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "audit.db");
        _prevInbox = Environment.GetEnvironmentVariable("ADELAIDE_INBOX");
        Environment.SetEnvironmentVariable("ADELAIDE_INBOX", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ADELAIDE_INBOX", _prevInbox);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly byte[] Pdf = "%PDF-1.4 poller test"u8.ToArray();
    private static readonly byte[] Pdf2 = "%PDF-1.4 different content"u8.ToArray();

    private SqliteAuditStore NewStore() => new(_db);

    private static (DrainPoller Poller, DrainPollerHealth Health, Channel<UploadJob> Queue) Build(
        IAuditStore store, long maxBytes = 25L * 1024 * 1024, IMalwareScanner? scanner = null)
    {
        var health = new DrainPollerHealth();
        var queue = Channel.CreateUnbounded<UploadJob>();
        var poller = new DrainPoller(null, null, null, health, store, store, queue, new KeyGate(),
            scanner ?? new DisabledMalwareScanner(), maxBytes, NullLogger<DrainPoller>.Instance);
        return (poller, health, queue);
    }

    [Fact]
    public async Task An_unknown_file_is_captured_durably_BEFORE_the_staging_copy_is_deleted()
    {
        using var store = NewStore();
        var (poller, _, queue) = Build(store);
        var file = new FakeFile("Scanner/a.pdf", Pdf);
        // ORDER pin (not just end state): at the moment of the staging delete, the inbox must already own the pdf.
        file.OnDelete = () => Assert.NotEmpty(Directory.GetFiles(_dir, "*.pdf"));
        var src = new FakeSource { Files = { file } };

        await poller.TickAsync(src, CancellationToken.None);

        var doc = await store.FindBySourceRecordIdAsync("Scanner/a.pdf", "");
        Assert.NotNull(doc);
        Assert.Equal("Received", doc!.State);
        Assert.True(File.Exists(Path.Combine(_dir, $"{doc.DocumentId}.pdf")));
        Assert.True(file.Deleted);
        Assert.True(queue.Reader.TryRead(out var job));
        Assert.Equal(SourceChannel.Drain, job!.Channel);
        Assert.Equal("Scanner/a.pdf", job.SourceRecordId);
        var received = (await store.GetEventsAsync(doc.DocumentId)).Single(e => e.Type == "Received");
        Assert.Equal("drain-poller", received.Actor);                             // the poll path's provenance marker
        using var d = System.Text.Json.JsonDocument.Parse(received.Details);
        Assert.Equal("Drain", d.RootElement.GetProperty("channel").GetString());  // ledger continuity with the HTTP drain
    }

    [Fact]
    public async Task A_failed_staging_delete_resolves_to_dedup_next_tick_and_the_delete_is_retried()
    {
        using var store = NewStore();
        var (poller, _, queue) = Build(store);
        var file = new FakeFile("Scanner/b.pdf", Pdf) { FailDelete = true };
        var src = new FakeSource { Files = { file } };

        await poller.TickAsync(src, CancellationToken.None);         // ingested, delete failed → file remains
        Assert.False(file.Deleted);
        file.FailDelete = false;
        await poller.TickAsync(src, CancellationToken.None);         // content dedup → delete retried

        Assert.True(file.Deleted);
        Assert.True(queue.Reader.TryRead(out _));
        Assert.False(queue.Reader.TryRead(out _));                    // exactly ONE job — never double-ingested
    }

    [Fact]
    public async Task A_reused_path_with_different_content_ingests_a_new_document()
    {
        using var store = NewStore();
        var (poller, _, queue) = Build(store);
        var src = new FakeSource { Files = { new FakeFile("Scanner/c.pdf", Pdf) } };
        await poller.TickAsync(src, CancellationToken.None);
        src.Files.Add(new FakeFile("Scanner/c.pdf", Pdf2));           // same share path, new paper
        await poller.TickAsync(src, CancellationToken.None);

        Assert.True(queue.Reader.TryRead(out var first));
        Assert.True(queue.Reader.TryRead(out var second));            // both ingested — path reuse never silently drops
        Assert.NotEqual(first!.DocId, second!.DocId);
    }

    [Fact]
    public async Task Invalid_input_becomes_a_poison_record_that_OWNS_its_bytes_so_retry_can_work()
    {
        using var store = NewStore();
        var (poller, _, queue) = Build(store);
        var file = new FakeFile("Scanner/empty.pdf", []);
        var src = new FakeSource { Files = { file } };

        await poller.TickAsync(src, CancellationToken.None);

        var doc = await store.FindBySourceRecordIdAsync("Scanner/empty.pdf", "");
        Assert.NotNull(doc);
        Assert.Equal("DeadLetter", doc!.State);
        Assert.True(File.Exists(Path.Combine(_dir, $"{doc.DocumentId}.pdf")));  // bytes persisted → /api/retry works
        Assert.True(file.Deleted);                                    // Adelaide owns record AND bytes → staging goes
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Malware_is_recorded_but_never_persisted_and_the_source_copy_stays_as_quarantine()
    {
        using var store = NewStore();
        var (poller, health, queue) = Build(store, scanner: new BlockingScanner());
        var file = new FakeFile("Scanner/bad.pdf", Pdf);
        var src = new FakeSource { Files = { file } };

        await poller.TickAsync(src, CancellationToken.None);

        var doc = await store.FindBySourceRecordIdAsync("Scanner/bad.pdf", "");
        Assert.NotNull(doc);
        Assert.Equal("DeadLetter", doc!.State);                       // the verdict is on the ledger…
        Assert.False(File.Exists(Path.Combine(_dir, $"{doc.DocumentId}.pdf"))); // …but flagged bytes never touch the inbox
        Assert.False(file.Deleted);                                   // the source copy IS the quarantine
        Assert.False(queue.Reader.TryRead(out _));
        Assert.Contains("\"pendingFiles\":1", System.Text.Json.JsonSerializer.Serialize(health.Snapshot()));
    }

    [Fact]
    public async Task A_half_written_poison_record_is_healed_on_the_same_document()
    {
        using var store = NewStore();
        // The crack: Received landed, the DeadLetter append failed, no bytes persisted. Without the heal this
        // doc was immortal (non-terminal, no pdf, no affordance) and the next tick deleted the only bytes.
        var emptySha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])).ToLowerInvariant();
        await store.AppendAsync(new AuditEntry("ghost0000001", "Received", "drain-poller",
            Details: $$"""{"channel":"Drain","original":"g.pdf","sha256":"{{emptySha}}","sourceRecordId":"Scanner/g.pdf"}"""));

        var (poller, _, queue) = Build(store);
        var file = new FakeFile("Scanner/g.pdf", []);
        var src = new FakeSource { Files = { file } };
        await poller.TickAsync(src, CancellationToken.None);

        var doc = await store.FindBySourceRecordIdAsync("Scanner/g.pdf", "");
        Assert.Equal("ghost0000001", doc!.DocumentId);                // healed on the SAME document — no second record
        Assert.Equal("DeadLetter", doc.State);                        // the missing verdict landed
        Assert.True(File.Exists(Path.Combine(_dir, "ghost0000001.pdf")));
        Assert.True(file.Deleted);
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task A_recent_file_is_left_to_settle_and_counted_pending()
    {
        using var store = NewStore();
        var (poller, health, queue) = Build(store);
        var file = new FakeFile("Scanner/fresh.pdf", Pdf) { LastModified = DateTimeOffset.UtcNow };  // mid-upload shape
        var src = new FakeSource { Files = { file } };

        await poller.TickAsync(src, CancellationToken.None);

        Assert.False(file.Deleted);
        Assert.Null(await store.FindBySourceRecordIdAsync("Scanner/fresh.pdf", ""));  // never read, never ingested
        Assert.False(queue.Reader.TryRead(out _));
        Assert.Contains("\"pendingFiles\":1", System.Text.Json.JsonSerializer.Serialize(health.Snapshot()));
    }

    [Fact]
    public async Task An_oversize_file_is_skipped_from_the_listing_without_a_download_and_its_age_is_reported()
    {
        using var store = NewStore();
        var (poller, health, queue) = Build(store, maxBytes: 4);
        var file = new FakeFile("Scanner/big.pdf", Pdf);              // ListedSize (20) > cap (4)
        var src = new FakeSource { Files = { file } };

        await poller.TickAsync(src, CancellationToken.None);

        Assert.False(file.Deleted);
        Assert.False(file.WasRead);                                   // judged from the listing — zero transfer
        Assert.Null(await store.FindBySourceRecordIdAsync("Scanner/big.pdf", ""));
        Assert.False(queue.Reader.TryRead(out _));
        using var snap = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(health.Snapshot()));
        Assert.Equal(1, snap.RootElement.GetProperty("pendingFiles").GetInt32());
        var age = snap.RootElement.GetProperty("oldestPendingAgeMinutes").GetDouble();
        Assert.InRange(age, 25, 35);                                  // FakeFile's LastModified is ~30 min ago
    }

    [Fact]
    public async Task A_global_listing_failure_is_loud_and_preserves_the_previous_pending_picture()
    {
        using var store = NewStore();
        var (poller, health, queue) = Build(store, maxBytes: 4);
        var src = new FakeSource { Files = { new FakeFile("Scanner/big.pdf", Pdf) } };
        await poller.TickAsync(src, CancellationToken.None);          // establishes pendingFiles=1 (oversize)

        src.ThrowOnList = true;
        await poller.TickAsync(src, CancellationToken.None);          // must not throw…

        var json = System.Text.Json.JsonSerializer.Serialize(health.Snapshot());
        Assert.Contains("\"lastError\":\"InvalidOperationException\"", json);
        Assert.Contains("\"pendingFiles\":1", json);                  // …and must not zero the backlog picture
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Per_file_failures_mark_the_tick_unhealthy()
    {
        using var store = NewStore();
        var (poller, health, _) = Build(store);
        var src = new FakeSource { Files = { new FakeFile("Scanner/x.pdf", Pdf) { FailRead = true } } };

        await poller.TickAsync(src, CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(health.Snapshot());
        Assert.Contains("\"failedFiles\":1", json);
        Assert.Contains("per-file failure", json);                    // a read-denied SAS must not look healthy
        Assert.Contains("\"lastSuccessUtc\":null", json);
    }

    [Fact]
    public void Describe_never_contains_the_sas()
    {
        var src = new AzureFilesStagingSource(new Uri("https://x.file.core.windows.net/scans?sv=1&sig=TOPSECRET"), "Scanner");
        Assert.Equal("https://x.file.core.windows.net/scans/Scanner", src.Describe());
        Assert.DoesNotContain("TOPSECRET", src.Describe());
    }

    [Fact]
    public void Options_are_configured_or_off_and_a_broken_switch_is_distinguishable()
    {
        var prevUrl = Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_URL");
        var prevSub = Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_SUBDIR");
        var prevMin = Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_POLL_MINUTES");
        try
        {
            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_URL", null);
            Assert.Equal((null, null), DrainPollerOptions.FromEnv());
            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_URL", "   ");
            Assert.Equal((null, null), DrainPollerOptions.FromEnv());

            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_URL", "not a url");
            var (offOptions, error) = DrainPollerOptions.FromEnv();
            Assert.Null(offOptions);
            Assert.NotNull(error);                                     // set-but-broken ≠ deliberately off (T-0!)

            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_URL", "https://x.file.core.windows.net/scans?sv=fake");
            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_SUBDIR", "/Scanner/");
            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_POLL_MINUTES", "abc");
            var (opt, none) = DrainPollerOptions.FromEnv();
            Assert.Null(none);
            Assert.Equal("Scanner", opt!.Subdir);
            Assert.Equal(5, opt.PollMinutes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_URL", prevUrl);
            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_SUBDIR", prevSub);
            Environment.SetEnvironmentVariable("ADELAIDE_DRAIN_POLL_MINUTES", prevMin);
        }
    }

    // ---- fakes --------------------------------------------------------------------------------------------------

    private sealed class BlockingScanner : IMalwareScanner
    {
        public Task<MalwareDecision> ScanAsync(byte[] bytes, CancellationToken ct = default) =>
            Task.FromResult(new MalwareDecision(false, "EICAR (test)", ScanVerdict.Infected));
    }

    private sealed class FakeSource : IStagingSource
    {
        public List<FakeFile> Files { get; init; } = new();
        public bool ThrowOnList { get; set; }
        public string Describe() => "fake://staging";
        public async IAsyncEnumerable<IStagingFile> ListAsync([EnumeratorCancellation] CancellationToken ct)
        {
            if (ThrowOnList) throw new InvalidOperationException("share down (test)");
            foreach (var f in Files.ToList())
                if (!f.Deleted) yield return f;
            await Task.CompletedTask;
        }
    }

    private sealed class FakeFile(string srid, byte[] bytes) : IStagingFile
    {
        public bool Deleted { get; private set; }
        public bool FailDelete { get; set; }
        public bool FailRead { get; set; }
        public bool WasRead { get; private set; }
        public Action? OnDelete { get; set; }
        public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow.AddMinutes(-30);
        public string SourceRecordId => srid;
        public string FileName => Path.GetFileName(srid);
        public long? ListedSize => bytes.LongLength;
        public Task<byte[]?> ReadAsync(long maxBytes, CancellationToken ct)
        {
            if (FailRead) throw new UnauthorizedAccessException("read denied (test)");
            WasRead = true;
            return Task.FromResult<byte[]?>(bytes.LongLength > maxBytes ? null : bytes);
        }
        public Task DeleteAsync(CancellationToken ct)
        {
            if (FailDelete) throw new IOException("delete blocked (test)");
            OnDelete?.Invoke();
            Deleted = true;
            return Task.CompletedTask;
        }
        public Task<DateTimeOffset?> LastModifiedAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(LastModified);
    }
}
