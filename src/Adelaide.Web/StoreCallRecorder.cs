using Adelaide.Core;

namespace Adelaide.Web;

/// <summary>Bridges the wrappers' <see cref="IModelCallRecorder"/> seam to the PHI-side model_calls table.
/// FAIL-OPEN BUT COUNTED (the capture hard rule): a store failure is WARN-logged with the document id and
/// incremented on <see cref="CaptureHealth"/> — never thrown into the model call, never silent. The wrappers keep
/// their own belt-and-braces catch; between the two, capture can degrade but can never take a document down.</summary>
public sealed class StoreCallRecorder(Adelaide.Audit.ITranscriptStore store, CaptureHealth health, ILogger<StoreCallRecorder> log) : IModelCallRecorder
{
    public async Task RecordAsync(ModelCallRecord record, CancellationToken ct = default)
    {
        try
        {
            await store.PutModelCallAsync(record, ct);
        }
        catch (Exception ex)
        {
            health.CountFailure("model_calls");
            log.LogWarning("model_calls capture failed for {DocId} at {Site} ({ExType}); the call itself is unaffected.",
                record.Context.DocumentId, record.Context.Site.Wire(), ex.GetType().Name);
        }
    }
}
