namespace Adelaide.Core;

/// <summary>Structured content decoded from a Muster 12 (häusliche Krankenpflege) Blankoformularbedruckung
/// PDF417 barcode, per the KBV "Technisches Handbuch Blankoformularbedruckung" (v4.72) field layout for
/// Muster 12a/E, fields 01–102. The barcode is the prescription exactly as the issuing practice software
/// encoded it — deterministic, no OCR — so these values are authoritative over any vision reading of the
/// same form (e.g. Erst-/Folgeverordnung, which the vision transcript repeatedly mis-read).</summary>
public sealed record BfbVerordnung(
    string? LastName, string? FirstName, DateOnly? BirthDate, string? Kvnr,
    DateOnly? IssueDate, string? Diagnosis,
    string? PrescriptionOrder,            // "Erstverordnung" | "Folgeverordnung"
    DateOnly? ValidFrom, DateOnly? ValidUntil,
    string? KompressionSide,             // "rechts" | "links" | "beidseits"
    IReadOnlyList<string> Leistungen)    // human-readable list of the ticked measures
{
    /// <summary>The ticked measures as one display string (e.g. "Wundversorgung akut; Kompressionsstrümpfe anziehen (beidseits)").</summary>
    public string LeistungenSummary => string.Join("; ", Leistungen);
}

/// <summary>Decodes the tab-delimited Muster 12 BFB PDF417 payload into <see cref="BfbVerordnung"/>.</summary>
public static class BfbBarcode
{
    /// <summary>Parse a Muster 12 BFB PDF417 payload (tab-delimited, field NR = 1-based position). Returns null
    /// when the payload is not a Muster 12 barcode (e.g. a Medikationsplan, a QR code, or a bare KVNR).</summary>
    public static BfbVerordnung? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || !payload.Contains('\t')) return null;
        var f = payload.Split('\t');
        // Field 01 = Formularcode "12"; field 03 = Versionsnummer (11/12). Guard on the form code so we never
        // mis-read another form's barcode as a Verordnung. Up to field 21 (Zeitraum bis) must be addressable.
        if (f.Length < 21 || Get(f, 1) != "12") return null;

        var order = Get(f, 17).ToLowerInvariant() switch { "e" => "Erstverordnung", "f" => "Folgeverordnung", _ => (string?)null };
        var side = Get(f, 49).ToLowerInvariant() switch { "r" => "rechts", "l" => "links", "b" => "beidseits", _ => (string?)null };
        var sideSuffix = side is null ? "" : $" ({side})";

        var leistungen = new List<string>();
        void Tick(int nr, string label) { if (Get(f, nr) == "1") leistungen.Add(label); }
        Tick(22, "Herrichten der Medikamentenbox");
        Tick(28, "Medikamentengabe");
        if (Get(f, 34) == "1")
        {
            var route = Get(f, 36).ToLowerInvariant() switch { "i" => " (intramuskulär)", "s" => " (subkutan)", "a" => " (intramuskulär + subkutan)", _ => "" };
            leistungen.Add("Injektionen" + route);
        }
        Tick(42, "Blutzuckermessung (Erst-/Neueinstellung)");
        Tick(43, "Blutzuckermessung (bei intensivierter Insulintherapie)");
        if (Get(f, 50) == "1") leistungen.Add("Kompressionsstrümpfe anziehen" + sideSuffix);
        if (Get(f, 51) == "1") leistungen.Add("Kompressionsstrümpfe ausziehen" + sideSuffix);
        if (Get(f, 58) == "1") leistungen.Add("Kompressionsverbände anlegen" + sideSuffix);
        if (Get(f, 59) == "1") leistungen.Add("Kompressionsverbände abnehmen" + sideSuffix);
        Tick(66, "Stützende und stabilisierende Verbände");
        Tick(73, "Wundversorgung akut");
        Tick(80, "Wundversorgung chronisch");
        switch (Get(f, 88).ToLowerInvariant())
        {
            case "u": leistungen.Add("Unterstützungspflege"); break;
            case "k": leistungen.Add("Krankenhausvermeidungspflege"); break;
        }
        Tick(89, "Grundpflege");
        Tick(96, "Hauswirtschaftliche Versorgung");

        return new BfbVerordnung(
            NullIfEmpty(Get(f, 4)), NullIfEmpty(Get(f, 5)), Date8(Get(f, 6)), NullIfEmpty(Get(f, 9)),
            Date8(Get(f, 15)), NullIfEmpty(Get(f, 16)),
            order, Date6(Get(f, 20)), Date6(Get(f, 21)), side, leistungen);
    }

    private static string Get(string[] f, int nr1) => nr1 - 1 < f.Length ? f[nr1 - 1].Trim() : "";
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Field 06/15 are JJJJMMTT; partial dates (JJJJMM00, 00000000) intentionally fail → null.
    private static DateOnly? Date8(string s) => DateOnly.TryParseExact(s, "yyyyMMdd", out var d) ? d : null;

    // Fields 20/21 (Zeitraum von/bis) are JJMMTT — two-digit year, all within the 2000s for current prescriptions.
    private static DateOnly? Date6(string s) => s.Length == 6 && DateOnly.TryParseExact("20" + s, "yyyyMMdd", out var d) ? d : null;
}
