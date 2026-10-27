using Adelaide.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Adelaide.Worker;

/// <summary>
/// Live end-to-end of the AI extraction slice on a fictitious Verordnung: gpt-4o classify+extract →
/// live patient match → review gate. No PDF rendering yet (feeds canned Markdown); no write to SharePoint.
/// The KVNR is fictitious, so "no match → Review" is the expected, correct outcome.
/// </summary>
public static class ExtractDemo
{
    public static async Task RunAsync(IServiceProvider sp)
    {
        var extractor = sp.GetRequiredService<IExtractor>();
        var directory = sp.GetRequiredService<IPatientDirectory>();

        var unit = new DocumentUnit("extract-demo-1", 1, 1,
            new Transcript(SampleMarkdown, 1, 0.95, Array.Empty<DecodedBarcode>()));

        Console.WriteLine("Extracting (live gpt-4o: classify + extract)…\n");
        var data = await extractor.ExtractAsync(unit);

        Console.WriteLine("Extracted fields:");
        foreach (var f in data.Fields)
            Console.WriteLine($"  {f.Name,-14} = {Trunc(f.Value),-44} (conf {f.Confidence:0.00}, {f.Source})");

        var match = await directory.MatchAsync(data);
        Console.WriteLine($"\nMatch: {match.Outcome} via {match.Method}" +
            (match.Patient is { } p ? $" → #{p.ListItemId} {p.LastName}" : "  (fictitious KVNR → None is expected)"));

        var result = IntakePipeline.Decide(unit, unit.Transcript, data, match, new PipelineOptions());
        Console.WriteLine($"Disposition: {result.Disposition}");
        if (result.Reasons.Count > 0)
            Console.WriteLine("  reasons: " + string.Join("; ", result.Reasons));
    }

    private static string Trunc(string? s) => s is null ? "(null)" : s.Length <= 44 ? s : s[..41] + "...";

    // Fictitious sample (no real patient data).
    private const string SampleMarkdown = @"# Verordnung häuslicher Krankenpflege (Muster 12)

Krankenkasse bzw. Kostenträger: AOK Bayern
Versicherte/r: Frau Erika Mustermann
Geburtsdatum: 14.03.1948
Versichertennummer: Z000000005

[X] Erstverordnung   [ ] Folgeverordnung

Verordnungszeitraum: 01.06.2026 bis 30.06.2026

Diagnose: Diabetes mellitus Typ 2; Z.n. Apoplex.
Verordnete Maßnahmen der Behandlungspflege: Medikamentengabe, Blutzuckermessung,
An- und Ausziehen von Kompressionsstrümpfen.

Ausgestellt am 28.05.2026
Dr. med. Hans Bergmann, Facharzt für Allgemeinmedizin";
}
