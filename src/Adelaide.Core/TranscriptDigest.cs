namespace Adelaide.Core;

/// <summary>The transcript's content hash — the tamper-evidence that lets the PAYLOAD live off the ledger.
///
/// The audit chain must be able to prove which transcript a model produced. Putting the text itself in the chain does
/// that, but the chain is append-only: the text could then never be removed without breaking it, which collides
/// head-on with a DSGVO Art. 17 erasure request. A hash gives the same evidence and none of that problem — it is
/// non-PHI, not invertible, and stays valid after the text it describes has been deleted.
///
/// So the ledger carries the hash forever and the text lives in a separate, deletable store keyed by document id
/// (see Adelaide.Audit.TranscriptStore). Verifying a retained transcript is re-hashing it and comparing.</summary>
public static class TranscriptDigest
{
    /// <summary>SHA-256 of the transcript, lowercase hex. A null transcript hashes as the empty string, so the
    /// property is always present and always comparable — an absent hash would be ambiguous between "no transcript"
    /// and "an older event that predates this field".</summary>
    public static string Of(string? markdown)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(markdown ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
