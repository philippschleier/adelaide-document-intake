namespace Adelaide.Core;

public enum FieldKind { Text, Date, Choice, Bool, Lookup, Person }

/// <summary>A field to extract for a document type, mapped to its SharePoint internal column name.
/// Mandatory either unconditionally (<paramref name="Required"/>) or conditionally (<paramref name="RequiredUnless"/> —
/// required unless another field currently holds one of the exempt values, e.g. a Genehmigungsnummer is required unless
/// the outcome is an Ablehnung). A blank-but-required field routes to review; the review form blocks submit until set.</summary>
public record DocField(string Column, string Label, FieldKind Kind, bool Required = false, RequiredUnless? RequiredUnless = null)
{
    /// <summary>Is this field mandatory given the current field values? True if unconditionally Required, or if
    /// RequiredUnless is set and the referenced field does NOT currently hold one of the exempt values (case-insensitive).</summary>
    public bool IsRequiredGiven(Func<string, string?> value) =>
        Required || (RequiredUnless is { } ru && !ru.Values.Any(v => string.Equals(v, value(ru.Field), StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Conditional requirement: the field is required UNLESS <see cref="Field"/> currently equals one of
/// <see cref="Values"/> (e.g. ApprovalNumber is required unless ApprovalOutcome is "Abgelehnt").</summary>
public record RequiredUnless(string Field, string[] Values);

/// <summary>A SharePoint Content Type = a document kind, with the fields to extract for it.
/// <paramref name="Description"/> defines the type (used by the classifier) — the single source of truth.</summary>
public record DocTypeDef(string ContentType, IReadOnlyList<DocField> Fields, string[]? Aliases = null, string? Description = null);

/// <summary>
/// The document taxonomy, mirroring the Content Types on the "Patient Records" library.
/// Drives three stages: classify (pick a ContentType), extract (only that type's Fields),
/// and file (set ContentType + Patient + those columns). Choice vocabularies load separately.
/// Can later be regenerated from SharePoint via Graph (lists/{id}/contentTypes + /columns).
/// </summary>
public static class DocumentCatalog
{
    private static DocField Patient => new("Patient", "Patient", FieldKind.Lookup);
    private static DocField DocDate => new("DocumentDate", "Dokumentendatum", FieldKind.Date);
    // DocumentDate marked REQUIRED for auto-fileable types (Verordnung, Genehmigung): an incomplete one routes to review
    // at the GATE, not just the submit form. Per-type because SharePoint requires DocumentDate on most types but not all
    // (e.g. Beratungsbesuch); the catalog<->SharePoint requiredness drift is cured systematically by the planned startup sync.
    private static DocField DocDateReq => new("DocumentDate", "Dokumentendatum", FieldKind.Date, true);
    private static DocField F(string column, string label, FieldKind kind, bool required = false, RequiredUnless? requiredUnless = null) => new(column, label, kind, required, requiredUnless);

    /// <summary>Bare content type for unknown/low-confidence documents → routed to review.</summary>
    public const string Fallback = "Document";

    public static readonly IReadOnlyList<DocTypeDef> Types = new[]
    {
        new DocTypeDef("Verordnung", new[]{ Patient, DocDateReq,
            F("ValidFrom","Gültig ab",FieldKind.Date), F("ValidUntil","Gültig bis",FieldKind.Date),
            F("PrescriptionOrder","Verordnungsfolge",FieldKind.Choice, required:true) },
            new[]{ "Verordnung häuslicher Krankenpflege","Muster 12","HKP" },
            Description: "Ärztliche Verordnung häuslicher Krankenpflege (Muster 12), vom Arzt/der Praxis ausgestellt und unterschrieben; verordnet Behandlungspflege-Leistungen."),

        new DocTypeDef("Genehmigung", new[]{ Patient, DocDate,
            F("ApprovalNumber","Genehmigungsnummer",FieldKind.Text, requiredUnless: new("ApprovalOutcome", new[]{ "Abgelehnt" })),
            F("ApprovalOutcome","Ergebnis (Genehmigung)",FieldKind.Choice),
            F("ValidFrom","Gültig ab",FieldKind.Date), F("ValidUntil","Gültig bis",FieldKind.Date) },
            Description: "Antwort der Krankenkasse auf eine konkrete ärztliche VERORDNUNG häuslicher Kranken-/Behandlungspflege (Muster 12): genehmigt/bewilligt, abweichend genehmigt oder abgelehnt. Bezieht sich IMMER auf eine konkrete ärztliche Verordnung. KEINE Genehmigung sind allgemeine Kostenzusagen für Pflegesachleistung, Pflegegeld oder Leistungen nach Pflegegrad ohne Bezug zu einer ärztlichen Verordnung — diese sind Kassenkorrespondenz."),

        new DocTypeDef("Bescheid", new[]{ Patient, DocDate,
            F("BenefitType","Bescheidart",FieldKind.Choice),
            F("ValidFrom","Gültig ab",FieldKind.Date), F("ValidUntil","Gültig bis",FieldKind.Date) },
            Description: "Bescheid der Pflegekasse über den PFLEGEGRAD auf Basis der Begutachtung durch den Medizinischen Dienst (Feststellung, Erhöhung, Senkung oder Ablehnung des Pflegegrades). NUR Pflegegrad-Entscheidungen — andere Kassenschreiben sind Kassenkorrespondenz."),

        new DocTypeDef("Gutachten", new[]{ Patient, DocDate,
            F("AssessmentResult","Ergebnis (Gutachten)",FieldKind.Choice), F("EffectiveDate","Gültig ab (Gutachten)",FieldKind.Date),
            F("CareLevel","Pflegegrad",FieldKind.Choice) },
            new[]{ "MD","MDK","Pflegegutachten","Pflegebegutachtung" },
            Description: "Das Pflegegutachten des Medizinischen Dienstes (MD/MDK) selbst — der Begutachtungsbericht zur Feststellung des Pflegegrades (nicht der Bescheid der Kasse)."),

        new DocTypeDef("Leistungsnachweis", new[]{ Patient,
            F("LegalBasis","Leistungsgrundlage",FieldKind.Choice),
            F("ServiceMonthNum","Abrechnungsmonat",FieldKind.Choice), F("ServiceYear","Abrechnungsjahr",FieldKind.Choice) },
            new[]{ "LNW" },
            Description: "Leistungsnachweis (LNW): Nachweis der erbrachten Pflegeleistungen je Tag/Monat, meist mit Unterschriften."),

        new DocTypeDef("Pflegevertrag", new[]{ Patient, DocDate,
            F("ValidFrom","Gültig ab",FieldKind.Date), F("SignedByPatient","Unterschrieben",FieldKind.Bool) },
            new[]{ "Behandlungspflegevertrag","Pflegevertrag" },
            Description: "Pflege- bzw. Behandlungspflegevertrag zwischen dem Pflegedienst und dem Patienten."),

        // The Beratungsdatum is stored in the shared DocumentDate column (a default-view column, so the date is visible
        // when browsing the library) rather than a Beratungsbesuch-only ConsultationDate column that only shows on the item.
        new DocTypeDef("Beratungsbesuch § 37.3", new[]{ Patient, DocDate },
            new[]{ "§37.3","§ 37.3","Beratungseinsatz","Nachweis Beratungseinsatz" },
            Description: "Nachweis des Beratungseinsatzes/Beratungsbesuchs nach § 37.3 SGB XI (bei Bezug von Pflegegeld)."),

        new DocTypeDef("Erstgespräch", new[]{ Patient, DocDate },
            Description: "Dokumentation des Erstgesprächs/Erstbesuchs beim Patienten (Aufnahme/Anamnese durch den Pflegedienst)."),

        new DocTypeDef("Arztbrief", new[]{ Patient, DocDate,
            F("IssuingFacility","Ausstellende Einrichtung",FieldKind.Choice) },
            new[]{ "Entlassbrief","Entlassbericht","Befundbericht","Laborbericht" },
            Description: "Arztbrief, Entlassbrief/Entlassbericht oder Befund-/Laborbericht, insbesondere aus dem Krankenhaus."),

        new DocTypeDef("Medikamentenplan", new[]{ Patient, DocDate,
            F("DocNotes","Bemerkung",FieldKind.Text) },
            new[]{ "BMP","Medikationsplan","Bundeseinheitlicher Medikationsplan" },
            Description: "Medikamenten- bzw. (bundeseinheitlicher) Medikationsplan (BMP): die GESAMTE Dauermedikation als Tabelle, trägt häufig einen DataMatrix-Barcode. Abzugrenzen von einem ärztlichen Rezept/einer Arzneimittelverordnung (Verschreibung einzelner Präparate, oft Muster 16/Kassenrezept) — das ist KEIN Medikationsplan."),

        new DocTypeDef("Allgemeine Korrespondenz", new[]{ Patient, DocDate,
            F("DocSubject","Betreff",FieldKind.Text), F("DocNotes","Bemerkung",FieldKind.Text) },
            Description: "Allgemeiner Schriftverkehr, der keinem spezifischeren Typ zuzuordnen ist."),

        new DocTypeDef("Kassenkorrespondenz", new[]{ Patient, DocDate,
            F("SenderType","Absendertyp",FieldKind.Choice), F("DocSubject","Betreff",FieldKind.Text) },
            Description: "Sonstiger Schriftverkehr mit einer Kranken-/Pflegekasse, der sich NICHT auf eine konkrete ärztliche Verordnung bezieht und KEIN Pflegegrad-Bescheid ist — z. B. allgemeine Mitteilungen, Anfragen, Absetzungen, Zahlungsavise SOWIE Kostenzusagen/Bewilligungen für Pflegesachleistung oder Pflegegeld nach Pflegegrad (z. B. 'wir übernehmen die Pflegesachleistung nach Pflegegrad 3 …')."),

        new DocTypeDef("Sozialamtskorrespondenz", new[]{ Patient, DocDate,
            F("DocSubject","Betreff",FieldKind.Text), F("DocNotes","Bemerkung",FieldKind.Text) },
            Description: "Schriftverkehr mit dem Sozialamt bzw. dem Träger der Sozialhilfe."),

        new DocTypeDef("Patientenverfügung", new[]{ Patient, DocDate,
            F("SignedDate","Unterzeichnet am",FieldKind.Date), F("Notarised","Notariell beglaubigt",FieldKind.Bool),
            F("StorageLocation","Aufbewahrungsort",FieldKind.Text) },
            Description: "Patientenverfügung: schriftliche Festlegung des Patienten zu gewünschten/abgelehnten medizinischen Maßnahmen."),

        new DocTypeDef("Vorsorgevollmacht", new[]{ Patient, DocDate,
            F("SignedDate","Unterzeichnet am",FieldKind.Date), F("AuthorisedPerson","Bevollmächtigte Person",FieldKind.Text),
            F("Notarised","Notariell beglaubigt",FieldKind.Bool), F("StorageLocation","Aufbewahrungsort",FieldKind.Text) },
            new[]{ "Betreuungsvollmacht" },
            Description: "Vorsorge- bzw. Betreuungsvollmacht: Bevollmächtigung einer Person, im Vorsorgefall für Gesundheits- und/oder Vermögensangelegenheiten zu entscheiden. Abzugrenzen von einer Datenschutz-/Schweigepflichtentbindungs-Vollmacht (bloße Einwilligung in die Datenverarbeitung/Weitergabe) und von einer Patientenverfügung — beide sind KEINE Vorsorgevollmacht."),

        new DocTypeDef("Betreuerausweis", new[]{ Patient, DocDate,
            F("CaregiverName","Betreuer",FieldKind.Text) },
            new[]{ "Bestellungsurkunde","Betreuerbestellung" },
            Description: "Betreuerausweis bzw. Bestellungsurkunde des Betreuungsgerichts; weist die rechtliche Betreuung nach."),
    };

    public static DocTypeDef? Find(string contentType) =>
        Types.FirstOrDefault(t => string.Equals(t.ContentType, contentType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Distinct choice columns across all types — these need their allowed-value vocabularies loaded from SharePoint.</summary>
    public static IReadOnlyList<string> ChoiceColumns() =>
        Types.SelectMany(t => t.Fields).Where(f => f.Kind == FieldKind.Choice).Select(f => f.Column).Distinct().ToList();
}
