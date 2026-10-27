namespace Adelaide.Core;

/// <summary>
/// Allowed values for the SharePoint Choice columns on "Patient Records", pulled live from the site.
/// The extractor is constrained to these (emit exactly one; anything else → review).
/// Regenerable from Graph (list columns → choice.choices).
/// NOTE: ServiceYear is a hardcoded list that currently ends at 2030 — it will need extending in SharePoint.
/// </summary>
public static class Vocab
{
    public static readonly IReadOnlyDictionary<string, string[]> Choices = new Dictionary<string, string[]>
    {
        ["LegalBasis"]        = new[] { "§ 36 SGB XI", "§ 37.1 SGB V", "§ 37.2 SGB V", "§ 38 SGB V", "§ 39 SGB XI", "§ 45b SGB XI" },
        ["PrescriptionOrder"] = new[] { "Erstverordnung", "Folgeverordnung" },
        ["ApprovalOutcome"]   = new[] { "Wie verordnet", "Abweichend genehmigt", "Abgelehnt" },
        ["SenderType"]        = new[] { "Krankenkasse", "Pflegekasse", "Sonstige" },
        ["BenefitType"]       = new[] { "Entlastungsbetrag", "Verhinderungspflege", "Pflegegrad", "Sonstiges" },
        ["AssessmentResult"]  = new[] { "Pflegegrad bestätigt", "Pflegegrad erhöht", "Pflegegrad gesenkt", "Abgelehnt" },
        ["CareLevel"]         = new[] { "1", "2", "3", "4", "5" },
        ["IssuingFacility"]   = new[] { "Krankenhaus", "Reha", "Praxis", "Sonstiges" },
        ["ServiceMonthNum"]   = new[] { "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12" },
        ["ServiceYear"]       = new[] { "2024", "2025", "2026", "2027", "2028", "2029", "2030" },
    };

    public static string[]? For(string column) => Choices.TryGetValue(column, out var v) ? v : null;

    /// <summary>True if the column is free-form (not a constrained choice).</summary>
    public static bool IsFree(string column) => !Choices.ContainsKey(column);
}
