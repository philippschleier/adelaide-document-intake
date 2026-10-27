using System.Collections.Concurrent;

namespace Adelaide.Web;

/// <summary>In-process capture-health counters (docs/CAPTURE.md). Every capture write is fail-open — it must never
/// fail the document — but "fail-open" is only safe when the failure is COUNTED somewhere a human looks: the silent
/// <c>SetLabelAsync</c> swallow that let labels no-op invisibly is the anti-pattern these counters exist to kill.
/// Process-local by design (reset on restart): the durable truth lives in the stores and the ledger, which
/// /api/stats reads alongside these; the counters answer "has capture failed SINCE THIS PROCESS STARTED", which is
/// the actionable operator question.</summary>
public sealed class CaptureHealth
{
    private readonly ConcurrentDictionary<string, long> _failures = new(StringComparer.Ordinal);
    private long _labelsMissed;

    /// <summary>A capture write failed (exception) in the named area — model_calls, corpus, corpus_fields,
    /// adjudications, label, purge. WARN-logged by the caller; counted here.</summary>
    public void CountFailure(string area) => _failures.AddOrUpdate(area, 1, (_, v) => v + 1);

    /// <summary>A review-lane label had no corpus row to land on (SetLabelAsync returned false). Distinct from a
    /// failure: the write worked, the row was missing — the exact condition that went unnoticed for 5 filings.</summary>
    public void CountLabelMissed() => Interlocked.Increment(ref _labelsMissed);

    public long LabelsMissedSinceStart => Interlocked.Read(ref _labelsMissed);

    public long FailuresTotal => _failures.Values.Sum();

    /// <summary>Snapshot for /api/stats — area → count, plus the label misses.</summary>
    public IReadOnlyDictionary<string, long> FailuresByArea() => new Dictionary<string, long>(_failures);
}

/// <summary>The capture-health snapshot for /api/stats and the startup line — durable truth from the stores and the
/// ledger, plus the in-process counters. Epoch = the capture store file's creation instant (the CorpusGuardTests
/// convention): only documents processed after it can possibly have rows, so it is the honest denominator for
/// "is capture keeping up". Everything here is non-PHI: counts and a mode string.</summary>
internal static class CaptureStats
{
    internal static async Task<object> CollectAsync(Adelaide.Audit.ITranscriptStore? store, Adelaide.Core.IAuditStore ledger,
        CaptureHealth? health, CancellationToken ct)
    {
        var failures = health?.FailuresByArea() ?? new Dictionary<string, long>();
        if (store is null)
            return new { mode = TranscriptRetentionGate.Describe(), store = "(absent)", failuresSinceStart = failures };

        // Epoch = the store's own account of when capture began (SQLite: file creation; Azure SQL: first corpus row) —
        // the honest denominator for "is capture keeping up". A store with no history yet denominates from now.
        var epoch = await store.EpochAsync(ct) ?? DateTimeOffset.UtcNow;
        var landed = await store.LabeledRowCountAsync(ct);
        return new
        {
            mode = TranscriptRetentionGate.Describe(),
            epoch,
            modelCalls = await store.ModelCallCountAsync(ct),
            transcripts = await store.CountAsync(ct),
            corpus = new
            {
                rows = await store.CorpusRowCountAsync(ct),
                transcribedSinceEpoch = await ledger.CountDocumentsWithEventSinceAsync("Transcribed", epoch, ct),
            },
            // Review-lane labels ONLY: auto-filed documents are unlabeled BY DESIGN (Outcome=AUTOFILED) and are
            // counted in corpusFields below, never here — so this gap always means something is actually wrong.
            labels = new
            {
                expectedReviewLaneSinceEpoch = await ledger.CountReviewedThenFiledSinceAsync(epoch, ct),
                landed,
                missedSinceProcessStart = health?.LabelsMissedSinceStart ?? 0,
            },
            corpusFields = await store.CorpusFieldOutcomeCountsAsync(ct), // pending | MATCH | CORRECTION | FILL | AUTOFILED
            adjudications = await store.AdjudicationCountAsync(ct),
            failuresSinceStart = failures,
        };
    }
}
