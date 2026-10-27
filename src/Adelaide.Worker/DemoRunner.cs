using Adelaide.Core;

namespace Adelaide.Worker;

/// <summary>Offline demonstration of the verify-or-review gate using stub adapters (no cloud).</summary>
internal static class DemoRunner
{
    public static async Task RunAsync()
    {
        static Transcript T(double quality) => new("# transcript (markdown)…", 1, quality, Array.Empty<DecodedBarcode>());
        static ExtractedField F(string name, string? value, double conf, string source) => new(name, value, conf, source);
        static ExtractedData D(params ExtractedField[] fields) => new(fields);

        // Stand-in for the SharePoint "Patients" list (synthetic test data — no real patients).
        var patients = new[]
        {
            new PatientRef(1, "Z000000005", "Mustermann", "Max",   new DateOnly(1956, 5, 29), "Active"),
            new PatientRef(2, "T000000002", "Beispiel",      "Anna", new DateOnly(1939, 3, 17), "Active"),
        };

        var transcripts = new Dictionary<string, Transcript>
        {
            ["bmp-001"] = T(0.95), ["letter-002"] = T(0.90), ["lab-003"] = T(0.88), ["bad-004"] = T(0.50), ["poison-005"] = T(0.00),
        };

        var extracts = new Dictionary<string, ExtractedData>
        {
            ["bmp-001"] = D(F("KVNR", "Z000000005", 0.99, "barcode"), F("LastName", "Mustermann", 0.96, "barcode"),
                            F("BirthDate", "1956-05-29", 0.98, "barcode"), F("DocumentType", "Medikamentenplan", 0.97, "barcode")),
            ["letter-002"] = D(F("LastName", "Beispiel", 0.93, "vision"), F("BirthDate", "1939-03-17", 0.91, "vision"),
                               F("DocumentType", "Arztbrief", 0.92, "vision")),
            ["lab-003"] = D(F("LastName", "Mustermann", 0.90, "vision"), F("BirthDate", "1970-01-01", 0.90, "vision"),
                            F("DocumentType", "Laborbericht", 0.90, "vision")),
            ["bad-004"] = D(F("KVNR", "Z000000004", 0.60, "vision"), F("LastName", "Unklar", 0.55, "vision"),
                            F("DocumentType", "Sonstiges", 0.50, "vision")),
            ["poison-005"] = D(),
        };

        var pipeline = new IntakePipeline(
            new ScenarioTranscriber(transcripts, new HashSet<string> { "poison-005" }),
            new PassthroughSeparator(),
            new ScenarioExtractor(extracts),
            new InMemoryPatientDirectory(patients),
            new ConsoleSink(),
            new ConsoleLedger());

        var inbox = new[]
        {
            new IncomingDocument("bmp-001",    "samples/bmp.pdf"   , SourceChannel.Fax,   "bmp.pdf"),
            new IncomingDocument("letter-002", "samples/letter.pdf"     ,        SourceChannel.Email, "letter.pdf"),
            new IncomingDocument("lab-003",    "samples/lab.pdf",               SourceChannel.Email, "lab.pdf"),
            new IncomingDocument("bad-004",    "samples/bad.pdf",               SourceChannel.Fax,   "bad.pdf"),
            new IncomingDocument("poison-005", "samples/poison.pdf",            SourceChannel.Api,   "poison.pdf"),
        };

        Console.WriteLine("Adelaide intake — offline demo (stub adapters)\n");
        foreach (var doc in inbox)
        {
            Console.WriteLine($"■ {doc.DocumentId}  ({doc.Channel})");
            try
            {
                foreach (var r in await pipeline.ProcessAsync(doc))
                    Console.WriteLine($"   => {r.Disposition}  (patient: {r.Match.Outcome} via {r.Match.Method})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   => DEAD-LETTER — held for a human, not lost ({ex.Message})");
            }
            Console.WriteLine();
        }
    }
}
