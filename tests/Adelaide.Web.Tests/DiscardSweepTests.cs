using System.Threading.Channels;
using Adelaide.Audit;
using Adelaide.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

/// <summary>The discard-recovery sweep (PurgeAgedDiscardedAsync): ledger-enumerated, delete-then-append,
/// artifact-less rows seal immediately (with the post-kill-switch WARNING), sealed leftovers release without
/// an append, claims and the kill-switch keep it out. Uses the shared inbox-env collection: ADELAIDE_INBOX is
/// process-global. Gate decisions and the clock are injected via the gate-explicit overload — the window
/// boundary never depends on the ambient clock.</summary>
[Collection("inbox-env")]
public class DiscardSweepTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"adelaide-dsweep-{Guid.NewGuid():N}.db");
    private readonly string _inbox = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"adelaide-dsweep-in-{Guid.NewGuid():N}")).FullName;
    private readonly string? _priorInbox = Environment.GetEnvironmentVariable("ADELAIDE_INBOX");

    public DiscardSweepTests() => Environment.SetEnvironmentVariable("ADELAIDE_INBOX", _inbox);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ADELAIDE_INBOX", _priorInbox);
        try { File.Delete(_db); } catch { }
        try { Directory.Delete(_inbox, recursive: true); } catch { }
    }

    private (UploadProcessor Proc, SqliteAuditStore Store, FilingClaims Claims, CapturingLogger Log) Build()
    {
        var store = new SqliteAuditStore(_db);
        var claims = new FilingClaims();
        var log = new CapturingLogger();
        var proc = new UploadProcessor(Channel.CreateUnbounded<UploadJob>(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            store, store, claims, log);
        return (proc, store, claims, log);
    }

    /// <summary>A Discarded doc with a NeedsReview predecessor and, optionally, the full artifact set.</summary>
    private async Task SeedDiscardedAsync(SqliteAuditStore store, string id, bool withArtifacts)
    {
        await store.AppendAsync(new AuditEntry(id, "NeedsReview", "test", Details: """{"reasonCodes":["synthetic"]}"""));
        await store.AppendAsync(new AuditEntry(id, "Discarded", "test", Details: "{}"));
        if (!withArtifacts) return;
        await File.WriteAllBytesAsync(Path.Combine(_inbox, $"{id}.pdf"), System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));
        await File.WriteAllTextAsync(Path.Combine(_inbox, $"{id}.review.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_inbox, $"{id}.rotation.json"), """{"1":90}""");
        await File.WriteAllTextAsync(Path.Combine(_inbox, $"{id}.pretranscript.json"), "{}");
    }

    /// <summary>Backdate the projection's LastUpdated (the sweep's age source — the latest Discarded event's
    /// At) so tests can age a discard without waiting. Raw SQL on the test's own temp DB, same technique the
    /// HTTP fixture uses for its projection-only row.</summary>
    private void BackdateDiscard(string id, DateTimeOffset to)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _db }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE documents SET LastUpdated=$t WHERE DocumentId=$id";
        cmd.Parameters.AddWithValue("$t", to.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static string[] Artifacts(string inbox, string id) =>
        [Path.Combine(inbox, $"{id}.pdf"), Path.Combine(inbox, $"{id}.review.json"),
         Path.Combine(inbox, $"{id}.rotation.json"), Path.Combine(inbox, $"{id}.pretranscript.json")];

    [Fact]
    public async Task Aged_discard_is_sealed_delete_then_append()
    {
        var (proc, store, _, _) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-aged00001", withArtifacts: true);
            BackdateDiscard("sw-aged00001", DateTimeOffset.UtcNow.AddDays(-40));

            await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.All(Artifacts(_inbox, "sw-aged00001"), p => Assert.False(File.Exists(p))); // every artifact released…
            var doc = (await store.GetDocumentAsync("sw-aged00001"))!;
            Assert.Equal("Purged", doc.State);                                                // …then the seal
            var last = (await store.GetEventsAsync("sw-aged00001"))[^1];
            Assert.Equal("Purged", last.Type);
            Assert.Equal("discard-sweep", last.Actor);
            Assert.Contains("\"graceDays\":30", last.Details);
        }
    }

    [Fact]
    public async Task In_window_discard_with_artifacts_is_untouched()
    {
        var (proc, store, _, _) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-fresh0001", withArtifacts: true);

            await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Equal("Discarded", (await store.GetDocumentAsync("sw-fresh0001"))!.State);
            Assert.All(Artifacts(_inbox, "sw-fresh0001"), p => Assert.True(File.Exists(p)));
        }
    }

    [Fact]
    public async Task Grace_zero_is_a_zero_day_window_purged_on_the_next_tick()
    {
        // The decided semantics: explicit 0 does NOT disable the sweep — it makes everything eligible at once.
        var (proc, store, _, _) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-zero00001", withArtifacts: true);

            await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 0, DateTimeOffset.UtcNow.AddSeconds(5), CancellationToken.None);

            Assert.Equal("Purged", (await store.GetDocumentAsync("sw-zero00001"))!.State);
            Assert.All(Artifacts(_inbox, "sw-zero00001"), p => Assert.False(File.Exists(p)));
        }
    }

    [Fact]
    public async Task Artifact_less_discard_seals_immediately_and_a_post_kill_switch_one_WARNs()
    {
        var (proc, store, _, log) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-bare00001", withArtifacts: false); // discardedAt = now → after 2026-07-27

            // In-window by age (fresh discard, 30d window) — but with no artifacts the window is a fiction.
            await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Equal("Purged", (await store.GetDocumentAsync("sw-bare00001"))!.State);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unknown deleter"));
        }
    }

    [Fact]
    public async Task Artifact_less_pre_kill_switch_discard_seals_without_the_WARNING()
    {
        var (proc, store, _, log) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-old000001", withArtifacts: false);
            BackdateDiscard("sw-old000001", DateTimeOffset.Parse("2026-07-01T00:00:00+00:00")); // the unconditional-delete era

            await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Equal("Purged", (await store.GetDocumentAsync("sw-old000001"))!.State);
            Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning); // ordinary history, no alarm
        }
    }

    [Fact]
    public async Task Sweep_is_inert_while_the_kill_switch_is_closed()
    {
        var (proc, store, _, _) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-dark00001", withArtifacts: true);
            BackdateDiscard("sw-dark00001", DateTimeOffset.UtcNow.AddDays(-400)); // aged far past any window

            await proc.PurgeAgedDiscardedAsync(purgeEnabled: false, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.Equal("Discarded", (await store.GetDocumentAsync("sw-dark00001"))!.State);
            Assert.All(Artifacts(_inbox, "sw-dark00001"), p => Assert.True(File.Exists(p)));
        }
    }

    [Fact]
    public async Task A_held_claim_keeps_the_sweep_out_until_released()
    {
        var (proc, store, claims, _) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-claim0001", withArtifacts: true);
            BackdateDiscard("sw-claim0001", DateTimeOffset.UtcNow.AddDays(-40));

            using (var held = claims.Acquire("doc:sw-claim0001"))
            {
                Assert.True(held.Acquired);
                await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.Equal("Discarded", (await store.GetDocumentAsync("sw-claim0001"))!.State); // skipped, not raced
            }

            await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal("Purged", (await store.GetDocumentAsync("sw-claim0001"))!.State);
        }
    }

    [Fact]
    public async Task A_failed_artifact_delete_keeps_the_document_Discarded_until_it_succeeds()
    {
        var (proc, store, _, log) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-lock00001", withArtifacts: true);
            BackdateDiscard("sw-lock00001", DateTimeOffset.UtcNow.AddDays(-40));

            // Make the PDF path undeletable CROSS-PLATFORM by making it a DIRECTORY: File.Delete on a
            // directory throws on every OS. (The first version held an open FileShare.None handle, which
            // blocks deletion only on Windows — POSIX unlinks open files, so on Linux CI the delete
            // succeeded and the sweep CORRECTLY sealed, failing the test's premise, not the sweep.)
            var pdfPath = Path.Combine(_inbox, "sw-lock00001.pdf");
            File.Delete(pdfPath);
            Directory.CreateDirectory(pdfPath);
            try
            {
                await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);
                Assert.Equal("Discarded", (await store.GetDocumentAsync("sw-lock00001"))!.State);
                Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("retried next tick"));
            }
            finally { Directory.Delete(pdfPath); }

            await File.WriteAllBytesAsync(pdfPath, System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF")); // a deletable artifact again
            await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal("Purged", (await store.GetDocumentAsync("sw-lock00001"))!.State); // self-heals once the obstruction is gone
        }
    }

    [Fact]
    public async Task Sealed_leftover_artifacts_are_released_without_any_append()
    {
        // The one-time seal's shape: Purged in the ledger, files still on disk. The sweep releases the files
        // and appends NOTHING — the row is already sealed and the store guard would reject an append anyway.
        var (proc, store, _, _) = Build();
        using (store)
        {
            await SeedDiscardedAsync(store, "sw-left00001", withArtifacts: true);
            await store.AppendAsync(new AuditEntry("sw-left00001", "Purged", "discard-seal", Details: """{"seal":true}"""));
            var eventsBefore = (await store.GetEventsAsync("sw-left00001")).Count;

            await proc.PurgeAgedDiscardedAsync(purgeEnabled: true, graceDays: 30, DateTimeOffset.UtcNow, CancellationToken.None);

            Assert.All(Artifacts(_inbox, "sw-left00001"), p => Assert.False(File.Exists(p)));
            Assert.Equal("Purged", (await store.GetDocumentAsync("sw-left00001"))!.State);
            Assert.Equal(eventsBefore, (await store.GetEventsAsync("sw-left00001")).Count); // NO append
        }
    }

    /// <summary>Minimal capturing logger — FiledLocatorTests has a private one; this class needs its own to
    /// assert the WARNING contract (the artifact-less tripwire is a log line, so the log IS the behavior).</summary>
    internal sealed class CapturingLogger : ILogger<UploadProcessor>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
