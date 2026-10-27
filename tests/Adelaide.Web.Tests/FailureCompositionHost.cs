using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Adelaide.Audit;
using Adelaide.Core;

namespace Adelaide.Web.Tests;

/// <summary>
/// A per-test Adelaide.Web host for the FAILURE-COMPOSITION investigation, started with fault seams enabled.
///
/// WHY NOT <see cref="HttpSurfaceFixture"/>. That one is a COLLECTION fixture: a single child process shared by every
/// test class in the collection. The fault seams here (<c>ADELAIDE_TEST_FILER</c>, <c>ADELAIDE_TEST_APPEND_FAIL</c>)
/// are read from the CHILD PROCESS environment at composition time, so they cannot be varied per test on a shared
/// host. Each test that needs a fault therefore starts its own host and disposes it — slower, and correct.
///
/// SAFETY, inherited verbatim from the HTTP-surface harness and re-asserted here rather than assumed:
///  • An ephemeral loopback port, asserted not to be one of <see cref="HttpSurfaceFixture.ForbiddenPorts"/> — 8123 is
///    the default port of a real instance, which may hold patient data. Nothing here addresses a host but 127.0.0.1.
///  • ADELAIDE_TEST_MODE=1, so the child takes a PROCESS-SCOPED mutex and can never contend for the production
///    single-filer mutex, and every outbound seam (Graph/SharePoint/OpenAI/Document Intelligence/Entra) is blocked.
///  • An EXPLICIT environment allowlist, so no real credential from the developer's shell can reach the child.
///  • A SYNTHETIC ledger created fresh per host. The live ledger is never opened, copied or read, so no patient data
///    can reach a failure message.
///
/// The fault seams do NOT add outbound capability: <c>ADELAIDE_TEST_FILER=succeed</c> swaps a locally-succeeding stub
/// in at the filer seam. It returns a synthetic locator and opens no socket; the blocked handler stays underneath.
/// </summary>
public sealed class FaultHost : IAsyncDisposable
{
    private Process? _proc;

    public string Root { get; private set; } = "";
    public HttpClient Client { get; private set; } = null!;
    public int Port { get; private set; }
    public string InboxPath => Path.Combine(Root, "inbox");
    public string LedgerPath => Path.Combine(Root, "ledger.db");
    public string ProcessLog => ReadShared(Path.Combine(Root, "stdout.log")) + ReadShared(Path.Combine(Root, "stderr.log"));

    /// <summary>The synthetic locator <c>TestFaults.SucceedingFiler</c> returns. Duplicated as a literal rather than
    /// referenced, so a test failure message never depends on the harness type being visible.</summary>
    public const string SyntheticDriveItemId = "SYNTHETIC-FILED-DRIVEITEM";

    /// <summary>
    /// Start a host. <paramref name="faults"/> holds the fault variables (e.g. ADELAIDE_TEST_APPEND_FAIL=Rotated);
    /// <paramref name="seed"/> runs after the ledger and inbox exist but BEFORE the process starts, so a state is
    /// true from the host's first tick.
    /// </summary>
    public static async Task<FaultHost> StartAsync(Dictionary<string, string> faults, Func<FaultHost, Task>? seed = null)
    {
        var h = new FaultHost { Root = Path.Combine(Path.GetTempPath(), "adelaide-failcomp", Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(h.InboxPath);
        Directory.CreateDirectory(Path.Combine(h.Root, "logs"));

        if (seed is not null) await seed(h);

        h.Port = ClaimFreePort();
        if (Array.IndexOf(HttpSurfaceFixture.ForbiddenPorts, h.Port) >= 0)
            throw new InvalidOperationException($"refusing to bind {h.Port}: reserved/production port");

        var psi = new ProcessStartInfo(LocateDotnet(), $"\"{LocateWebDll()}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = h.Root, // keeps DotEnv's FindUp away from any repo .env
        };

        var keep = new[] { "PATH", "SystemRoot", "windir", "TEMP", "TMP", "USERPROFILE", "ProgramFiles", "ProgramFiles(x86)", "ProgramData", "COMSPEC", "NUMBER_OF_PROCESSORS", "OS", "PATHEXT", "SystemDrive", "LOCALAPPDATA", "APPDATA", "HOME" };
        foreach (var k in psi.Environment.Where(kv => Array.IndexOf(keep, kv.Key) < 0).Select(kv => kv.Key).ToList())
            psi.Environment.Remove(k);

        psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(LocateDotnet())!;
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{h.Port}";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ADELAIDE_TEST_MODE"] = "1";
        psi.Environment["ADELAIDE_AUDIT_DB"] = h.LedgerPath;
        psi.Environment["ADELAIDE_TRANSCRIPT_DB"] = Path.Combine(h.Root, "capture.db");
        psi.Environment["ADELAIDE_INBOX"] = h.InboxPath;
        psi.Environment["ADELAIDE_LOG_DIR"] = Path.Combine(h.Root, "logs");
        psi.Environment["ENTRA_TENANT_ID"] = "00000000-0000-0000-0000-000000000000";   // synthetic — never dialled
        psi.Environment["ENTRA_CLIENT_ID"] = "00000000-0000-0000-0000-000000000000";
        psi.Environment["SP_SITE_ID"] = "contoso.sharepoint.invalid,synthetic-site,synthetic-web";
        psi.Environment["ENTRA_CLIENT_SECRET"] = "synthetic-not-a-real-secret";
        psi.Environment["ADELAIDE_OPENAI_ENDPOINT"] = "https://openai.invalid";
        // Long reconcile interval by default: these tests assert on what a REQUEST left behind, and a periodic sweep
        // firing mid-assertion would repair (or further mutate) the state under test. A test that wants the sweep sets
        // ADELAIDE_RECONCILE_MINUTES itself through `faults`.
        psi.Environment["ADELAIDE_RECONCILE_MINUTES"] = "600";
        foreach (var (k, v) in faults) psi.Environment[k] = v;

        var outw = new StreamWriter(new FileStream(Path.Combine(h.Root, "stdout.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var errw = new StreamWriter(new FileStream(Path.Combine(h.Root, "stderr.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        h._proc = new Process { StartInfo = psi };
        h._proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (outw) outw.WriteLine(e.Data); };
        h._proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errw) errw.WriteLine(e.Data); };
        h._proc.Start();
        h._proc.BeginOutputReadLine();
        h._proc.BeginErrorReadLine();

        h.Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{h.Port}"), Timeout = TimeSpan.FromSeconds(30) };

        // Fail LOUDLY with the child's output. A harness that silently skips proves nothing (PHASE1-VERIFICATION §5.14).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (h._proc.HasExited)
                throw new InvalidOperationException($"Adelaide.Web exited during startup (code {h._proc.ExitCode}).\n{h.ProcessLog}");
            try { if ((await h.Client.GetAsync("/health")).IsSuccessStatusCode) return h; }
            catch (HttpRequestException) { /* not listening yet */ }
            await Task.Delay(250);
        }
        throw new InvalidOperationException($"Adelaide.Web did not become healthy on 127.0.0.1:{h.Port}.\n{h.ProcessLog}");
    }

    // ---- seeding + inspection ----

    /// <summary>Append an event to this host's synthetic ledger (used both to seed and to assert).</summary>
    public async Task AppendAsync(string id, string type, string? details = null, string actor = "user:test")
    {
        var store = new SqliteAuditStore(LedgerPath, "test");
        await store.AppendAsync(new AuditEntry(id, type, actor, Details: details ?? "{}"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>The document's full event stream, read back from the ledger the host just wrote.</summary>
    public async Task<IReadOnlyList<AuditEvent>> EventsAsync(string id)
    {
        var store = new SqliteAuditStore(LedgerPath, "test");
        var e = await store.GetEventsAsync(id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return e;
    }

    /// <summary>The projection row, or null.</summary>
    public async Task<DocumentAudit?> DocAsync(string id)
    {
        var store = new SqliteAuditStore(LedgerPath, "test");
        var d = await store.GetDocumentAsync(id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return d;
    }

    /// <summary>Put an arrival PDF + a pending-review sidecar in the inbox. Both routes under test 404 before they
    /// consult document state unless the sidecar exists, so without it every state guard is unreachable.</summary>
    public void SeedInbox(string id, int pages = 4)
    {
        File.WriteAllBytes(Path.Combine(InboxPath, id + ".pdf"), MakeRealPdf(pages));
        File.WriteAllText(Path.Combine(InboxPath, id + ".review.json"), "{}");
    }

    public byte[] InboxPdf(string id) => File.ReadAllBytes(Path.Combine(InboxPath, id + ".pdf"));
    public bool InboxHas(string suffixName) => File.Exists(Path.Combine(InboxPath, suffixName));

    /// <summary>A real, parseable PDF of blank pages — no content streams at all, so it can carry no patient data.</summary>
    public static byte[] MakeRealPdf(int pages)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ---- request helpers ----

    public Task<HttpResponseMessage> AsReviewer(HttpMethod m, string url, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body };
        r.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL", HttpSurfaceFixture.ReviewerPrincipal);
        r.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        return Client.SendAsync(r);
    }

    public static StringContent Json(string raw) => new(raw, Encoding.UTF8, "application/json");

    // ---- infrastructure (port claiming is re-implemented — HttpSurfaceFixture's copy is private; the host-path
    // lookups are deliberately SHARED, see TestHostPaths) ----

    private static int ClaimFreePort()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            if (Array.IndexOf(HttpSurfaceFixture.ForbiddenPorts, p) < 0) return p;
        }
        throw new InvalidOperationException("could not claim a free non-reserved port");
    }

    // Locating the child host is SHARED with HttpSurfaceFixture (see TestHostPaths). It used to be duplicated here
    // because the two harnesses were staged separately and each kept its copies private — and that duplication is
    // precisely what this pair of methods proved dangerous: both copies hardcoded "dotnet.exe" and the Windows-only
    // artifacts layout, and both broke the same way the first time CI ran them on Linux. One copy now.
    private static string LocateDotnet() => TestHostPaths.Dotnet();
    private static string LocateWebDll() => TestHostPaths.WebDll();

    private static string ReadShared(string p)
    {
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(entireProcessTree: true); } catch { }
            try { await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        }
        _proc?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); break; }
            catch (IOException) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) { await Task.Delay(200); }
        }
    }
}
