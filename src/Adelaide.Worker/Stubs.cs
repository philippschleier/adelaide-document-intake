using System.Globalization;
using Adelaide.Core;

namespace Adelaide.Worker;

// Stub adapters so the pipeline runs end-to-end with no cloud. Each will be replaced by a real
// adapter (Azure DI / GPT-4o transcription+extraction, Graph "Patient Management", SharePoint sink, durable ledger).

public sealed class ScenarioTranscriber(IReadOnlyDictionary<string, Transcript> map, ISet<string> poison) : ITranscriber
{
    public Task<Transcript> TranscribeAsync(IncomingDocument doc, CancellationToken ct = default)
    {
        if (poison.Contains(doc.DocumentId))
            throw new InvalidOperationException("corrupt/encrypted PDF — cannot transcribe");
        return Task.FromResult(map[doc.DocumentId]);
    }
}

public sealed class PassthroughSeparator : ISeparator
{
    public Task<IReadOnlyList<DocumentUnit>> SeparateAsync(IncomingDocument doc, Transcript whole, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DocumentUnit>>(new[] { new DocumentUnit(doc.DocumentId + "-u1", 1, whole.PageCount, whole) });
}

public sealed class ScenarioExtractor(IReadOnlyDictionary<string, ExtractedData> map) : IExtractor
{
    public Task<ExtractedData> ExtractAsync(DocumentUnit unit, CancellationToken ct = default)
    {
        var docId = unit.UnitId.EndsWith("-u1") ? unit.UnitId[..^3] : unit.UnitId;
        return Task.FromResult(map[docId]);
    }
}

/// <summary>Stands in for the SharePoint "Patient Management" list: KVNR-first, else name+DOB.</summary>
public sealed class InMemoryPatientDirectory(IEnumerable<PatientRef> patients) : IPatientDirectory
{
    private readonly List<PatientRef> _patients = patients.ToList();

    public Task<PatientMatch> MatchAsync(ExtractedData data, CancellationToken ct = default)
    {
        var kvnr = data.Value("KVNR");
        if (!string.IsNullOrEmpty(kvnr) && Kvnr.IsValid(kvnr))
        {
            var hits = _patients.Where(p => p.Kvnr == kvnr).ToList();
            if (hits.Count > 0)
                return Task.FromResult(new PatientMatch(hits.Count == 1 ? MatchOutcome.Single : MatchOutcome.Multiple, hits[0], "kvnr"));
        }

        var last = data.Value("LastName");
        if (!string.IsNullOrEmpty(last) &&
            DateOnly.TryParseExact(data.Value("BirthDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob)) // NormDate guarantees ISO-or-absent; exact parse, never lenient
        {
            var hits = _patients.Where(p => string.Equals(p.LastName, last, StringComparison.OrdinalIgnoreCase) && p.BirthDate == dob).ToList();
            var outcome = hits.Count switch { 1 => MatchOutcome.Single, 0 => MatchOutcome.None, _ => MatchOutcome.Multiple };
            return Task.FromResult(new PatientMatch(outcome, hits.Count == 1 ? hits[0] : null, "name+dob"));
        }

        return Task.FromResult(new PatientMatch(MatchOutcome.None, null, "none"));
    }
}

public sealed class ConsoleSink : IDocumentSink
{
    public Task FileAsync(IncomingDocument doc, DocumentUnit unit, ProcessingResult r, CancellationToken ct = default)
    {
        if (r.Disposition == Disposition.AutoFile)
            Console.WriteLine($"   FILE   {unit.UnitId} → patient '{r.Match.Patient?.LastName}' (#{r.Match.Patient?.ListItemId})  (conf {r.OverallConfidence:F2})");
        else
            Console.WriteLine($"   REVIEW {unit.UnitId} → 'Intake – Needs Review'  [{string.Join("; ", r.Reasons)}]");
        return Task.CompletedTask;
    }
}

public sealed class ConsoleLedger : ILedger
{
    public Task SetStateAsync(string id, ProcessingState state, CancellationToken ct = default)
    {
        Console.WriteLine($"   · {id} → {state}");
        return Task.CompletedTask;
    }
}
