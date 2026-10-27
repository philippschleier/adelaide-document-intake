using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Adelaide.Core;

/// <summary>Content fingerprint for detecting DUPLICATE non-patient (business) documents (design §12 Phase 1). A business
/// document has no patient+type+date key (the medical duplicate signal in <see cref="Adelaide.Core"/>), so the only
/// reliable "same document" signal is its TEXT: this returns a stable hash of the normalized transcript. Two scans of the
/// same document with the same OCR text share a fingerprint → the doc is routed to review with the compare-and-confirm
/// overlay, and the reviewer decides (NEVER auto-rejected). The hash is a one-way digest — like the PDF SHA-256 already
/// recorded on every document — so it is PHI-safe to store in the immutable audit.</summary>
public static class BusinessDedup
{
    // Bound the input so a very long transcript doesn't dominate the hash; the leading text (sender, subject, reference,
    // amounts) identifies a business document well enough to catch a re-scan. Below the floor there is too little to
    // fingerprint reliably — return null so a near-empty transcript never collides two unrelated documents.
    private const int MaxChars = 8000;
    private const int MinChars = 24;

    /// <summary>A stable fingerprint of the document's text, or null when there is too little text to be reliable.
    /// Normalization (lowercased + whitespace-collapsed) makes trivial layout/OCR-spacing differences fingerprint the
    /// same; genuinely different OCR text does NOT (this catches exact re-scans, not fuzzy near-duplicates).</summary>
    public static string? Fingerprint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();
        if (normalized.Length < MinChars) return null;
        if (normalized.Length > MaxChars) normalized = normalized[..MaxChars];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
