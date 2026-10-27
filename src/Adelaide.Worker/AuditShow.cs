using Adelaide.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Adelaide.Worker;

/// <summary>
/// Prints a document's audit projection + full event stream (with Details) and verifies the chain.
/// The argument may be a documentId, or any substring found in an event's Details (e.g. an original filename).
/// </summary>
public static class AuditShow
{
    public static async Task RunAsync(IServiceProvider sp, string query)
    {
        var store = sp.GetRequiredService<SqliteAuditStore>();
        if (string.IsNullOrWhiteSpace(query)) { Console.Error.WriteLine("usage: audit-show <documentId | filename-substring>"); return; }

        var docId = query;
        var doc = await store.GetDocumentAsync(docId);
        if (doc is null)
        {
            // C6: names live in the PROJECTION now, so it is the authoritative (and unbounded) name lookup — the
            // events-Details search is only the fallback for pre-C6 history, whose payloads still carried names.
            // Projection first also picks the NEWEST match when a reused scanner path exists in both eras
            // (verification finding: Details-first resolved the stale pre-C6 doc and hid the current one).
            var resolved = await store.FindDocumentIdByNameAsync(query)
                        ?? await store.FindDocumentIdByDetailAsync(query)
                        // Last resort: SQLite LIKE folds ASCII case only, so a query differing in umlaut case
                        // ('MÜLLER' vs 'Müller') misses both lookups. Unicode-folding scan, bounded to the 5000
                        // most recent docs — acceptable for a CLI convenience, and only reached on a double miss.
                        ?? (await store.ListDocumentsAsync())
                            .FirstOrDefault(d => (d.OriginalName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                                              || (d.FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                                              || (d.SourceRecordId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))?.DocumentId;
            if (resolved is not null) { docId = resolved; doc = await store.GetDocumentAsync(docId); }
        }
        if (doc is null) { Console.WriteLine($"No audit record matching '{query}'."); return; }

        Console.WriteLine($"Document {doc.DocumentId}");
        Console.WriteLine($"  state={doc.State}  type={doc.ContentType}  patient#={doc.PatientListItemId}  disposition={doc.Disposition}  events={doc.EventCount}");
        Console.WriteLine($"  first={doc.FirstSeen:u}  last={doc.LastUpdated:u}\n");

        Console.WriteLine("Event stream:");
        foreach (var e in await store.GetEventsAsync(docId))
            Console.WriteLine($"  #{e.Seq,-4} {e.At:u} {e.Type,-12} {e.Actor,-11} {e.Details}");

        Console.WriteLine($"\nChain verify: {((await store.VerifyChainAsync()).Intact ? "OK — tamper-evident" : "FAILED")}");
    }
}
