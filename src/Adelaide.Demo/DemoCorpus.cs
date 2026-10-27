using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Adelaide.Core;

namespace Adelaide.Demo;

/// <summary>
/// The synthetic demo corpus: pre-recorded transcription + extraction results for the generated sample PDFs in
/// <c>demo/samples/</c>, plus the synthetic patient directory. Every name, KVNR, date and organisation in it is
/// fictional (see demo/README.md); the files are produced by <c>tools/Adelaide.SampleGen</c>.
///
/// What it is NOT: a model. Demo mode does not run OCR or an LLM — it replays the fields recorded here, so the REAL
/// downstream stages (KVNR validation, patient matching, the verify-or-review gate, the three duplicate layers, the
/// hash-chained ledger, the outbox filer and the review UI) can be exercised end-to-end without any cloud service.
/// A document that is not in the corpus transcribes to nothing and is routed to review, exactly as an unreadable scan.
/// </summary>
public sealed class DemoCorpus
{
    public sealed record FieldFixture(string Name, string? Value, double Confidence, string Source);

    /// <summary>A pre-recorded extraction for a page range (1-based, inclusive) of a fixture document — lets the demo
    /// show a reviewer splitting a two-document scan, each child then extracting its own fields.</summary>
    public sealed record UnitFixture(int FirstPage, int LastPage, IReadOnlyList<FieldFixture> Fields);

    public sealed record DocumentFixture(
        string File,
        string Sha256,
        string Scenario,
        double Legibility,
        IReadOnlyList<string> Pages,
        IReadOnlyList<FieldFixture> Fields,
        IReadOnlyList<UnitFixture>? Units = null);

    public sealed record PatientFixture(int ListItemId, string Kvnr, string LastName, string FirstName, string BirthDate,
        string Status, string? KlientId = null, string? PatientNr = null);

    public sealed record CorpusFile(IReadOnlyList<DocumentFixture> Documents);

    /// <summary>Recorded "model" identity stamped on every demo transcript/extraction, so the ledger and capture rows
    /// say plainly that no real model produced them.</summary>
    public const string ModelId = "demo-fixture";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly Dictionary<string, DocumentFixture> _bySha;
    private readonly Dictionary<string, IReadOnlyList<FieldFixture>> _byMarkdown;

    public IReadOnlyList<DocumentFixture> Documents { get; }
    public IReadOnlyList<PatientRef> Patients { get; }

    public DemoCorpus(IReadOnlyList<DocumentFixture> documents, IReadOnlyList<PatientFixture> patients)
    {
        Documents = documents;
        Patients = patients.Select(ToPatientRef).ToList();
        _bySha = documents.ToDictionary(d => d.Sha256, StringComparer.OrdinalIgnoreCase);

        // Extraction is keyed by the transcript TEXT the demo transcriber produced, so a reviewer-split child (which
        // reuses its slice of the parent transcript and is never re-transcribed) resolves to the fields recorded for
        // exactly that page range.
        _byMarkdown = new Dictionary<string, IReadOnlyList<FieldFixture>>(StringComparer.Ordinal);
        foreach (var d in documents)
        {
            _byMarkdown.TryAdd(JoinPages(d.Pages), d.Fields);
            foreach (var u in d.Units ?? [])
                _byMarkdown.TryAdd(JoinPages(d.Pages.Skip(u.FirstPage - 1).Take(u.LastPage - u.FirstPage + 1)), u.Fields);
        }
    }

    /// <summary>The join the web host uses when it reassembles a split child's carried page slices
    /// (<c>string.Join("\n\n", pages)</c>) — the demo transcriber MUST produce the identical string.</summary>
    public static string JoinPages(IEnumerable<string> pages) => string.Join("\n\n", pages);

    public DocumentFixture? FindBySha256(string sha) => _bySha.GetValueOrDefault(sha);

    public IReadOnlyList<FieldFixture>? FindFieldsByTranscript(string markdown) =>
        string.IsNullOrEmpty(markdown) ? null : _byMarkdown.GetValueOrDefault(markdown);

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Load <c>fixtures.json</c> + <c>patients.json</c> from <paramref name="dir"/>.</summary>
    public static DemoCorpus Load(string dir)
    {
        var docs = Read<CorpusFile>(Path.Combine(dir, "fixtures.json")).Documents;
        var patients = Read<List<PatientFixture>>(Path.Combine(dir, "patients.json"));
        return new DemoCorpus(docs, patients);
    }

    public static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + "\n");

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Demo data file is empty or invalid: {path}");

    private static PatientRef ToPatientRef(PatientFixture p) => new(
        p.ListItemId, p.Kvnr, p.LastName, p.FirstName,
        DateOnly.TryParseExact(p.BirthDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
        p.Status, p.KlientId, p.PatientNr);
}
