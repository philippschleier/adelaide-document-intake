using System.Globalization;

namespace Adelaide.Core;

/// <summary>Derives the deterministic OpenAI <c>seed</c> for an extraction from the document's content hash.
/// Reproducibility provenance, not PHI: re-uploading identical bytes reproduces identical model output (for a given
/// backend <c>system_fingerprint</c>), and different documents get different seeds. Lives in Core so the derivation is
/// unit-testable independently of the live model call.</summary>
public static class ExtractionSeed
{
    /// <summary>Stable 31-bit non-negative seed from the first 32 bits of the content hash (the dedup SHA, hex).
    /// Returns null when no usable hash is available (→ unseeded extraction, temperature 0 only). Tolerant of a
    /// malformed/non-hex hash (returns null rather than throwing) so it can never fail the extraction it annotates.</summary>
    public static int? FromContentSha(string? contentSha) =>
        !string.IsNullOrWhiteSpace(contentSha)
        && contentSha.Length >= 8
        && int.TryParse(contentSha.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)
            ? raw & 0x7FFFFFFF   // clear the sign bit → always non-negative (0xffffffff → int.MaxValue, not -1)
            : null;
}
