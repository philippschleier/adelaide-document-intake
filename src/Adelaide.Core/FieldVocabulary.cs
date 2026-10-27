namespace Adelaide.Core;

/// <summary>
/// The CLOSED set of field-name tokens that may be recorded anywhere durable — the append-only ledger's
/// <c>correctedFields</c>/<c>fieldSignals[].field</c>/<c>fieldProvenance[].field</c>, and the measurement corpus's
/// <c>corpus_fields.FieldName</c>.
///
/// WHY THIS LIVES IN Adelaide.Core RATHER THAN AT THE REVIEW ENDPOINT: the extraction response schema cannot enum
/// column names, so the model can author one. For a CATALOG type the extractor drops anything outside that type's
/// schema at the source, but the bare <see cref="DocumentCatalog.Fallback"/> type has no schema to check against —
/// and a fallback-typed document ALWAYS routes to review, so it is not a rare path. Such a name is not guaranteed
/// to be non-identifying (a German form yields labels like "Versichertennummer &lt;patient name&gt;"), yet every
/// downstream reader treats a field NAME as inherently safe.
///
/// It has to be enforced at the EXTRACTOR, not only at the review diff: the intake capture write persists
/// <c>data.Fields</c> straight into <c>corpus_fields</c> BEFORE any review, into a table the retention purge
/// deliberately exempts. Filtering only at review would leave that copy untouched — and would additionally strand
/// those rows unstampable, because the review outcome path would no longer name them.
///
/// Derived, never enumerated, from the sources the code already treats as authoritative, so it costs no upkeep:
/// every catalog column across every type, the identity block, and the server-added provenance fields.
/// </summary>
public static class FieldVocabulary
{
    /// <summary>Server-added / cross-type names that are not any single type's catalog column: the Domain and
    /// ContentType discriminators, OriginalContentType from a classify self-check overturn, DocumentDate (the
    /// form's fifth fixed identity input, which Leistungsnachweis alone has no column for), and Leistungen from
    /// the BFB barcode.</summary>
    private static readonly string[] ServerAdded =
        { "Domain", "ContentType", "OriginalContentType", "DocumentDate", "Leistungen" };

    /// <summary>Every ledger-safe column name: the union of all catalog columns across all types, the identity
    /// fields, and <see cref="ServerAdded"/>. A UNION across types (not the submitted type's columns) because a
    /// type CORRECTION leaves the original type's extracted keys in play.</summary>
    public static readonly IReadOnlySet<string> Columns = Build();

    private static HashSet<string> Build()
    {
        var v = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in DocumentCatalog.Types)
            foreach (var f in t.Fields)
                v.Add(f.Column);
        v.UnionWith(IdentityAgreement.Fields);
        v.UnionWith(ServerAdded);
        return v;
    }

    /// <summary>Identifier shape floor: ASCII letter start, letters/digits/underscore, ≤64 chars. Free prose,
    /// names with spaces or umlauts, and oversized strings fail it. Kept alongside the vocabulary because the two
    /// refuse different things — an alphanumeric pseudo-name is a valid identifier but not a field.</summary>
    public static bool ValidShape(string name) =>
        name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>The one test: identifier shape AND the closed vocabulary.</summary>
    public static bool IsSafe(string name) => ValidShape(name) && Columns.Contains(name);
}
