using System.Globalization;
using System.Text;
using Adelaide.Core;
using Adelaide.Demo;
using Adelaide.SampleGen;
using static Adelaide.SampleGen.Block;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Adelaide demo-corpus generator. Writes demo/samples/*.pdf, demo/fixtures.json and demo/patients.json.
//
// EVERYTHING HERE IS FICTIONAL. Names use the conventional German placeholders (Mustermann, Musterfrau, Beispiel,
// Testfall, Probe); every KVNR is a low-entropy synthetic value with a valid check digit; every organisation is
// labelled "(fiktiv)". Each page carries a "SYNTHETISCHES MUSTERDOKUMENT" banner. The layouts are simple letters —
// they intentionally do NOT reproduce official KBV/insurer form designs.
//
// Each document is defined ONCE as a list of blocks; the same blocks render the PDF page AND the Markdown transcript
// the demo transcriber replays, so the "transcription" is exactly the text on the page. The extracted fields are what
// the production extractor would be expected to return for that page — they are fixtures, not model output.
//
// Usage: dotnet run --project tools/Adelaide.SampleGen [-- <repo-root>]
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

var root = args.Length > 0 ? args[0] : FindRepoRoot();
var demoDir = Path.Combine(root, "demo");
var samplesDir = Path.Combine(demoDir, "samples");
Directory.CreateDirectory(samplesDir);

// ---- synthetic patient directory ----
var kvMusterfrau = Kv("M", "11111111");
var kvTestfallA = Kv("T", "22222222");
var kvTestfallB = Kv("T", "33333333");
var kvProbe = Kv("P", "44444444");
var kvProbeOld = Kv("P", "55555555");
const string KvMustermann = "Z000000005", KvBeispiel = "T000000002"; // the synthetic values the unit tests already use
var kvProbeMisread = kvProbe[..9] + (char)('0' + (kvProbe[9] - '0' + 3) % 10); // one wrong check digit

var patients = new List<DemoCorpus.PatientFixture>
{
    new(101, KvMustermann, "Mustermann", "Max", "1956-05-29", "Aktiv", "K-0101", "1001"),
    new(102, KvBeispiel, "Beispiel", "Anna", "1939-03-17", "Aktiv", "K-0102", "1002"),
    new(103, kvMusterfrau, "Musterfrau", "Erika", "1948-11-02", "Aktiv", "K-0103", "1003"),
    // Two DIFFERENT people with the same name and date of birth — the case name+DOB matching must never guess.
    new(104, kvTestfallA, "Testfall", "Klara", "1944-02-20", "Aktiv", "K-0104", "1004"),
    new(105, kvTestfallB, "Testfall", "Klara", "1944-02-20", "Aktiv", "K-0105", "1005"),
    // Same person recorded twice, one record discharged: matching resolves to the single ACTIVE record.
    new(106, kvProbe, "Probe", "Paul", "1960-01-01", "Aktiv", "K-0106", "1006"),
    new(107, kvProbeOld, "Probe", "Paul", "1960-01-01", "Inaktiv", "K-0107", "1007"),
};

static DemoCorpus.FieldFixture F(string name, string? value, double conf, string source = "vision") => new(name, value, conf, source);

// ---- blocks shared by several documents ----
Block[] Praxis(string doctor) =>
[
    Small($"{doctor} (fiktiv) · Allgemeinmedizin · Musterstraße 1 · 12345 Musterstadt"),
];
Block[] Kasse() =>
[
    Small("Musterkasse – Die fiktive Gesundheitskasse · Postfach 10 00 00 · 12345 Musterstadt"),
];

var docs = new List<(string File, string Scenario, double Legibility, Block[][] Pages, DemoCorpus.FieldFixture[] Fields, DemoCorpus.UnitFixture[]? Units)>();

// 01 — clean prescription, KVNR match → auto-filed
docs.Add(("01-verordnung-mustermann.pdf",
    "Clean Verordnung (home-care prescription): valid KVNR matches exactly one patient, every required field present, type is on the auto-file allowlist → AUTO-FILED.",
    0.96,
    [[.. Praxis("Praxis Dr. med. Beispiel"),
      H("Verordnung häuslicher Krankenpflege"),
      KV("Krankenkasse", "Musterkasse (fiktiv)"),
      KV("Name, Vorname", "Mustermann, Max"), KV("geb. am", "29.05.1956"), KV("Versicherten-Nr.", KvMustermann),
      Chk(true, "Erstverordnung"), Chk(false, "Folgeverordnung"),
      KV("Zeitraum", "vom 01.10.2026 bis 31.12.2026"),
      P("Behandlungspflege: Medikamentengabe 2x täglich; Anziehen von Kompressionsstrümpfen beidseits."),
      KV("Ausstellungsdatum", "28.09.2026"), Small("Unterschrift und Stempel des Arztes (fiktiv)")]],
    [F("Domain", "Medical", 0.99), F("ContentType", "Verordnung", 0.97, "vision/verified"),
     F("KVNR", KvMustermann, 0.97), F("LastName", "Mustermann", 0.98), F("FirstName", "Max", 0.98),
     F("BirthDate", "1956-05-29", 0.96), F("DocumentDate", "2026-09-28", 0.95),
     F("ValidFrom", "2026-10-01", 0.94), F("ValidUntil", "2026-12-31", 0.94),
     F("PrescriptionOrder", "Erstverordnung", 0.96, "di")],
    null));

// 02 — payer approval, auto-filed
Block[] Approval(string faxHeader) =>
[
    .. FaxLine(faxHeader),
    .. Kasse(),
    H("Genehmigung häuslicher Krankenpflege"),
    KV("Versicherte", "Anna Beispiel, geb. 17.03.1939"), KV("Versicherten-Nr.", KvBeispiel),
    KV("Genehmigungsnummer", "GEN-2026-000123"),
    P("Die ärztliche Verordnung vom 15.09.2026 genehmigen wir wie verordnet für den Zeitraum 01.10.2026 bis 31.12.2026."),
    KV("Datum", "22.09.2026"), Small("Musterkasse (fiktiv) – Leistungsabteilung"),
];
DemoCorpus.FieldFixture[] approvalFields =
[
    F("Domain", "Medical", 0.99), F("ContentType", "Genehmigung", 0.96, "vision/verified"),
    F("KVNR", KvBeispiel, 0.97), F("LastName", "Beispiel", 0.97), F("FirstName", "Anna", 0.97),
    F("BirthDate", "1939-03-17", 0.95), F("DocumentDate", "2026-09-22", 0.94),
    F("ApprovalNumber", "GEN-2026-000123", 0.93), F("ApprovalOutcome", "Wie verordnet", 0.92),
    F("ValidFrom", "2026-10-01", 0.93), F("ValidUntil", "2026-12-31", 0.93),
];
docs.Add(("02-genehmigung-beispiel.pdf",
    "Payer approval (Genehmigung) for a valid KVNR → AUTO-FILED. Upload it a second time to see the exact-bytes (SHA-256) duplicate hold.",
    0.95, [Approval("")], approvalFields, null));

// 03 — the same approval re-sent by fax: different bytes, same business key → duplicate review
docs.Add(("03-genehmigung-beispiel-fax-kopie.pdf",
    "The same approval arriving again by fax: different bytes, but the same patient + type + date + period → layer-2 business-key duplicate → REVIEW with side-by-side compare. Process 02 first.",
    0.88, [Approval("FAX  22.09.2026 14:02  Musterkasse (fiktiv)  S. 1/1")], approvalFields, null));

// 04 — discharge letter, matched by name + DOB, not on the auto-file allowlist → review
docs.Add(("04-arztbrief-musterfrau.pdf",
    "Hospital discharge letter with no KVNR: matched by surname + date of birth; Arztbrief is not on the auto-file allowlist → REVIEW with the patient pre-selected.",
    0.93,
    [[Small("Klinikum Beispielstadt (fiktiv) · Klinik für Orthopädie · Klinikweg 2 · 12345 Musterstadt"),
      H("Entlassbrief"),
      KV("Patientin", "Erika Musterfrau, geb. 02.11.1948"),
      KV("Aufenthalt", "21.09.2026 – 04.10.2026"),
      P("Diagnose: Zustand nach Hüft-TEP rechts (fiktiver Befund). Weiterbehandlung: Wundkontrolle, Mobilisation, Thromboseprophylaxe."),
      KV("Datum", "05.10.2026"), Small("Mit freundlichen Grüßen – Dr. med. Muster (fiktiv), Oberarzt")]],
    [F("Domain", "Medical", 0.99), F("ContentType", "Arztbrief", 0.93, "vision/verified"),
     F("LastName", "Musterfrau", 0.95), F("FirstName", "Erika", 0.95), F("BirthDate", "1948-11-02", 0.94),
     F("DocumentDate", "2026-10-05", 0.92), F("IssuingFacility", "Krankenhaus", 0.90)],
    null));

// 05 — name + DOB collision → ambiguous → reviewer picks
docs.Add(("05-arztbrief-testfall-namensgleich.pdf",
    "Letter for 'Klara Testfall, 20.02.1944' — two different active patients share that name and birth date → AMBIGUOUS → REVIEW with both candidates offered; nothing is guessed.",
    0.92,
    [[Small("Praxis für Kardiologie Dr. med. Probe (fiktiv) · Herzweg 3 · 12345 Musterstadt"),
      H("Befundbericht"),
      KV("Patientin", "Klara Testfall, geb. 20.02.1944"),
      P("Langzeit-EKG ohne pathologischen Befund (fiktiv). Kontrolle in 6 Monaten."),
      KV("Datum", "07.10.2026")]],
    [F("Domain", "Medical", 0.99), F("ContentType", "Arztbrief", 0.91, "vision/verified"),
     F("LastName", "Testfall", 0.94), F("FirstName", "Klara", 0.94), F("BirthDate", "1944-02-20", 0.93),
     F("DocumentDate", "2026-10-07", 0.92), F("IssuingFacility", "Praxis", 0.88)],
    null));

// 06 — KVNR misread (check digit fails) → never looked up; name+DOB finds the one ACTIVE record; still review
docs.Add(("06-verordnung-kvnr-pruefziffer.pdf",
    "Prescription whose KVNR was read with a wrong check digit: the invalid number is never looked up; name + DOB resolves to the single ACTIVE record of two; the failed check digit still forces REVIEW.",
    0.81,
    [[.. Praxis("Praxis Dr. med. Beispiel"),
      H("Verordnung häuslicher Krankenpflege"),
      KV("Name, Vorname", "Probe, Paul"), KV("geb. am", "01.01.1960"), KV("Versicherten-Nr.", kvProbeMisread),
      Chk(false, "Erstverordnung"), Chk(true, "Folgeverordnung"),
      KV("Zeitraum", "vom 01.11.2026 bis 31.01.2027"),
      P("Behandlungspflege: Blutzuckermessung 1x täglich."),
      KV("Ausstellungsdatum", "20.10.2026")]],
    [F("Domain", "Medical", 0.99), F("ContentType", "Verordnung", 0.95, "vision/verified"),
     F("KVNR", kvProbeMisread, 0.86), F("LastName", "Probe", 0.95), F("FirstName", "Paul", 0.95),
     F("BirthDate", "1960-01-01", 0.93), F("DocumentDate", "2026-10-20", 0.92),
     F("ValidFrom", "2026-11-01", 0.91), F("ValidUntil", "2027-01-31", 0.91),
     F("PrescriptionOrder", "Folgeverordnung", 0.93, "di")],
    null));

// 07 — business document (no patient) → business lane → reviewer picks the Zielbereich
docs.Add(("07-rechnung-buerobedarf.pdf",
    "An office-supplies invoice: classified as a BUSINESS document (no patient dimension) → the business lane: REVIEW to choose a Zielbereich, then filed to the separate business library.",
    0.94,
    [[Small("Bürobedarf Beispiel GmbH (fiktiv) · Papierstraße 9 · 12345 Musterstadt"),
      H("Rechnung Nr. 2026-0042"),
      KV("Rechnungsdatum", "12.10.2026"),
      P("2 x Druckerpapier A4 (500 Blatt) · 1 x Ordner-Set · 1 x Toner schwarz"),
      KV("Gesamtbetrag", "89,40 EUR (fiktiv)"),
      P("Zahlbar innerhalb von 14 Tagen ohne Abzug.")]],
    [F("Domain", "Business", 0.97), F("DocumentDate", "2026-10-12", 0.93)],
    null));

// 09 — two documents for two patients in one scan → reviewer splits; each child extracts its own fields
docs.Add(("09-stapel-zwei-dokumente.pdf",
    "One scan containing two documents for two patients (a prescription for Anna Beispiel, then a lab report for Max Mustermann) → identity cannot be attributed → REVIEW. Split it into pages 1 and 2 in the review screen: each child is re-extracted on its own pages.",
    0.90,
    [[.. Praxis("Praxis Dr. med. Beispiel"),
      H("Verordnung häuslicher Krankenpflege"),
      KV("Name, Vorname", "Beispiel, Anna"), KV("geb. am", "17.03.1939"), KV("Versicherten-Nr.", KvBeispiel),
      Chk(false, "Erstverordnung"), Chk(true, "Folgeverordnung"),
      KV("Zeitraum", "vom 01.01.2027 bis 31.03.2027"),
      P("Behandlungspflege: Medikamentengabe 3x täglich."),
      KV("Ausstellungsdatum", "21.10.2026")],
     [Small("Labor Musterstadt (fiktiv) · Laborweg 5 · 12345 Musterstadt"),
      H("Laborbericht"),
      KV("Patient", "Max Mustermann, geb. 29.05.1956"),
      P("Kleines Blutbild unauffällig; HbA1c 6,1 % (fiktive Werte)."),
      KV("Datum", "22.10.2026")]],
    [F("Domain", "Medical", 0.90), F("ContentType", DocumentCatalog.Fallback, 0.55, "vision/unverified"),
     F("LastName", "Beispiel", 0.60), F("FirstName", "Anna", 0.60), F("BirthDate", "1939-03-17", 0.58)],
    [new(1, 1, [F("Domain", "Medical", 0.99), F("ContentType", "Verordnung", 0.95, "vision/verified"),
               F("KVNR", KvBeispiel, 0.96), F("LastName", "Beispiel", 0.97), F("FirstName", "Anna", 0.97),
               F("BirthDate", "1939-03-17", 0.95), F("DocumentDate", "2026-10-21", 0.93),
               F("ValidFrom", "2027-01-01", 0.92), F("ValidUntil", "2027-03-31", 0.92),
               F("PrescriptionOrder", "Folgeverordnung", 0.94, "di")]),
     new(2, 2, [F("Domain", "Medical", 0.99), F("ContentType", "Arztbrief", 0.92, "vision/verified"),
               F("LastName", "Mustermann", 0.96), F("FirstName", "Max", 0.96), F("BirthDate", "1956-05-29", 0.95),
               F("DocumentDate", "2026-10-22", 0.91), F("IssuingFacility", "Sonstiges", 0.85)])]));

// ---- write PDFs + fixtures ----
var fixtures = new List<DemoCorpus.DocumentFixture>();
foreach (var (file, scenario, legibility, pages, fields, units) in docs)
{
    var pdf = new MiniPdf();
    foreach (var page in pages) Render(pdf.NewPage().SyntheticBanner(), page);
    var bytes = pdf.ToBytes();
    File.WriteAllBytes(Path.Combine(samplesDir, file), bytes);
    fixtures.Add(new(file, DemoCorpus.Sha256Hex(bytes), scenario, legibility, pages.Select(ToMarkdown).ToList(), fields, units));
    Console.WriteLine($"  {file,-44} {bytes.Length,6} B  {pages.Length}p  sha256={DemoCorpus.Sha256Hex(bytes)[..12]}…");
}

// 08 — an "unreadable fax": deliberately NOT in fixtures.json, so the demo transcriber knows nothing about it
{
    var pdf = new MiniPdf().NewPage().SyntheticBanner();
    for (var y = 70; y < 800; y += 14) pdf.Box(40 + (y * 37 % 60), y, 420 + (y * 53 % 90), 7, gray: 0.15 + (y % 5) * 0.05);
    pdf.Text(60, 820, "(simulierter, unleserlicher Faxeingang)", 8);
    File.WriteAllBytes(Path.Combine(samplesDir, "08-unleserliches-fax.pdf"), pdf.ToBytes());
    Console.WriteLine("  08-unleserliches-fax.pdf                     (not in fixtures: transcribes to nothing → review)");
}

DemoCorpus.Write(Path.Combine(demoDir, "fixtures.json"), new DemoCorpus.CorpusFile(fixtures));
DemoCorpus.Write(Path.Combine(demoDir, "patients.json"), patients);

// Self-check: every patient KVNR is check-digit valid; the deliberate misread is not.
foreach (var p in patients) if (!Kvnr.IsValid(p.Kvnr)) throw new InvalidOperationException($"synthetic KVNR invalid: {p.Kvnr}");
if (Kvnr.IsValid(kvProbeMisread)) throw new InvalidOperationException("misread KVNR unexpectedly valid");
Console.WriteLine($"Wrote {fixtures.Count + 1} PDFs, fixtures.json ({fixtures.Count} documents) and patients.json ({patients.Count} patients) to {demoDir}");
return 0;

// ---- helpers ----
static Block[] FaxLine(string header) => header.Length > 0 ? [Small(header)] : [];

static string Kv(string letter, string eightDigits)
{
    for (var d = 0; d <= 9; d++)
    {
        var candidate = letter + eightDigits + d.ToString(CultureInfo.InvariantCulture);
        if (Kvnr.IsValid(candidate)) return candidate;
    }
    throw new InvalidOperationException("no check digit");
}

static void Render(MiniPdf pdf, Block[] blocks)
{
    double y = 64;
    foreach (var b in blocks)
    {
        switch (b.Kind)
        {
            case BlockKind.Small: pdf.Text(56, y, b.Text, 8); y += 22; break;
            case BlockKind.Heading: y += 10; pdf.Text(56, y, b.Text, 16, bold: true); pdf.Line(56, y + 6, 539, y + 6); y += 32; break;
            case BlockKind.Field: pdf.Text(56, y, b.Label + ":", 10, bold: true); pdf.Text(200, y, b.Text, 11); y += 22; break;
            case BlockKind.Check: pdf.Check(56, y, b.Ticked, b.Text); y += 20; break;
            case BlockKind.Paragraph:
                foreach (var line in Wrap(b.Text, 88)) { pdf.Text(56, y, line, 10.5); y += 15; }
                y += 10; break;
        }
    }
}

static string ToMarkdown(Block[] blocks)
{
    var sb = new StringBuilder();
    foreach (var b in blocks)
        sb.Append(b.Kind switch
        {
            BlockKind.Small => b.Text + "\n\n",
            BlockKind.Heading => "# " + b.Text + "\n\n",
            BlockKind.Field => $"**{b.Label}:** {b.Text}\n\n",
            BlockKind.Check => $"[{(b.Ticked ? "X" : " ")}] {b.Text}\n\n",
            _ => b.Text + "\n\n",
        });
    return "SYNTHETISCHES MUSTERDOKUMENT - fiktive Daten, keine echte Person, Einrichtung oder Kasse\n\n" + sb.ToString().TrimEnd();
}

static IEnumerable<string> Wrap(string text, int width)
{
    var line = new StringBuilder();
    foreach (var word in text.Split(' '))
    {
        if (line.Length > 0 && line.Length + word.Length + 1 > width) { yield return line.ToString(); line.Clear(); }
        if (line.Length > 0) line.Append(' ');
        line.Append(word);
    }
    if (line.Length > 0) yield return line.ToString();
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Adelaide.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException("Run from inside the repository, or pass the repo root as the first argument.");
}

namespace Adelaide.SampleGen
{
    internal enum BlockKind { Small, Heading, Field, Check, Paragraph }

    internal sealed record Block(BlockKind Kind, string Text, string Label = "", bool Ticked = false)
    {
        public static Block Small(string t) => new(BlockKind.Small, t);
        public static Block H(string t) => new(BlockKind.Heading, t);
        public static Block KV(string label, string value) => new(BlockKind.Field, value, label);
        public static Block Chk(bool ticked, string label) => new(BlockKind.Check, label, Ticked: ticked);
        public static Block P(string t) => new(BlockKind.Paragraph, t);
    }
}
