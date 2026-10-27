using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Adelaide.Audit;
using Adelaide.Core;

namespace Adelaide.Web.Tests;

/// <summary>
/// Stands up a REAL Adelaide.Web instance — its own OS process, its own Kestrel, its own ephemeral port — against a
/// SYNTHETIC ledger, and tears it all down afterwards. It is the harness for the HTTP-surface audit.
///
/// WHY OUT-OF-PROCESS rather than WebApplicationFactory/TestServer. Three of the things this audit exists to measure
/// are not modelled by TestServer at all: Kestrel's MaxRequestBodySize (TestServer has no connection layer, so an
/// oversize body is simply accepted), HttpContext.Connection.RemoteIpAddress (null under TestServer, which is exactly
/// the rate limiter's fallback partition key), and the real 413/431/400 shapes the connection layer produces before a
/// handler ever runs. A test that "passed" against TestServer would be measuring a different program.
///
/// SAFETY — this is the whole point of the class, so it is enforced in code, not by convention:
///  • The port is claimed from the OS ephemeral range and asserted NOT to be 8123 (the default port of a real
///    instance, which may hold patient data) — see <see cref="ForbiddenPorts"/>. Nothing here ever addresses a host other than 127.0.0.1.
///  • The child process runs with ADELAIDE_TEST_MODE=1, which (a) takes a process-scoped mutex instead of the
///    production single-filer mutex Global\Adelaide.Web, so it can never contend with the live filer, and (b) replaces
///    every typed HttpClient's primary handler and the TokenCredential, so the instance CANNOT reach Graph,
///    SharePoint, Azure OpenAI, Document Intelligence or an Entra token endpoint. See Adelaide.Web.TestMode.
///  • The environment is built from an EXPLICIT allowlist (<see cref="BuildEnvironment"/>): the child inherits no
///    ambient ENTRA_*/ADELAIDE_* configuration from the developer's shell, so it cannot pick up real credentials.
///  • The ledger is SYNTHETIC and created fresh per run. The live ledger is never opened, copied or read — which is
///    also why no patient data can reach a test failure message.
/// </summary>
public sealed class HttpSurfaceFixture : IAsyncLifetime
{
    /// <summary>Ports this harness must never bind or address. 8123 is the default port of a real instance.</summary>
    public static readonly int[] ForbiddenPorts = { 8123, 80, 443 };

    private Process? _proc;
    private string _root = "";

    public HttpClient Client { get; private set; } = null!;
    public int Port { get; private set; }
    public string InboxPath => Path.Combine(_root, "inbox");
    public string LedgerPath => Path.Combine(_root, "ledger.db");
    /// <summary>Everything the child process wrote to stdout/stderr — used to assert on LOG content, and dumped
    /// when startup fails so a broken harness never looks like a failing assertion.</summary>
    public string ProcessLog => ReadAllTextShared(Path.Combine(_root, "stdout.log")) + ReadAllTextShared(Path.Combine(_root, "stderr.log"));

    // ---- synthetic corpus: one document per interesting state. ----
    // Ids are EXACTLY 12 lowercase hex characters, the real shape the ledger produces (Guid("N")[..12]). This is not
    // cosmetic: the routes apply TWO different validators — the loose IsValidDocId (any casing, any length) on the
    // legacy endpoints, and the strict IsLedgerDocId (^[0-9a-f]{12}(-u[0-9]+)?$) on /api/documents/{id}/history. An id
    // that satisfied only the loose one would 400 on the history route and every history finding would be an artefact
    // of the fixture rather than of the code.
    public const string Filed = "aaaa00000001";
    public const string Discarded = "aaaa00000002";
    public const string Superseded = "aaaa00000003";
    public const string Segmented = "aaaa00000004";
    public const string SeededHistory = "aaaa00000005";
    public const string DeadLetter = "aaaa00000006";
    public const string NeedsReview = "aaaa00000007";
    public const string Filing = "aaaa00000008";
    public const string DuplicateSuspect = "aaaa00000009";
    /// <summary>An id that is VALID by shape but has no ledger row — the "does not exist" probe.</summary>
    public const string Unknown = "aaaa0000000a";
    /// <summary>Carries deliberately-shaped Details for probing the /api/documents/{id}/history allowlist.</summary>
    public const string HistoryProbe = "aaaa0000000b";
    /// <summary>A DeadLetter document reserved for the concurrency test, so no other test mutates its state.</summary>
    public const string ConcurrentRetry = "aaaa0000000c";

    /// <summary>
    /// A NeedsReview document backed by a REAL, parseable multi-page PDF. Without one, /api/split's page-count parse
    /// (Program.cs:1331) rejects every request with 400 BEFORE the range-partition validation at :1335-1343 — so the
    /// whole of the split body validation is unreachable and any test of it would be vacuous.
    /// </summary>
    public const string Splittable = "aaaa0000000d";
    /// <summary>Page count of <see cref="Splittable"/>'s PDF.</summary>
    public const int SplittablePages = 4;

    /// <summary>A document that exists in the PROJECTION but has no events — the C9 dedup-seed shape.</summary>
    public const string SeededProjectionOnly = "aaaa0000000e";

    /// <summary>
    /// Inbox files only — deliberately NO ledger row until a test creates one with <see cref="AppendEventAsync"/>.
    /// <para>
    /// `Filing` is the one state that <c>AuditQueryPredicates.ReconcileWhere</c> selects, and UploadProcessor runs a
    /// FULL reconcile at startup — so a document seeded as Filing BEFORE the host starts is re-enqueued and
    /// dead-lettered before the first test runs. Any test that then asserted "not 409" would pass against a
    /// DeadLetter document and prove nothing about Filing. Creating the row at test time instead keeps the state
    /// true for the duration of the request under test.
    /// </para>
    /// </summary>
    public const string FilingLate = "aaaa0000000f";

    // ---- undiscard specimens (discard-recovery). Each is DEDICATED to HttpSurfaceUndiscardTests: a successful
    // undiscard mutates the shared ledger (that is the feature), so no other test may key off these ids. ----
    /// <summary>NeedsReview → Discarded, PDF + review sidecar on disk: the in-window happy path.</summary>
    public const string UndiscardReview = "aaaa00000010";
    /// <summary>DuplicateSuspect → Discarded, PDF only (a suspect never ran the pipeline, so no sidecar ever
    /// existed): restores on the PDF alone.</summary>
    public const string UndiscardSuspect = "aaaa00000011";
    /// <summary>Live NeedsReview carrying duplicateOf, PDF + sidecar: the discard→undiscard→discard→undiscard
    /// cycle drives it through the real endpoints twice.</summary>
    public const string UndiscardCycle = "aaaa00000012";
    /// <summary>NeedsReview → Discarded with NO inbox files: the artifact-missing refusal.</summary>
    public const string UndiscardNoPdf = "aaaa00000013";
    /// <summary>Received → Discarded → Purged: the sealed state — every mutation, undiscard included, 409s.</summary>
    public const string Purged = "aaaa00000014";
    /// <summary>NeedsReview → Discarded with PDF + sidecar, NEVER undiscarded by any test: the stable
    /// specimen for the list endpoint's `undiscardable` affordance flag (true here, false on UndiscardNoPdf).</summary>
    public const string UndiscardAffordance = "aaaa00000015";
    /// <summary>Live NeedsReview with a REAL multi-page PDF + review + saved-rotation sidecars, dedicated to
    /// HttpSurfaceSplitWindowTests: a real split runs against it (mutating — it becomes Segmented and mints
    /// children), pinning that the endpoint appends only and every parent artifact survives the transition.</summary>
    public const string SplitWindowParent = "aaaa00000016";

    /// <summary>Append an event to the running host's ledger from the test, so a state can be established AFTER the
    /// startup sweep has run. SQLite is in WAL mode, so a second connection may write while the host holds its own.</summary>
    public async Task AppendEventAsync(string id, string type, string? details = null)
    {
        var store = new SqliteAuditStore(LedgerPath, "test");
        await store.AppendAsync(new AuditEntry(id, type, "user:test", Details: details ?? "{}"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>A marker placed INSIDE a nested object under an ALLOWLISTED top-level key. Synthetic — it stands in
    /// for whatever a future payload might nest there; no real value is used anywhere in this harness.</summary>
    public const string NestedMarker = "SYNTHETIC-NESTED-MARKER";
    /// <summary>A marker placed as the value of a WITHHELD top-level key. Must never appear in a response.</summary>
    public const string WithheldMarker = "SYNTHETIC-WITHHELD-MARKER";

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "adelaide-httpaudit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(InboxPath);
        Directory.CreateDirectory(Path.Combine(_root, "logs"));

        await SeedLedgerAsync();
        SeedInbox();

        Port = ClaimFreePort();
        if (Array.IndexOf(ForbiddenPorts, Port) >= 0)
            throw new InvalidOperationException($"refusing to bind {Port}: reserved/production port");

        var webDll = LocateWebDll();
        var psi = new ProcessStartInfo(LocateDotnet(), $"\"{webDll}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _root, // keeps DotEnv's FindUp away from any repo .env
        };
        BuildEnvironment(psi);

        var outw = new StreamWriter(new FileStream(Path.Combine(_root, "stdout.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var errw = new StreamWriter(new FileStream(Path.Combine(_root, "stderr.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _proc = new Process { StartInfo = psi };
        _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (outw) outw.WriteLine(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errw) errw.WriteLine(e.Data); };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(30) };

        // Wait for liveness. Fail LOUDLY with the child's output — a harness that silently "skips" is the failure mode
        // this repo's own verification report called out (a self-skip that still reports Passed proves nothing).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_proc.HasExited)
                throw new InvalidOperationException($"Adelaide.Web exited during startup (code {_proc.ExitCode}).\n{ProcessLog}");
            try
            {
                var r = await Client.GetAsync("/health");
                if (r.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { /* not listening yet */ }
            await Task.Delay(250);
        }
        throw new InvalidOperationException($"Adelaide.Web did not become healthy on 127.0.0.1:{Port}.\n{ProcessLog}");
    }

    /// <summary>
    /// The child's environment, built from an EXPLICIT allowlist rather than inherited. Anything not set here is
    /// absent in the child, which is what keeps a developer's real ENTRA_CLIENT_SECRET / SQL connection string /
    /// CLAMAV host out of the harness. The two secrets that must be non-empty for the composition root to build are
    /// obvious fakes.
    /// </summary>
    private void BuildEnvironment(ProcessStartInfo psi)
    {
        // Start from nothing but the OS essentials the runtime needs.
        var keep = new[] { "PATH", "SystemRoot", "windir", "TEMP", "TMP", "USERPROFILE", "ProgramFiles", "ProgramFiles(x86)", "ProgramData", "COMSPEC", "NUMBER_OF_PROCESSORS", "OS", "PATHEXT", "SystemDrive", "LOCALAPPDATA", "APPDATA", "HOME" };
        var inherited = psi.Environment.Where(kv => Array.IndexOf(keep, kv.Key) < 0).Select(kv => kv.Key).ToList();
        foreach (var k in inherited) psi.Environment.Remove(k);

        psi.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(LocateDotnet())!;
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{Port}";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production"; // NOT Development: keeps the dev-actor identity path off
        psi.Environment["DOTNET_NOLOGO"] = "1";

        psi.Environment["ADELAIDE_TEST_MODE"] = "1";               // mutex bypass + no outbound, together
        psi.Environment["ADELAIDE_AUDIT_DB"] = LedgerPath;
        psi.Environment["ADELAIDE_TRANSCRIPT_DB"] = Path.Combine(_root, "capture.db");
        psi.Environment["ADELAIDE_INBOX"] = InboxPath;
        psi.Environment["ADELAIDE_LOG_DIR"] = Path.Combine(_root, "logs");
        psi.Environment["ENTRA_TENANT_ID"] = "00000000-0000-0000-0000-000000000000";   // synthetic — never dialled
        psi.Environment["ENTRA_CLIENT_ID"] = "00000000-0000-0000-0000-000000000000";
        psi.Environment["SP_SITE_ID"] = "contoso.sharepoint.invalid,synthetic-site,synthetic-web";
        psi.Environment["ENTRA_CLIENT_SECRET"] = "synthetic-not-a-real-secret";
        psi.Environment["ADELAIDE_OPENAI_ENDPOINT"] = "https://openai.invalid";
        // Deliberately UNSET: ADELAIDE_DRAIN_AUTH_*, ADELAIDE_CLAMAV_HOST, ADELAIDE_FILED_PURGE_*, ADELAIDE_SQL_*,
        // ADELAIDE_DEV_ACTOR, ADELAIDE_KEYVAULT_URI. Each absence is a documented part of the audited configuration.
    }

    /// <summary>Append one event per synthetic document. State is the event Type (SqliteAuditStore.UpsertProjection
    /// sets State=@state from entry.Type), so this produces a document in each state with a valid hash chain.</summary>
    private async Task SeedLedgerAsync()
    {
        var store = new SqliteAuditStore(LedgerPath, "seed");
        async Task Add(string id, string type, string? details = null) =>
            await store.AppendAsync(new AuditEntry(id, type, "user:seed", Details: details ?? "{}"));

        await Add(NeedsReview, "NeedsReview", """{"reasonCodes":["synthetic"],"confidence":0.5}""");
        await Add(Filing, "Filing");
        await Add(Filed, "Filed", """{"driveItemId":"SYNTHETIC-DRIVEITEM-1","sha256":"0000000000000000000000000000000000000000000000000000000000000001"}""");
        await Add(Discarded, "Discarded", $$"""{"duplicateOf":"{{Filed}}"}""");
        await Add(Superseded, "Superseded", $$"""{"supersededBy":"{{Filed}}","reason":"vom Nutzer ersetzt"}""");
        await Add(Segmented, "Segmented", """{"units":2,"unitCount":2}""");
        await Add(SeededHistory, "SeededHistory", """{"seed":true}""");
        await Add(DeadLetter, "DeadLetter", """{"reasonCodes":["synthetic_failure"]}""");
        await Add(ConcurrentRetry, "DeadLetter", """{"reasonCodes":["synthetic_failure"]}""");
        await Add(Splittable, "NeedsReview", """{"reasonCodes":["synthetic"],"pages":4}""");
        await Add(DuplicateSuspect, "DuplicateSuspect", $$"""{"duplicateOf":"{{Filed}}"}""");

        // Undiscard specimens: real pre-discard streams (the guard derives the restore target by scanning back
        // from the LATEST Discarded event, so a lone Discarded first event — the `Discarded` specimen above —
        // is the no-restorable-predecessor case, covered separately).
        await Add(UndiscardReview, "NeedsReview", $$"""{"duplicateOf":"{{Filed}}","reasonCodes":["synthetic"]}""");
        await Add(UndiscardReview, "Discarded", $$"""{"duplicateOf":"{{Filed}}"}""");
        await Add(UndiscardSuspect, "DuplicateSuspect", $$"""{"duplicateOf":"{{Filed}}"}""");
        await Add(UndiscardSuspect, "Discarded", $$"""{"duplicateOf":"{{Filed}}"}""");
        await Add(UndiscardCycle, "NeedsReview", $$"""{"duplicateOf":"{{Filed}}","reasonCodes":["synthetic"]}""");
        await Add(UndiscardNoPdf, "NeedsReview", """{"reasonCodes":["synthetic"]}""");
        await Add(UndiscardNoPdf, "Discarded", "{}");
        await Add(Purged, "Received", """{"channel":"Upload"}""");
        await Add(Purged, "Discarded", "{}");
        await Add(Purged, "Purged", """{"seal":true}""");
        await Add(UndiscardAffordance, "NeedsReview", $$"""{"duplicateOf":"{{Filed}}","reasonCodes":["synthetic"]}""");
        await Add(UndiscardAffordance, "Discarded", $$"""{"duplicateOf":"{{Filed}}"}""");
        await Add(SplitWindowParent, "NeedsReview", """{"reasonCodes":["synthetic"],"pages":4}""");

        // Probe payloads for the history allowlist, all on ONE document:
        //  1. a WITHHELD top-level key (fileName) whose value must never be returned;
        //  2. an ALLOWLISTED top-level key (units) holding a NESTED object with an unlisted sub-key — the allowlist is
        //     applied only at depth 1, so this tests whether the subtree is emitted unfiltered AND unreported;
        //  3. a payload that is not a JSON object at all, and one that is malformed.
        await Add(HistoryProbe, "Received",
            $$"""{"fileName":"{{WithheldMarker}}","sha256":"00","units":[{"unitId":"u1","unlistedSubKey":"{{NestedMarker}}"}]}""");
        await Add(HistoryProbe, "NeedsReview", $$"""{"error":"{{WithheldMarker}}","reasonCodes":["synthetic"]}""");
        await Add(HistoryProbe, "Filed", """[1,2,3]""");        // non-object Details
        await Add(HistoryProbe, "Rotated", "{not valid json");  // malformed Details

        SeedProjectionOnlyRow();
    }

    /// <summary>
    /// Insert a PROJECTION row with NO events — the exact shape the C9 dedup seed produces. Migrator writes the
    /// `documents` projection directly and deliberately never appends an event (that is the "projection-only, never
    /// writes an event" property C9 claims), so a seeded historical document has a ledger row and an empty stream.
    /// Written with raw SQL because the IAuditStore seam has no projection-only write — which is itself the point.
    /// </summary>
    private void SeedProjectionOnlyRow()
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = LedgerPath }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO documents (DocumentId, State, FirstSeen, LastUpdated, EventCount)
            VALUES ($id, 'SeededHistory', $at, $at, 0);
            """;
        cmd.Parameters.AddWithValue("$id", SeededProjectionOnly);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Put an arrival PDF in the inbox for the documents whose HTTP behavior depends on one existing.
    /// <para>
    /// This is not decoration: several routes resolve "is this document still actionable?" from the PRESENCE OF THE
    /// INBOX FILE rather than from ledger state. Under the deployment's purge-OFF posture a FILED document keeps its
    /// arrival PDF indefinitely, so seeding a PDF beside a Filed ledger row reproduces the live configuration exactly.
    /// </para>
    /// Synthetic bytes only — a PDF header and nothing else. No test here needs it to render.
    /// </summary>
    private void SeedInbox()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic audit fixture - not a real document\n");
        foreach (var id in InboxSeeded)
        {
            // The splittable documents get a REAL PDF (blank pages, no content) so split range validation — and,
            // for SplitWindowParent, a REAL committed split — is reachable; every other fixture document gets an
            // unparseable stub, which is enough for routes that only test File.Exists and keeps them from ever
            // rendering or splitting for real.
            File.WriteAllBytes(Path.Combine(InboxPath, id + ".pdf"), id is Splittable or SplitWindowParent ? MakeRealPdf(SplittablePages) : bytes);
            // A pending-review sidecar. /api/review and /api/split BOTH return 404 before they ever consult document
            // state unless this file exists, so without it the terminal-state guards are unreachable and a test that
            // "passed" would have proven only that the sidecar was missing. "{}" is enough: the guards run before the
            // sidecar is parsed.
            File.WriteAllText(Path.Combine(InboxPath, id + ".review.json"), "{}");
        }
        // The DuplicateSuspect-provenance undiscard specimen gets the PDF ONLY: a suspect never ran the
        // pipeline, so a review sidecar never existed — the guard must restore it without one.
        File.WriteAllBytes(Path.Combine(InboxPath, UndiscardSuspect + ".pdf"), bytes);
        // The split-window parent additionally carries a saved orientation: the split-survival pin asserts all
        // THREE parent artifacts (pdf, review sidecar, rotation) come through the transition untouched.
        File.WriteAllText(Path.Combine(InboxPath, SplitWindowParent + ".rotation.json"), """{"pages":{"1":90}}""");
    }

    /// <summary>Documents that have BOTH an arrival PDF and a pending-review sidecar on disk — i.e. the purge-OFF
    /// posture the deployment actually runs, in which a settled document's inputs survive its terminal event.</summary>
    public static readonly string[] InboxSeeded = { Filed, NeedsReview, Segmented, Superseded, DeadLetter, ConcurrentRetry, Filing, Splittable, FilingLate, UndiscardReview, UndiscardCycle, UndiscardAffordance, SplitWindowParent };

    /// <summary>A real, parseable PDF of <paramref name="pages"/> blank pages — synthetic by construction: it has no
    /// content streams at all, so it can carry no patient data.</summary>
    private static byte[] MakeRealPdf(int pages)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ---- identity helpers: forge the platform-injected Easy Auth principal header ----

    public const string OidReviewer = "00000000-0000-0000-0000-00000000re01";
    public const string OidAdmin = "00000000-0000-0000-0000-0000000adm01";

    /// <summary>A Reviewer-only principal. Deliberately NOT an overload of <see cref="Principal"/>: a
    /// <c>Principal(params string[] roles)</c> sibling silently binds <c>Principal("Reviewer")</c> to the
    /// (oid, roles) overload with ZERO roles, which yields a 403 that looks exactly like a real finding.</summary>
    public static string ReviewerPrincipal => Principal(OidReviewer, "Reviewer");

    /// <summary>An Admin-only principal — Admin WITHOUT Reviewer, which is itself under test.</summary>
    public static string AdminPrincipal => Principal(OidAdmin, "Admin");

    /// <summary>A principal carrying ROLE claims and NO oid — authorized by the policies, but unattributable by
    /// <c>EasyAuthDefaults.Actor</c>. The gap between those two is finding A1.</summary>
    public static string PrincipalRolesOnly(params string[] roles)
    {
        var claims = roles.Select(r => new { typ = "roles", val = r }).Cast<object>().ToList();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { claims })));
    }

    /// <summary>A second, textually DIFFERENT oid-less principal — same roles, plus a name claim. Distinct as an
    /// identity in every respect except the one the rate limiter partitions on.</summary>
    public static string PrincipalRolesOnlyWithName(string name, params string[] roles)
    {
        var claims = roles.Select(r => (object)new { typ = "roles", val = r }).ToList();
        claims.Add(new { typ = "name", val = name });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { claims })));
    }

    /// <summary>Base64 of the Easy Auth principal JSON. This is what App Service injects after IT has validated the
    /// user; in-process there is no signature, so this is exactly the trust model under test.</summary>
    public static string Principal(string oid, params string[] roles)
    {
        var claims = new List<object> { new { typ = "http://schemas.microsoft.com/identity/claims/objectidentifier", val = oid } };
        foreach (var r in roles) claims.Add(new { typ = "roles", val = r });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { claims })));
    }

    public HttpRequestMessage Req(HttpMethod m, string url, string? principal, bool csrf = false, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body };
        if (principal is not null) r.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL", principal);
        if (csrf) r.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        return r;
    }

    public Task<HttpResponseMessage> AsReviewer(HttpMethod m, string url, bool csrf = false, HttpContent? body = null)
        => Client.SendAsync(Req(m, url, ReviewerPrincipal, csrf, body));

    public Task<HttpResponseMessage> AsAdmin(HttpMethod m, string url, bool csrf = false, HttpContent? body = null)
        => Client.SendAsync(Req(m, url, AdminPrincipal, csrf, body));

    public Task<HttpResponseMessage> Anonymous(HttpMethod m, string url, HttpContent? body = null)
        => Client.SendAsync(Req(m, url, null, false, body));

    public static StringContent Json(string raw) => new(raw, Encoding.UTF8, "application/json");

    // ---- infrastructure ----

    private static int ClaimFreePort()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            if (Array.IndexOf(ForbiddenPorts, p) < 0) return p;
        }
        throw new InvalidOperationException("could not claim a free non-reserved port");
    }

    // Locating the child host is SHARED with FaultHost (see TestHostPaths). Both fixtures carried private copies
    // that hardcoded "dotnet.exe" and the Windows-only artifacts layout; both broke identically the first time CI
    // ran them on Linux. One copy now — a platform assumption duplicated is one that gets half-fixed.
    private static string LocateDotnet() => TestHostPaths.Dotnet();
    private static string LocateWebDll() => TestHostPaths.WebDll();

    private static string ReadAllTextShared(string p)
    {
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(entireProcessTree: true); } catch { }
            try { await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        }
        _proc?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // release the seeding connection's file handles
        for (var i = 0; i < 5; i++) // WAL sidecars can linger a moment after the child exits
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); break; }
            catch (IOException) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) { await Task.Delay(200); }
        }
    }
}

[CollectionDefinition("http-surface")]
public sealed class HttpSurfaceCollection : ICollectionFixture<HttpSurfaceFixture>;
