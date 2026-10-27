using Adelaide.Audit;

namespace Adelaide.Web;

/// <summary>The capture-store erasure operation behind POST /api/capture/erase (migration plan C3e) — extracted so
/// the behavior is unit-testable without HTTP. Erases CAPTURE rows only; the ledger, inbox files and SharePoint
/// artifacts are separate surfaces with their own paths. Every act — including erasing a document that has no rows
/// — is recorded in the store's measure-side erasure log, which survives all erasure and purge paths by
/// construction (it is the record OF them).</summary>
internal static class CaptureErasure
{
    internal sealed record Result(bool Removed, bool Logged);

    /// <summary>scope=content ⇒ DeleteAsync (text and model-call bodies go; measurement rows AND the redrive bound's
    /// attempt marks survive); scope=full ⇒ ForgetAsync (measurement and the attempt marks too — `full` is
    /// document-terminal). The caller validates the scope string; this method trusts it.</summary>
    internal static async Task<Result> ExecuteAsync(ITranscriptStore store, string documentId, string scope,
        string actor, ILogger log, CancellationToken ct)
    {
        var removed = scope == "full"
            ? await store.ForgetAsync(documentId, ct)
            : await store.DeleteAsync(documentId, ct);
        var logged = true;
        try { await store.LogErasureAsync(documentId, scope, actor, DateTimeOffset.UtcNow, ct); }
        catch (Exception ex)
        {
            logged = false;
            log.LogError("Capture erasure LOG failed for {DocId} ({ExType}) — the erasure itself executed (removed={Removed}); record the act manually.",
                documentId, ex.GetType().Name, removed);
        }
        // WARN on purpose: erasures are rare, operator-initiated, and belong in the log tail.
        log.LogWarning("Capture erasure: scope={Scope} doc={DocId} actor={Actor} removed={Removed} logged={Logged}",
            scope, documentId, actor, removed, logged);
        return new(removed, logged);
    }
}
