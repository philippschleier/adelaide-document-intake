using Adelaide.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Adelaide.Worker;

/// <summary>
/// Privacy-safe live check of the Graph patient directory (resolved from DI, with a resilient HttpClient):
/// queries for deliberately non-existent records, so it proves auth + the indexed filters run against the
/// real list WITHOUT displaying any patient data.
/// </summary>
internal static class SmokeTests
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var dir = services.GetRequiredService<IPatientDirectory>();
        Console.WriteLine("app-only Graph patient-directory smoke test (no patient data shown)\n");

        var r1 = await dir.MatchAsync(new ExtractedData(new[] { new ExtractedField("KVNR", "Z000000005", 1.0, "test") }));
        Console.WriteLine($"  fake KVNR Z000000005  -> {r1.Outcome} via {r1.Method}   (expect None)");

        var r2 = await dir.MatchAsync(new ExtractedData(new[]
        {
            new ExtractedField("LastName", "Zzztestpatient", 1.0, "test"),
            new ExtractedField("BirthDate", "1900-01-01", 1.0, "test"),
        }));
        Console.WriteLine($"  fake name+DOB         -> {r2.Outcome} via {r2.Method}   (expect None)");

        Console.WriteLine("\n  Live queries executed via DI + resilient HttpClient.");
    }
}
