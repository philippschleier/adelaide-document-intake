namespace Adelaide.Core;

/// <summary>Is there enough transcribed text to act on at all?
///
/// This is deliberately NOT the legibility score. Legibility is the model's own self-report and is uncalibrated:
/// document 117203e499e9 returned SIX pages at legibility 0.9 with a completely empty transcript. Nothing downstream
/// noticed — the score cleared the 0.60 floor, so the document only reached a human indirectly, via classification
/// failing on empty text. A document can also fail this way with a perfectly good score and still classify into
/// something plausible, which files an empty document.
///
/// THRESHOLD, measured rather than guessed (368 recoverable transcripts, 2026-08-05):
///   • exactly one document had zero characters after trimming;
///   • nothing at all fell between 1 and 127 characters;
///   • the sparsest LEGITIMATE document ran 61 characters per page (a 15-page form at legibility 1.0).
/// So the rule is density-based at 10 chars/page — it fires on the empty document and leaves a 6x margin to the
/// thinnest real one. A per-page rule rather than an absolute one because "300 characters" is normal for a one-page
/// note and alarming for a twelve-page report.
///
/// A decoded barcode is an AUTHORITATIVE alternative source (the Muster 12 BFB payload carries the prescription
/// outright), so a document that has one is never flagged here — the text is not the only evidence.</summary>
public static class TranscriptQuality
{
    /// <summary>Minimum transcribed characters per rendered page before a transcript counts as usable.</summary>
    public const int MinCharsPerPage = 10;

    /// <summary>True when the transcript carries too little text to be acted on AND no barcode supplies the content
    /// independently. Pure and side-effect free so every path can share the one definition.</summary>
    public static bool IsUnusable(string? markdown, int pageCount, IReadOnlyList<DecodedBarcode>? barcodes)
    {
        if (barcodes is { Count: > 0 }) return false;             // authoritative alternative source
        var chars = markdown?.AsSpan().Trim().Length ?? 0;
        var pages = pageCount > 0 ? pageCount : 1;                // a malformed page count must not divide the rule away
        return chars < MinCharsPerPage * pages;
    }

    /// <inheritdoc cref="IsUnusable(string?,int,IReadOnlyList{DecodedBarcode}?)"/>
    public static bool IsUnusable(Transcript? t) =>
        t is null || IsUnusable(t.Markdown, t.PageCount, t.Barcodes);
}
