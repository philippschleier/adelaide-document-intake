using Adelaide.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Adelaide.Worker;

/// <summary>
/// Full pipeline on a real PDF: transcribe (render → local barcode → gpt-4o vision) → extract → live match → gate.
/// PHI-safe: prints only non-identifying signal (page/field/barcode counts, content type, confidence, disposition).
/// Never prints names, KVNRs, field values, or the transcript. Does NOT write to SharePoint.
/// </summary>
public static class IngestDemo
{
    public static async Task RunAsync(IServiceProvider sp, string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            Console.Error.WriteLine($"PDF not found: '{pdfPath}'. Usage: ingest <path-to-pdf>");
            return;
        }

        var transcriber = sp.GetRequiredService<ITranscriber>();
        var extractor = sp.GetRequiredService<IExtractor>();
        var directory = sp.GetRequiredService<IPatientDirectory>();

        Console.WriteLine($"Ingesting {Path.GetFileName(pdfPath)}  (render → barcode → gpt-4o vision → extract → match → gate)\n");

        var doc = new IncomingDocument("ingest-1", pdfPath, SourceChannel.Fax, Path.GetFileName(pdfPath));
        var t = await transcriber.TranscribeAsync(doc);
        Console.WriteLine($"Transcript : {t.PageCount} page(s), legibility {t.QualityScore:0.00}, {t.Markdown.Length} chars of Markdown");
        Console.WriteLine($"Barcodes   : {t.Barcodes.Count}" +
            (t.Barcodes.Count > 0 ? "  [" + string.Join(", ", t.Barcodes.Select(b => $"{b.Symbology}/{(b.ChecksumValid ? "valid" : "other")}")) + "]" : ""));

        var unit = new DocumentUnit("ingest-1", 1, t.PageCount, t, pdfPath);
        var data = await extractor.ExtractAsync(unit);
        var type = data.Value("ContentType") ?? "(none)";
        var names = data.Fields.Select(f => f.Name).Where(n => n != "ContentType").ToList();
        Console.WriteLine($"\nClassified : {type}");
        Console.WriteLine($"Extracted  : {names.Count} field(s) → {string.Join(", ", names)}   (min confidence {data.MinConfidence():0.00})");
        Console.WriteLine("             (values withheld — PHI)");

        var match = await directory.MatchAsync(data);
        Console.WriteLine($"\nMatch      : {match.Outcome} via {match.Method}");

        var result = IntakePipeline.Decide(unit, t, data, match, new PipelineOptions());
        Console.WriteLine($"Disposition: {result.Disposition}");
        if (result.Reasons.Count > 0) Console.WriteLine("  reasons  : " + string.Join("; ", result.Reasons));
    }
}
