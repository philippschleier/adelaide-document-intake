namespace Adelaide.Core;

/// <summary>The FIRST classification decision: which lane a document belongs to, medical or business.
///
/// Why this is separate from <see cref="DocumentCatalog"/> and not just another content type: the catalog is the
/// MEDICAL vocabulary. It drives field schemas, extraction, the auto-file allowlist and the patient match. A business
/// document — a supplier invoice, a payroll letter — has none of those properties, and adding it to the catalog would
/// hand it the whole medical extraction path. Worse, the catalog enum is what constrains the classifier's structured
/// output, so while the business lane lives outside the catalog the model is structurally INCAPABLE of saying
/// "this isn't medical". On the 2026-08-05 ledger that made all 358 business documents forced misses (23% of the
/// corpus) and pulled measured classification accuracy from 87.8% to 34.2% — an artefact of the vocabulary, not of
/// the classifier.
///
/// So: two decisions in sequence, each with its own enum, schema and metric. Domain runs first; type classification
/// runs only on the medical lane. The reviewer's checkbox stops being the sole source of the answer and becomes a
/// confirmation or correction of a model proposal — which is what makes the decision measurable at all.</summary>
public static class DocumentDomain
{
    /// <summary>Patient-related care documentation — the lane <see cref="DocumentCatalog"/> describes.</summary>
    public const string Medical = "Medical";

    /// <summary>Business/administrative documents with no patient dimension. Routed to a separate SharePoint site and
    /// labelled with a Zielbereich (HR / Finance / unklar) by the reviewer.</summary>
    public const string Business = "Business";

    /// <summary>The closed vocabulary, used as the response schema's enum.</summary>
    public static readonly string[] All = [Medical, Business];

    public static bool IsBusiness(string? domain) => string.Equals(domain, Business, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for any value we recognise; an unrecognised domain must never be treated as medical by default.</summary>
    public static bool IsKnown(string? domain) =>
        domain is not null && Array.Exists(All, d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase));
}
