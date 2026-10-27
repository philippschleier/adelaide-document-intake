namespace Adelaide.Core;

/// <summary>
/// Builds a compact, human-readable summary of the DECODED barcode content to lead the transcript wherever it is
/// shown — the review screen and the searchable Volltext. The barcode is the prescription exactly as the issuing
/// software encoded it (deterministic, no OCR), so it leads and the vision text becomes a subordinate record — the
/// Muster 12 checkbox grid is unreliable under OCR (missed ticks, mis-grouped measures).
///
/// It parses the barcode (BfbBarcode for Muster 12 Verordnungen) and renders the full decoded content. The RAW
/// payload is PHI and per <see cref="DecodedBarcode"/> must only be consumed via parsing — it is NEVER written; only
/// the parsed, labelled fields are. (The raw tab-delimited payload would also be useless for search — unlabelled
/// positional flags — so nothing is lost.)
/// </summary>
public static class BarcodeSummary
{
    /// <summary>The decoded-barcode block for a unit's barcodes, or null when none decode to known content.</summary>
    public static string? Block(IReadOnlyList<DecodedBarcode>? barcodes)
    {
        if (barcodes is null) return null;
        foreach (var bc in barcodes)
        {
            if (BfbBarcode.Parse(bc.RawText) is { } v) return VerordnungBlock(v);
            if (BmpBarcode.Parse(bc.RawText) is { Medikamente.Count: > 0 } mp) return MedikationsplanBlock(mp);
        }
        return null;
    }

    /// <summary>Prepend the decoded-barcode block to a transcript so the authoritative reading leads. Returns the
    /// original text unchanged when no barcode decodes (e.g. the review→file path rebuilds the transcript without
    /// barcodes — the block already travels in that markdown, so this never double-applies it).</summary>
    public static string LeadWith(string markdown, IReadOnlyList<DecodedBarcode>? barcodes)
    {
        var block = Block(barcodes);
        if (block is null) return markdown;
        return string.IsNullOrWhiteSpace(markdown) ? block : block + "\n\n" + markdown;
    }

    // The full Muster 12 Verordnung — everything the BFB barcode encodes, labelled. No raw payload.
    private static string VerordnungBlock(BfbVerordnung b)
    {
        var lines = new List<string> { "— Verordnungsdaten (Barcode, maßgeblich) —" };

        var id = new List<string>();
        var name = string.Join(", ", new[] { b.LastName, b.FirstName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (name.Length > 0) id.Add(name);
        if (b.BirthDate is { } dob) id.Add($"geb. {dob:dd.MM.yyyy}");
        if (!string.IsNullOrWhiteSpace(b.Kvnr)) id.Add($"KVNR {b.Kvnr}");
        if (id.Count > 0) lines.Add("Patient: " + string.Join(" · ", id));

        if (b.IssueDate is { } iss) lines.Add($"ausgestellt am {iss:dd.MM.yyyy}");
        if (!string.IsNullOrWhiteSpace(b.Diagnosis)) lines.Add("Diagnose: " + b.Diagnosis);

        var head = new List<string>();
        if (!string.IsNullOrWhiteSpace(b.PrescriptionOrder)) head.Add(b.PrescriptionOrder!);
        if (b.ValidFrom is { } vf && b.ValidUntil is { } vu) head.Add($"{vf:dd.MM.yyyy}–{vu:dd.MM.yyyy}");
        if (head.Count > 0) lines.Add(string.Join(" · ", head));

        if (b.Leistungen.Count > 0) lines.Add("Leistungen: " + b.LeistungenSummary);
        return string.Join("\n", lines);
    }

    // The full BMP medication plan — the patient header + one line per medication (PZN · dosing · indication). The
    // barcode has no trade names (only the PZN code), so the searchable value is the PZN and the indication/Grund.
    private static string MedikationsplanBlock(BmpMedikationsplan p)
    {
        var lines = new List<string> { "— Medikationsplan (Barcode, maßgeblich) —" };

        var id = new List<string>();
        var name = string.Join(", ", new[] { p.LastName, p.FirstName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (name.Length > 0) id.Add(name);
        if (p.BirthDate is { } dob) id.Add($"geb. {dob:dd.MM.yyyy}");
        if (!string.IsNullOrWhiteSpace(p.Kvnr)) id.Add($"KVNR {p.Kvnr}");
        if (id.Count > 0) lines.Add("Patient: " + string.Join(" · ", id));

        lines.Add($"{p.Medikamente.Count} Medikament(e):");
        foreach (var m in p.Medikamente)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(m.Name)) parts.Add(m.Name!);
            if (!string.IsNullOrWhiteSpace(m.Pzn)) parts.Add("PZN " + m.Pzn);
            if (!string.IsNullOrWhiteSpace(m.Dosing)) parts.Add("Dosierung " + m.Dosing);
            if (!string.IsNullOrWhiteSpace(m.Reason)) parts.Add(m.Reason!);
            lines.Add("• " + string.Join(" · ", parts));
        }
        return string.Join("\n", lines);
    }
}
