using Adelaide.Core;
using Adelaide.Demo;
using Adelaide.SharePoint;

namespace Adelaide.Web;

/// <summary>
/// DEMO MODE (ADELAIDE_DEMO_MODE=1) — the public repository's runnable configuration. NOT a production code path: every
/// member is unreachable unless the variable is exactly "1", and no production branch changes when it is unset.
///
/// It serves the REAL application — the same HTTP surface, Angular review UI, upload processor, verify-or-review gate,
/// duplicate layers, hash-chained SQLite ledger, outbox filing and reconcile sweep — with the four external seams
/// rebound to local, deterministic adapters (Adelaide.Demo):
///   ITranscriber / IExtractor  → replay pre-recorded results for the synthetic sample PDFs (no OCR, no LLM)
///   IPatientDirectory / IPatientSearch → the synthetic demo/patients.json
///   IFiler / IFiledDocumentStore / IDocumentSink → a local folder tree instead of a SharePoint library
///   IPdfTextLayer → unavailable (files the original PDF, the documented DI-fallback path)
///
/// Two guarantees make "a demo can never touch production" true by construction rather than by convention:
///   (1) <see cref="RefuseProductionSettings"/> — startup FAILS if any production connection setting is present in
///       the environment (SQL ledger, Key Vault, App Insights, the staging share, Entra secrets, AI endpoints).
///   (2) the same outbound-network block as <see cref="TestMode"/>: every typed HttpClient loses its socket and the
///       TokenCredential cannot mint a real token. A missed seam fails loudly instead of calling out.
/// </summary>
internal static class DemoMode
{
    internal const string EnvVar = "ADELAIDE_DEMO_MODE";

    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    /// <summary>Settings that only a real deployment has. Their presence in a demo process means the operator is
    /// pointing a demo at real infrastructure — refuse, by name, before anything is built.</summary>
    internal static readonly string[] ProductionSettings =
    {
        "ADELAIDE_SQL_CONNECTION_STRING", "ADELAIDE_SQL_SERVER", "ADELAIDE_SQL_DATABASE",
        "ADELAIDE_KEYVAULT_URI", "APPLICATIONINSIGHTS_CONNECTION_STRING",
        "ADELAIDE_DRAIN_SHARE_URL", "ADELAIDE_DRAIN_AUTH_TENANT", "ADELAIDE_DRAIN_AUTH_AUDIENCE",
        "ADELAIDE_EASYAUTH_VALIDATE", "ADELAIDE_EASYAUTH_TENANT_ID", "ADELAIDE_EASYAUTH_CLIENT_ID",
        "ENTRA_TENANT_ID", "ENTRA_CLIENT_ID", "ENTRA_CLIENT_SECRET", "SP_SITE_ID", "SP_BUSINESS_SITE_ID",
        "ADELAIDE_OPENAI_ENDPOINT", "ADELAIDE_DI_ENDPOINT",
    };

    internal static IReadOnlyList<string> PresentProductionSettings(Func<string, string?> env) =>
        ProductionSettings.Where(k => !string.IsNullOrWhiteSpace(env(k))).ToList();

    internal static void RefuseProductionSettings()
    {
        var present = PresentProductionSettings(Environment.GetEnvironmentVariable);
        if (present.Count > 0)
            throw new InvalidOperationException(
                $"{EnvVar}=1 but production settings are present: {string.Join(", ", present)}. " +
                "Demo mode runs only on synthetic data and never connects to real infrastructure — unset them (or drop the demo flag).");
    }

    /// <summary>The synthetic corpus directory (fixtures.json, patients.json, samples/): ADELAIDE_DEMO_DATA, else the
    /// nearest <c>demo/</c> folder walking up from the working directory.</summary>
    internal static string DataDir()
    {
        if (Environment.GetEnvironmentVariable("ADELAIDE_DEMO_DATA") is { Length: > 0 } explicitDir) return Path.GetFullPath(explicitDir);
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "demo");
            if (File.Exists(Path.Combine(candidate, "fixtures.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("Demo corpus not found: run from the repository (it has demo/fixtures.json) or set ADELAIDE_DEMO_DATA.");
    }

    /// <summary>Where the local filer writes "filed" documents: ADELAIDE_DEMO_FILED, else .adelaide-demo/filed.</summary>
    internal static string FiledDir() =>
        Path.GetFullPath(Environment.GetEnvironmentVariable("ADELAIDE_DEMO_FILED") is { Length: > 0 } d ? d : Path.Combine(".adelaide-demo", "filed"));

    /// <summary>Rebind the external seams. Called from the composition root ONLY when <see cref="Enabled"/>, after every
    /// production registration (last registration wins).</summary>
    internal static void Apply(WebApplicationBuilder builder)
    {
        TestMode.BlockOutboundNetwork(builder);

        var corpus = DemoCorpus.Load(DataDir());
        builder.Services.AddSingleton(corpus);
        builder.Services.AddTransient<ITranscriber, DemoTranscriber>();
        builder.Services.AddTransient<IExtractor, DemoExtractor>();
        builder.Services.AddTransient<IPdfTextLayer, NoTextLayer>();

        var directory = new DemoPatientDirectory(corpus.Patients);
        builder.Services.AddSingleton<IPatientDirectory>(directory);
        builder.Services.AddSingleton<IPatientSearch>(directory);

        var filer = new LocalFolderFiler(FiledDir());
        builder.Services.AddSingleton(filer);
        builder.Services.AddSingleton<IFiledDocumentStore>(filer);
        builder.Services.AddSingleton<IFiler>(filer);
        builder.Services.AddSingleton<IDocumentSink>(filer);
    }
}
