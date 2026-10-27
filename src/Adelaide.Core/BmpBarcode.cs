using System.Text;
using System.Xml.Linq;

namespace Adelaide.Core;

/// <summary>One medication line of a BMP. The barcode carries the PZN (drug code), the dosing schema, the dosing unit
/// and the indication/Grund — NOT the trade name (that needs a PZN lookup we don't have). <see cref="Name"/> is only
/// set on the rare plans that also embed a free-text drug name.</summary>
public sealed record BmpMedikament(string? Pzn, string? Name, string? Dosing, string? Reason);

/// <summary>A decoded Bundeseinheitlicher Medikationsplan (BMP): the patient header + the medication lines.</summary>
public sealed record BmpMedikationsplan(
    string? LastName, string? FirstName, DateOnly? BirthDate, string? Kvnr,
    IReadOnlyList<BmpMedikament> Medikamente);

/// <summary>Decodes a Bundeseinheitlicher Medikationsplan (BMP) DataMatrix payload — UTF-8 XML, root &lt;MP&gt;, per the
/// KBV/BMP specification (patient &lt;P&gt;, practice &lt;A&gt;, sections &lt;S&gt; of medication lines &lt;M&gt;). Like
/// <see cref="BfbBarcode"/> this is the sanctioned way to consume the raw payload (which is PHI) — only parsed fields
/// leave it. Returns null for anything that is not a BMP.</summary>
public static class BmpBarcode
{
    public static BmpMedikationsplan? Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var xml = FixEncoding(payload).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!xml.StartsWith("<MP", StringComparison.Ordinal)) return null;

        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return null; }
        var mp = doc.Root;
        if (mp is null || mp.Name.LocalName != "MP") return null;

        var p = mp.Elements().FirstOrDefault(e => e.Name.LocalName == "P");
        DateOnly? dob = DateOnly.TryParseExact(A(p, "b"), "yyyyMMdd", out var d) ? d : null;

        var meds = mp.Descendants().Where(e => e.Name.LocalName == "M").Select(m => new BmpMedikament(
            Pzn: NullIf(A(m, "p")),
            Name: NullIf(A(m, "a") ?? A(m, "w") ?? A(m, "h")),   // free-text drug name when the plan carries one
            Dosing: NullIf(Dosing(m)),
            Reason: NullIf(A(m, "r")))).ToList();

        return new BmpMedikationsplan(NullIf(A(p, "f")), NullIf(A(p, "g")), dob, NullIf(A(p, "egk")), meds);
    }

    // morgens-mittags-abends-nachts (omitted parts -> 0), or the free-text dosing 't'; appends the unit 'du'.
    private static string Dosing(XElement m)
    {
        var sched = new[] { A(m, "m"), A(m, "d"), A(m, "a"), A(m, "n") };
        var body = sched.Any(s => s.Length > 0) ? string.Join("-", sched.Select(s => s.Length > 0 ? s : "0")) : A(m, "t");
        var du = A(m, "du");
        return string.IsNullOrWhiteSpace(du) ? body : (body.Length > 0 ? $"{body} {du}" : du);
    }

    private static string A(XElement? e, string name) => e?.Attribute(name)?.Value?.Trim() ?? "";
    private static string? NullIf(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // The DataMatrix is UTF-8 but ZXing returns its bytes decoded as Latin-1, mojibaking German chars (a-umlaut shows
    // as two chars). Round-trip Latin-1 bytes -> UTF-8 to repair. If the result isn't valid UTF-8 (i.e. it was already
    // correct), the U+FFFD replacement char appears and we keep the original — a no-op on already-correct text.
    private static string FixEncoding(string s)
    {
        try
        {
            var utf8 = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(s));
            return utf8.Contains('�') ? s : utf8;
        }
        catch { return s; }
    }
}
