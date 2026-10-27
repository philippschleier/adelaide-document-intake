using Adelaide.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Adelaide.Worker;

/// <summary>
/// Records a synthetic document lifecycle into the real audit log, then prints the one-row projection,
/// the event stream, and the chain-verify result. All values are synthetic (no PHI).
/// </summary>
public static class AuditDemo
{
    public static async Task RunAsync(IServiceProvider sp)
    {
        var audit = sp.GetRequiredService<IAuditLog>();
        var id = $"demo-{Guid.NewGuid():N}"[..13];
        Console.WriteLine($"Recording a synthetic document lifecycle for {id}…\n");

        await audit.AppendAsync(new AuditEntry(id, "Received", "intake", Details: "{\"channel\":\"Fax\",\"source\":\"gntel\"}"));
        await audit.AppendAsync(new AuditEntry(id, "Transcribed", "vision", Details: "{\"pages\":1,\"legibility\":1.0}"));
        await audit.AppendAsync(new AuditEntry(id, "Extracted", "gpt-4o", Details: "{\"contentType\":\"Verordnung\"}"));
        await audit.AppendAsync(new AuditEntry(id, "Matched", "graph", Details: "{\"outcome\":\"Single\",\"patientListItemId\":999999}"));
        await audit.AppendAsync(new AuditEntry(id, "Filed", "sharepoint",
            Details: "{\"fileName\":\"2026-05-28_Verordnung_Z000000005_3f9a1c2e.pdf\",\"contentType\":\"Verordnung\",\"patientListItemId\":999999,\"disposition\":\"AutoFile\"}"));

        var doc = await audit.GetDocumentAsync(id);
        Console.WriteLine("One-row projection:");
        Console.WriteLine($"  {doc!.DocumentId}  state={doc.State}  type={doc.ContentType}  patient#={doc.PatientListItemId}  disposition={doc.Disposition}");
        Console.WriteLine($"  file = {doc.FileName}");
        Console.WriteLine($"  events={doc.EventCount}  first={doc.FirstSeen:u}  last={doc.LastUpdated:u}\n");

        Console.WriteLine("Event stream (append-only, hash-chained):");
        foreach (var e in await audit.GetEventsAsync(id))
            Console.WriteLine($"  #{e.Seq,-4} {e.Type,-12} {e.Actor,-11} {e.At:u}  hash={e.Hash[..12]}…");

        Console.WriteLine($"\nChain verify: {((await audit.VerifyChainAsync()).Intact ? "OK — tamper-evident" : "FAILED")}");
    }
}
