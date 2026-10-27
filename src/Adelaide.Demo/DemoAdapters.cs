using Adelaide.Ai;
using Adelaide.Core;

namespace Adelaide.Demo;

/// <summary>Demo stand-in for the vision transcriber (Azure OpenAI GPT-4o page images → Markdown + local barcode decode).
/// Looks the uploaded PDF up in the synthetic corpus by SHA-256 and replays its recorded page Markdown. An unknown PDF
/// yields an EMPTY transcript with legibility 0 — the gate's empty-transcript and low-quality checks then route it to
/// review, which is exactly how production treats an unreadable scan.</summary>
public sealed class DemoTranscriber(DemoCorpus corpus) : ITranscriber
{
    public async Task<Transcript> TranscribeAsync(IncomingDocument doc, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(doc.SourcePath, ct);
        if (corpus.FindBySha256(DemoCorpus.Sha256Hex(bytes)) is { } fx)
            return new Transcript(DemoCorpus.JoinPages(fx.Pages), fx.Pages.Count, fx.Legibility, Array.Empty<DecodedBarcode>(),
                PageMarkdown: fx.Pages, ModelDeployment: DemoCorpus.ModelId, ModelServed: DemoCorpus.ModelId);

        int pages;
        try { pages = PdfPageOps.PageCount(bytes); }
        catch (Exception) { pages = 1; } // unparseable → still a review item, never a crash
        return new Transcript("", pages, 0d, Array.Empty<DecodedBarcode>(),
            PageMarkdown: Enumerable.Repeat("", pages).ToList(), ModelDeployment: DemoCorpus.ModelId, ModelServed: DemoCorpus.ModelId);
    }
}

/// <summary>Demo stand-in for the structured extractor (Azure OpenAI, forced JSON schema, barcode + checkbox evidence).
/// Replays the fields recorded for this unit's transcript text; anything unrecognised extracts nothing.</summary>
public sealed class DemoExtractor(DemoCorpus corpus) : IExtractor
{
    public Task<ExtractedData> ExtractAsync(DocumentUnit unit, CancellationToken ct = default)
    {
        var fields = corpus.FindFieldsByTranscript(unit.Transcript.Markdown) ?? [];
        return Task.FromResult(new ExtractedData(
            fields.Select(f => new ExtractedField(f.Name, f.Value, f.Confidence, f.Source)).ToList(),
            ModelDeployment: DemoCorpus.ModelId, ModelServed: DemoCorpus.ModelId));
    }
}

/// <summary>Demo stand-in for the SharePoint "Patients" list (in production a 15-minute replica of the care-management
/// system's patient master data, queried via Microsoft Graph). Same matching strategy as the Graph adapter —
/// <see cref="PatientMatching.MatchAsync"/> — over the synthetic <c>demo/patients.json</c>.</summary>
public sealed class DemoPatientDirectory(IReadOnlyList<PatientRef> patients) : IPatientDirectory, IPatientSearch
{
    public Task<PatientMatch> MatchAsync(ExtractedData data, CancellationToken ct = default) =>
        PatientMatching.MatchAsync(data,
            (kvnr, _) => Task.FromResult<IReadOnlyList<PatientRef>>(
                patients.Where(p => string.Equals(p.Kvnr, kvnr, StringComparison.OrdinalIgnoreCase)).ToList()),
            (last, _) => Task.FromResult<IReadOnlyList<PatientRef>>(
                patients.Where(p => string.Equals(p.LastName, last, StringComparison.OrdinalIgnoreCase)).ToList()),
            ct);

    /// <summary>Mirrors GraphPatientDirectory.SearchAsync: a full KVNR matches exactly, otherwise a surname prefix.</summary>
    public Task<IReadOnlyList<PatientRef>> SearchAsync(string query, int max = 25, CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (query.Length < 2) return Task.FromResult<IReadOnlyList<PatientRef>>(Array.Empty<PatientRef>());
        var looksLikeKvnr = query.Length == 10 && char.IsLetter(query[0]) && query.Skip(1).All(char.IsDigit);
        var hits = patients.Where(p => looksLikeKvnr
            ? string.Equals(p.Kvnr, query, StringComparison.OrdinalIgnoreCase)
            : p.LastName.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<PatientRef>>(hits.Take(max).ToList());
    }
}

/// <summary>Demo stand-in for Document Intelligence's searchable-PDF rendition: unavailable, so the filer falls back
/// to the original PDF — the same degraded-but-safe path production takes when DI is down.</summary>
public sealed class NoTextLayer : IPdfTextLayer
{
    public Task<byte[]?> MakeSearchableAsync(byte[] pdf, CallContext? ctx = null, CancellationToken ct = default) =>
        Task.FromResult<byte[]?>(null);
}
