using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Adelaide.Core;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;

namespace Adelaide.Web;

// C7′ — THE IN-APP STAGING-SHARE POLLER (migration plan; replaces the external Adelaide.Drain process for cloud).
//
// Design rules, in the order they earned their place (several were sharpened by adversarial verification):
//   • CAPTURE UNCONDITIONALLY, ADJUDICATE NEVER: any settled staging file the poller can download becomes a
//     document or a recorded rejection (DrainIngest, recordRejections=true). The poller holds NO per-file state.
//   • CONSUME AFTER DURABLE CAPTURE — and only then: the staging copy is deleted ONLY when Adelaide owns both the
//     outcome and (where one exists) the bytes (Result.SafeToDeleteSource). Malware is the exception BY DESIGN:
//     its bytes are never written to the inbox, so the source copy stays as the quarantine and ages on the gauge.
//   • SETTLE BEFORE READING: a file whose LastModified is within the settle window may still be uploading
//     (Azure Files lists in-progress uploads at full size with unwritten ranges reading as zeros) — skip it this
//     tick. Reading mid-upload used to risk ingesting truncated bytes AND deleting the in-flight original.
//   • GLOBAL FAILURES STAY GLOBAL AND LOUD: an unreachable share / bad SAS fails the tick (health gauge + WARN)
//     WITHOUT wiping the previous tick's pending picture; SDK timeouts are failures, never mistaken for shutdown.
//   • LOG HYGIENE: staging paths/filenames can carry patient identifiers (the repo-wide rule) — log lines carry
//     an opaque srid tag and the DocumentId, never the path itself.
//
// T-0 is a config write: set ADELAIDE_DRAIN_SHARE_URL (+ a SAS with read+list+DELETE) on the App Service and stop
// invoking the on-prem Adelaide.Drain; rollback is the mirror image. The share must be drained-to-empty (or the C9
// seed imported) first — this ledger does not know what the OTHER instance already ingested. Easy Auth is
// deliberately untouched: its audience rejection remains the guard against a stray on-prem drain posting here.

/// <summary>Configured-or-off: Options null when ADELAIDE_DRAIN_SHARE_URL is unset/blank; ConfigError non-null
/// when it is SET but unparseable — the T-0 switch must fail loudly, not silently off (verification finding).
/// The URL carries the SAS — never log it; log the path-only Describe() instead.</summary>
internal sealed record DrainPollerOptions(Uri ShareUrlWithSas, string Subdir, int PollMinutes)
{
    internal static (DrainPollerOptions? Options, string? ConfigError) FromEnv()
    {
        var url = Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_URL");
        if (string.IsNullOrWhiteSpace(url)) return (null, null);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (null, "ADELAIDE_DRAIN_SHARE_URL is set but not an absolute URI — the poller is OFF although the switch looks thrown.");
        var subdir = (Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_SHARE_SUBDIR") ?? "").Trim('/', '\\').Replace('\\', '/');
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_POLL_MINUTES"), out var m) && m is > 0 and <= 1440 ? m : 5;
        return (new DrainPollerOptions(uri, subdir, minutes), null);
    }
}

/// <summary>The poller's non-PHI health gauge for /api/stats: counts and timestamps only, never filenames or
/// paths. Honesty rules from verification: per-file failures make the tick NOT fully successful (a read-denied
/// SAS must not look healthy), and a global failure preserves the previous pending picture instead of zeroing it
/// (an age alert must not clear exactly when things get worse).</summary>
internal sealed class DrainPollerHealth
{
    private readonly object _lock = new();
    private bool _configured;
    private DateTimeOffset? _lastPollUtc, _lastSuccessUtc, _oldestPendingUtc;
    private int _pendingCount, _failedFiles;
    private long _ingestedTotal, _dedupedTotal, _rejectedTotal;
    private string? _lastError;

    internal void MarkConfigured() { lock (_lock) _configured = true; }

    /// <summary>A completed enumeration (some files may still have failed individually).</summary>
    internal void RecordTick(DateTimeOffset now, int pending, DateTimeOffset? oldestPending,
        int ingested, int deduped, int rejected, int failedFiles)
    {
        lock (_lock)
        {
            _lastPollUtc = now;
            _pendingCount = pending;
            _oldestPendingUtc = oldestPending;
            _ingestedTotal += ingested;
            _dedupedTotal += deduped;
            _rejectedTotal += rejected;
            _failedFiles = failedFiles;
            if (failedFiles == 0) { _lastSuccessUtc = now; _lastError = null; }
            else _lastError = $"{failedFiles} per-file failure(s) last tick";
        }
    }

    /// <summary>A tick that failed before/while enumerating: record the failure, PRESERVE the pending picture.</summary>
    internal void RecordGlobalFailure(DateTimeOffset now, string error)
    {
        lock (_lock) { _lastPollUtc = now; _lastError = error; }
    }

    internal object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                configured = _configured,
                lastPollUtc = _lastPollUtc,
                lastSuccessUtc = _lastSuccessUtc,
                pendingFiles = _pendingCount,
                failedFiles = _failedFiles,
                oldestPendingAgeMinutes = _oldestPendingUtc is { } o ? Math.Round((DateTimeOffset.UtcNow - o).TotalMinutes, 1) : (double?)null,
                ingestedSinceStart = _ingestedTotal,
                dedupedSinceStart = _dedupedTotal,
                rejectedSinceStart = _rejectedTotal,
                lastError = _lastError,
            };
        }
    }
}

/// <summary>The staging-share seam (testability; the Azure implementation is below).</summary>
internal interface IStagingSource
{
    /// <summary>Path-only description — never the SAS.</summary>
    string Describe();
    IAsyncEnumerable<IStagingFile> ListAsync(CancellationToken ct);
}

internal interface IStagingFile
{
    /// <summary>Share-relative path — the SourceRecordId idempotency key, same as the external drain's.</summary>
    string SourceRecordId { get; }
    string FileName { get; }
    /// <summary>Size from the directory LISTING (no extra round-trip) — oversize files are skipped without ever
    /// downloading them (verification finding: the old cap-in-ReadAsync re-downloaded 25 MB every tick, forever).</summary>
    long? ListedSize { get; }
    Task<byte[]?> ReadAsync(long maxBytes, CancellationToken ct);
    Task DeleteAsync(CancellationToken ct);
    Task<DateTimeOffset?> LastModifiedAsync(CancellationToken ct);
}

/// <summary>Azure Files via the Files SDK + SAS (REST over 443) — the same access shape as the external drain.</summary>
internal sealed class AzureFilesStagingSource(Uri shareUriWithSas, string subdir) : IStagingSource
{
    private readonly ShareClient _share = new(shareUriWithSas);

    public string Describe()
    {
        var path = _share.Uri.GetLeftPart(UriPartial.Path); // strips the query — the SAS never leaves this record
        return subdir.Length == 0 ? path : $"{path}/{subdir}";
    }

    public IAsyncEnumerable<IStagingFile> ListAsync(CancellationToken ct) =>
        WalkAsync(subdir.Length == 0 ? _share.GetRootDirectoryClient() : _share.GetDirectoryClient(subdir), subdir, ct);

    private async IAsyncEnumerable<IStagingFile> WalkAsync(ShareDirectoryClient dir, string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in dir.GetFilesAndDirectoriesAsync(cancellationToken: ct))
        {
            ct.ThrowIfCancellationRequested();
            var rel = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.IsDirectory)
            {
                await foreach (var sub in WalkAsync(dir.GetSubdirectoryClient(entry.Name), rel, ct))
                    yield return sub;
            }
            else if (entry.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                yield return new AzureFileItem(dir.GetFileClient(entry.Name), rel, entry.Name, entry.FileSize);
            }
        }
    }

    private sealed class AzureFileItem(ShareFileClient file, string srid, string name, long? listedSize) : IStagingFile
    {
        public string SourceRecordId => srid;
        public string FileName => name;
        public long? ListedSize => listedSize;

        public async Task<byte[]?> ReadAsync(long maxBytes, CancellationToken ct)
        {
            var resp = await file.DownloadAsync(cancellationToken: ct);
            await using var content = resp.Value.Content;
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + read > maxBytes) return null; // belt-and-braces behind the ListedSize skip
                ms.Write(buffer, 0, read);
            }
            return ms.ToArray();
        }

        public Task DeleteAsync(CancellationToken ct) => file.DeleteIfExistsAsync(cancellationToken: ct);

        public async Task<DateTimeOffset?> LastModifiedAsync(CancellationToken ct)
        {
            try { return (await file.GetPropertiesAsync(cancellationToken: ct)).Value.LastModified; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; } // gauge/settle input only; never let metadata fail the file
        }
    }
}

internal sealed class DrainPoller(DrainPollerOptions? options, string? configError, IStagingSource? source,
    DrainPollerHealth health, IAuditLog audit, IAuditStore store, Channel<UploadJob> queue, KeyGate keyGate,
    IMalwareScanner malwareScanner, long maxFileBytes, ILogger<DrainPoller> log) : BackgroundService
{
    /// <summary>Files modified more recently than this may still be uploading — skip them this tick. Azure Files
    /// lists an in-progress upload at its full allocated size while unwritten ranges read as zeros; reading it
    /// would ingest truncated bytes and (worse) consume the in-flight original.</summary>
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configError is not null)
        {
            // The T-0 switch looks thrown but is broken — this must be loud, not "not configured" (verification finding).
            log.LogError("Drain poller OFF with a broken switch: {Error}", configError);
            health.RecordGlobalFailure(DateTimeOffset.UtcNow, configError);
            return;
        }
        if (options is null || source is null)
        {
            log.LogInformation("Drain poller inactive: ADELAIDE_DRAIN_SHARE_URL not configured (intake is UI + HTTP drain only).");
            return;
        }
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ADELAIDE_DRAIN_PATTERN")))
            log.LogWarning("ADELAIDE_DRAIN_PATTERN is set but NOT supported by the in-app poller (it consumes *.pdf only); the external drain's pattern does not carry over.");
        health.MarkConfigured();
        log.LogInformation("Drain poller active: {Source} every {Minutes} min (source-record idempotency; consume-after-durable-capture; settle window {Settle} min).",
            source.Describe(), options.PollMinutes, SettleWindow.TotalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(source, stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(options.PollMinutes), stoppingToken); }
            catch (OperationCanceledException) { /* shutting down; the while condition exits */ }
        }
    }

    /// <summary>One poll pass. Internal for tests (the OutboxInvariantTests convention — no timer gymnastics).</summary>
    internal async Task TickAsync(IStagingSource src, CancellationToken ct)
    {
        var inbox = Environment.GetEnvironmentVariable("ADELAIDE_INBOX") ?? "inbox";
        int ingested = 0, deduped = 0, rejected = 0, pending = 0, failed = 0;
        DateTimeOffset? oldestPending = null;
        var now = DateTimeOffset.UtcNow;
        try
        {
            await foreach (var file in src.ListAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var tag = SridTag(file.SourceRecordId); // opaque — staging paths can carry patient identifiers
                try
                {
                    // Oversize: judged from the LISTING, never downloaded (25 MB × every tick was the old cost).
                    if (file.ListedSize is { } size && size > maxFileBytes)
                    {
                        log.LogWarning("Drain poller: staging file {SridTag} exceeds the {Max} MB cap ({Size} MB); left in place.",
                            tag, maxFileBytes / (1024 * 1024), size / (1024 * 1024));
                        pending++;
                        oldestPending = Older(oldestPending, await file.LastModifiedAsync(ct));
                        continue;
                    }

                    // Settle guard: an mtime inside the window may be an upload still in flight — next tick.
                    var lastModified = await file.LastModifiedAsync(ct);
                    if (lastModified is { } lm && now - lm < SettleWindow)
                    {
                        pending++;
                        oldestPending = Older(oldestPending, lastModified);
                        continue;
                    }

                    var bytes = await file.ReadAsync(maxFileBytes, ct);
                    if (bytes is null) // grew past the cap between listing and read — treat as the oversize case
                    {
                        log.LogWarning("Drain poller: staging file {SridTag} exceeded the cap during read; left in place.", tag);
                        pending++;
                        oldestPending = Older(oldestPending, lastModified);
                        continue;
                    }

                    var result = await DrainIngest.IngestAsync(bytes, file.FileName, file.SourceRecordId, "drain-poller",
                        recordRejections: true, inbox, audit, store, queue, keyGate, malwareScanner, maxFileBytes, log, ct);
                    switch (result.Kind)
                    {
                        case DrainIngest.ResultKind.Ingested: ingested++; break;
                        case DrainIngest.ResultKind.Deduplicated: deduped++; break;
                        default: rejected++; break;
                    }

                    if (result.SafeToDeleteSource)
                    {
                        // CONSUME AFTER DURABLE CAPTURE: Adelaide owns the outcome AND the bytes. A failed delete
                        // leaves the file; next tick the content dedup resolves it and this delete is retried.
                        try { await file.DeleteAsync(ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception delEx)
                        {
                            log.LogWarning("Drain poller: consumed {SridTag} (doc {DocId}) but could not delete the staging copy ({ExType}); retried next tick.",
                                tag, result.DocumentId, delEx.GetType().Name);
                            pending++;
                            oldestPending = Older(oldestPending, lastModified);
                        }
                    }
                    else
                    {
                        // Malware quarantine (or a rejected record whose bytes exist only at the source): the
                        // staging copy is deliberately kept and ages on the gauge until an operator acts.
                        log.LogWarning("Drain poller: {SridTag} rejected (doc {DocId}); source copy KEPT as quarantine.",
                            tag, result.DocumentId);
                        pending++;
                        oldestPending = Older(oldestPending, lastModified);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception fileEx) // incl. SDK timeouts (TaskCanceled WITHOUT our ct) — failures, not shutdown
                {
                    log.LogWarning("Drain poller: could not consume {SridTag} this tick ({ExType}); left in place.",
                        tag, fileEx.GetType().Name);
                    failed++;
                    pending++;
                    oldestPending = Older(oldestPending, await SafeLastModifiedAsync(file, ct));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; } // real shutdown: no bookkeeping
        catch (Exception ex) // incl. SDK timeouts — a global failure must be LOUD and must not zero the gauges
        {
            health.RecordGlobalFailure(DateTimeOffset.UtcNow, ex.GetType().Name);
            log.LogWarning("Drain poller tick failed globally ({ExType}); nothing consumed, retrying next tick. Check the share URL/SAS.", ex.GetType().Name);
            return;
        }
        health.RecordTick(DateTimeOffset.UtcNow, pending, oldestPending, ingested, deduped, rejected, failed);
        if (ingested + deduped + rejected > 0)
            log.LogInformation("Drain poller tick: ingested={Ingested} deduplicated={Deduped} rejected={Rejected} pending={Pending} failed={Failed}.",
                ingested, deduped, rejected, pending, failed);
    }

    private static async Task<DateTimeOffset?> SafeLastModifiedAsync(IStagingFile file, CancellationToken ct)
    {
        try { return await file.LastModifiedAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>Opaque, stable tag for a source record id — staging paths/filenames can carry patient identifiers
    /// and never appear in logs (the repo-wide rule DrainIngest already honors).</summary>
    internal static string SridTag(string srid) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(srid)))[..12].ToLowerInvariant();

    private static DateTimeOffset? Older(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a < b ? a : b);
}
