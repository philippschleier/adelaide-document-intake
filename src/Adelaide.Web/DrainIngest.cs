using System.Threading.Channels;
using Adelaide.Core;

namespace Adelaide.Web;

/// <summary>The drain ingestion core (migration plan C7′) — the single path scanner-share content enters the
/// pipeline by, extracted from POST /api/drain/upload so the in-app staging-share poller and the HTTP endpoint
/// share EVERY semantic: content-first idempotency (verify-hash-before-skip — a source-record match with different
/// bytes is a REUSED path and must ingest, never skip), per-srid serialization against re-delivery races,
/// scan-before-persist with poison recording, and capture-to-inbox before any enqueue.</summary>
internal static class DrainIngest
{
    internal enum ResultKind
    {
        /// <summary>New document: bytes persisted to the inbox, Received appended, job enqueued.</summary>
        Ingested,
        /// <summary>Same source path with the SAME bytes already resolved — the at-least-once re-delivery case.
        /// The existing document's outcome stands, whatever it was.</summary>
        Deduplicated,
        /// <summary>Rejected AND durably recorded (Received + DeadLetter poison events). Whether the source copy
        /// may be deleted is <see cref="Result.SafeToDeleteSource"/>: true when the bytes were persisted to the
        /// inbox (invalid input — the record is genuinely retryable), false for malware (FINDING 1d: flagged
        /// bytes are never written to the inbox; the source copy is the quarantine).</summary>
        RejectedRecorded,
        /// <summary>Rejected without any record (endpoint mode: the HTTP caller gets the error and keeps the file).</summary>
        RejectedUnrecorded,
    }

    /// <summary><paramref name="SafeToDeleteSource"/> is the poller's consume decision: true only when Adelaide
    /// durably owns BOTH the outcome and (where one exists) the bytes — deleting the source of a record whose
    /// bytes exist nowhere else would make the record unresolvable (verification finding).</summary>
    internal sealed record Result(ResultKind Kind, string? DocumentId, string? Reason, bool SafeToDeleteSource);

    /// <summary><paramref name="recordRejections"/> selects the caller's memory model: the HTTP endpoint returns
    /// errors to a client that keeps the file and its own ledger (false); the poller has no caller, so a rejection
    /// must become a document record with real affordances (true) — invalid input is persisted to the inbox FIRST,
    /// so its DeadLetter record is genuinely retryable ("terminal states belong where the affordances live").</summary>
    internal static async Task<Result> IngestAsync(byte[] bytes, string originalName, string srid, string actor,
        bool recordRejections, string inbox, IAuditLog audit, IAuditStore store, Channel<UploadJob> queue,
        KeyGate keyGate, IMalwareScanner malwareScanner, long maxFileBytes, ILogger log, CancellationToken ct)
    {
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        // Serialise per SourceRecordId so a concurrent re-delivery can't both miss the idempotency check (TOCTOU).
        using (await keyGate.LockAsync("srid:" + srid, ct))
        {
            var existing = await store.FindBySourceRecordIdAsync(srid, "", ct);
            if (existing is not null && string.Equals(existing.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            {
                var existingPdf = File.Exists(Path.Combine(inbox, $"{existing.DocumentId}.pdf"));
                // Same path + same bytes. Three sub-cases (verification findings — the naive "dedup means done"
                // both destroyed quarantined sources and immortalized half-written poison records):
                //   • DeadLetter doc: the standing verdict IS the outcome — report it as the rejection it was
                //     (keeps the HTTP drain client's permanent/transient classification identical across the
                //     dual-running window). Deletable only if the inbox holds the bytes (invalid-input poison);
                //     a no-pdf DeadLetter is a malware record whose only bytes are the source copy.
                //   • Received doc with NO inbox pdf: a HALF-WRITTEN poison record (the Received landed, the
                //     verdict append failed). Heal it below by re-deriving the verdict on the SAME document —
                //     never report it as an owned outcome.
                //   • Anything else: a genuine re-delivery of a document that owns its bytes/outcome — dedup.
                if (existing.State == "DeadLetter")
                    return new(ResultKind.RejectedRecorded, existing.DocumentId, "previously rejected", SafeToDeleteSource: existingPdf);
                if (existing.State != "Received" || existingPdf)
                    return new(ResultKind.Deduplicated, existing.DocumentId, null, SafeToDeleteSource: true);
                log.LogWarning("Drain ingest: healing half-written record {DocId} (Received without bytes or verdict) — re-deriving the verdict on the same document.", existing.DocumentId);
                return await JudgeAsync(existing.DocumentId, appendReceived: false);
            }

            var docId = Guid.NewGuid().ToString("N")[..12];
            if (existing is not null) // path reused for DIFFERENT content — the scenario that used to silently drop. LOUD alert, then ingest as new.
                log.LogWarning("Drain source-record REUSED with different content (existing doc {Existing} sha {ExistingSha}, incoming sha {NewSha}) — ingesting as NEW doc {DocId}, NOT skipping. This path-collision previously dropped the document silently.",
                    existing.DocumentId, existing.Sha256 is { Length: >= 8 } es ? es[..8] : "(none)", sha[..8], docId);
            return await JudgeAsync(docId, appendReceived: true);

            // The single verdict path — also the healer for half-written records (same bytes ⇒ same verdict, so
            // re-running it on the existing id completes exactly what the interrupted run would have written).
            async Task<Result> JudgeAsync(string id, bool appendReceived)
            {
                var path = Path.Combine(inbox, $"{id}.pdf");
                // C6: the Received payload is structural only — channel + content hash. The original filename and
                // the source-record path both embed patient identifiers (scanner names carry them; the srid IS a
                // share path ending in the filename), so they route to the updatable projection via the side-channel,
                // never into the append-only Details. Idempotency is unaffected: FindBySourceRecordIdAsync reads the
                // projection column, which the side-channel keeps populated.
                AuditEntry Received() => new(id, "Received", actor,
                    Details: J(new { channel = "Drain", sha256 = sha }),
                    Projection: new ProjectionOverrides(OriginalName: originalName, SourceRecordId: srid));
                // Rejection classes are reason codes (C6): the event carries the class; the human-readable text goes
                // to the projection's Reasons (erasable) and the log (live ops), never the chain.
                (string Code, string Text)? invalid = bytes.Length == 0 ? (ReasonCodes.EmptyFile, "empty file")
                    : bytes.LongLength > maxFileBytes ? (ReasonCodes.FileTooLarge, "file too large")
                    : !PdfSignature.LooksLikePdf(bytes) ? (ReasonCodes.InvalidPdf, "not a valid PDF")
                    : ((string, string)?)null;
                if (invalid is { } inv)
                {
                    // Mode-gated only for NEW records: when healing an existing half-record (appendReceived=false)
                    // the record already exists and must receive its verdict regardless of the caller's mode.
                    if (!recordRejections && appendReceived) return new(ResultKind.RejectedUnrecorded, null, inv.Text, SafeToDeleteSource: false);
                    // Persist BEFORE the events: the poison record must genuinely own its bytes, or /api/retry and
                    // the viewer 404 and the record is forever unresolvable (verification finding). The bytes are
                    // scanned below only on the valid path; garbage is never rendered unless a human retries it —
                    // and a retry re-enters the full pipeline, where the C0 attempt bound guards the worst case.
                    await File.WriteAllBytesAsync(path, bytes, ct);
                    if (appendReceived)
                        await audit.AppendAsync(Received(), ct);
                    await audit.AppendAsync(new AuditEntry(id, "DeadLetter", actor,
                        Details: J(new { errorClass = inv.Code, reasonCodes = new[] { inv.Code } }),
                        Projection: new ProjectionOverrides(Reasons: inv.Text)), ct);
                    log.LogWarning("Drain ingest rejected {DocId} ({Reason}); recorded as poison with its bytes captured, nothing enqueued.", id, inv.Text);
                    return new(ResultKind.RejectedRecorded, id, inv.Text, SafeToDeleteSource: true);
                }

                // FINDING 1d: scan before persisting/rendering/filing; flagged bytes are NEVER written to the
                // inbox — the record exists, the SOURCE copy is the quarantine (the poller must not delete it).
                var scan = await malwareScanner.ScanAsync(bytes, ct);
                if (!scan.Allowed)
                {
                    if (appendReceived)
                        await audit.AppendAsync(Received(), ct);
                    await audit.AppendAsync(new AuditEntry(id, "DeadLetter", "malware-scan",
                        Details: J(new { errorClass = ReasonCodes.MalwareBlocked, reasonCodes = new[] { ReasonCodes.MalwareBlocked } }),
                        Projection: new ProjectionOverrides(Reasons: scan.Reason)), ct);
                    log.LogWarning("Drain upload blocked by malware scan for {DocId} (verdict={Verdict}); not persisted, not enqueued; the source copy is the quarantine.", id, scan.Verdict);
                    return new(ResultKind.RejectedRecorded, id, scan.Reason, SafeToDeleteSource: false);
                }

                await File.WriteAllBytesAsync(path, bytes, ct);
                if (appendReceived)
                    await audit.AppendAsync(Received(), ct);
                await queue.Writer.WriteAsync(new UploadJob(id, path, originalName, false, srid, SourceChannel.Drain), ct);
                return new(ResultKind.Ingested, id, null, SafeToDeleteSource: true);
            }
        }
    }

    private static string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);
}
