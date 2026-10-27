using Adelaide.Core;

/// <summary>The Transcribed event's Details payload, in ONE place so a test can assert the shape that actually ships.
///
/// This exists because the N3 rework had to guarantee the serialised event stayed byte-identical to its pre-rework
/// form with only the content hash appended. A test that rebuilds the payload independently proves nothing about the
/// event the host emits — it just asserts that two copies of the same idea agree. Both call sites use these builders,
/// so the assertion is about the real thing.
///
/// The transcript text itself is deliberately NOT here and must never be: the chain is append-only, so anything in it
/// can never be erased. Only <see cref="TranscriptDigest"/> of the text goes on the event; the text lives in the
/// separate, deletable Adelaide.Audit.TranscriptStore.</summary>
internal static class TranscribedEventDetails
{
    /// <summary>The vision-transcription path. Property order is load-bearing: the first seven are the pre-rework
    /// payload verbatim, and transcriptSha256 is appended last.</summary>
    public static object Vision(Transcript t) => new
    {
        pages = t.PageCount,
        legibility = t.QualityScore,
        barcodes = t.Barcodes.Count,
        model = t.ModelDeployment,
        modelVersion = t.ModelServed,
        systemFingerprint = t.ModelFingerprint,
        promptHash = t.PromptHash,
        transcriptSha256 = TranscriptDigest.Of(t.Markdown),
        // A behavioural parameter, recorded beside the model id for the same reason: a later shift in results is only
        // attributable if the setting that produced them was written down. Null on a non-reasoning model.
        reasoningEffort = t.ReasoningEffort,
    };

    /// <summary>The split-child path, which reuses the parent's slice instead of calling the model. Same rule: the
    /// pre-rework six properties, then the hash.</summary>
    public static object ReuseSplit(Transcript t, string parentDocId, string separatorVersion) => new
    {
        pages = t.PageCount,
        legibility = t.QualityScore,
        barcodes = t.Barcodes.Count,
        source = "parent",
        parent = parentDocId,
        separatorVersion,
        transcriptSha256 = TranscriptDigest.Of(t.Markdown),
    };
}
