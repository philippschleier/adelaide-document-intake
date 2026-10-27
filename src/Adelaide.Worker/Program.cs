using Adelaide.Core;
using Adelaide.SharePoint;
using Adelaide.Worker;
using Adelaide.Ai;
using Adelaide.Audit;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

var command = args.Length > 0 ? args[0] : "demo";

// Offline demo needs no cloud/secret.
if (command == "demo")
{
    await DemoRunner.RunAsync();
    return;
}

if (command == "audit-types")
{
    // Diagnostic: event-type histogram + recent 'Rotated' events straight from the audit DB (ADELAIDE_AUDIT_DB or arg 1).
    var adb = args.Length > 1 ? args[1] : (Environment.GetEnvironmentVariable("ADELAIDE_AUDIT_DB") ?? "adelaide-audit.db");
    if (!File.Exists(adb)) { Console.Error.WriteLine($"audit db not found: {adb}"); return; }
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = adb, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
    conn.Open();
    Console.WriteLine("event type counts (most-recent activity first):");
    using (var c = conn.CreateCommand()) { c.CommandText = "SELECT Type, COUNT(*), MAX(Seq) FROM events GROUP BY Type ORDER BY MAX(Seq) DESC"; using var r = c.ExecuteReader(); while (r.Read()) Console.WriteLine($"  {r.GetString(0),-18} count={r.GetInt64(1),6}  lastSeq={r.GetInt64(2)}"); }
    Console.WriteLine("\nrecent 'Rotated' events:");
    using (var c = conn.CreateCommand()) { c.CommandText = "SELECT Seq, DocumentId, UnitId, At FROM events WHERE Type='Rotated' ORDER BY Seq DESC LIMIT 10"; using var r = c.ExecuteReader(); var any = false; while (r.Read()) { any = true; Console.WriteLine($"  seq={r.GetInt64(0)} doc={r.GetString(1)} unit={(r.IsDBNull(2) ? "(null)" : r.GetString(2))} at={r.GetString(3)}"); } if (!any) Console.WriteLine("  (NONE — the backend has recorded no rotation)"); }
    return;
}

if (command == "test-bind")
{
    // Does the minimal-API JSON stack (web defaults) bind {"1":180} → Dictionary<int,int>? (the review `rotations` field)
    var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    void T(string label, string json)
    {
        try { var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(json, opts); Console.WriteLine($"{label} => {(d is null ? "null" : "{" + string.Join(",", d.Select(kv => $"{kv.Key}:{kv.Value}")) + "}")}"); }
        catch (Exception ex) { Console.WriteLine($"{label} => THREW {ex.GetType().Name}: {ex.Message}"); }
    }
    T("Dictionary<int,int> {\"1\":180,\"2\":90}", "{\"1\":180,\"2\":90}");
    return;
}

// PHI-SAFE Gate-3 runtime probe (no host / no cloud): render each PDF and run the native zxing-cpp decoder, printing
// ONLY non-identifying signal — page count, #codes, symbology:bytelength, and boundary / structural-KVNR flags.
// NEVER prints a payload, KVNR, name or filename. Usage: barcode-probe <pdf|dir> [maxDocs]
if (command == "barcode-probe")
{
    var target = args.Length > 1 ? args[1] : "";
    var max = args.Length > 2 && int.TryParse(args[2], out var mm) ? mm : 60;
    var files = Directory.Exists(target)
        ? Directory.EnumerateFiles(target, "*.pdf").OrderBy(f => f, StringComparer.Ordinal).Take(max).ToList()
        : (File.Exists(target) ? new List<string> { target } : new List<string>());
    if (files.Count == 0) { Console.Error.WriteLine($"No PDF(s) at '{target}'. Usage: barcode-probe <pdf|dir> [maxDocs]"); return; }
    int idx = 0, decoded = 0, withCode = 0, withBoundary = 0, withStruct = 0, errors = 0;
    foreach (var f in files)
    {
        IReadOnlyList<SkiaSharp.SKBitmap>? pages = null;
        try
        {
            pages = Adelaide.Ai.PdfRenderer.Render(f, 300, 15);
            var bars = Adelaide.Ai.BarcodeScanner.ScanRaw(pages);
            decoded++;
            var boundary = bars.Any(b => (b.Format.Contains("Matrix", StringComparison.Ordinal) && b.Bytes > 80) || b.Format.Contains("PDF417", StringComparison.Ordinal));
            var hasStruct = bars.Any(b => Adelaide.Core.BarcodeIdentity.StructuralKvnr(b.Text) is not null);
            if (bars.Count > 0) withCode++;
            if (boundary) withBoundary++;
            if (hasStruct) withStruct++;
            var fmts = string.Join(",", bars.Select(b => $"{b.Format}:{b.Bytes}b").Distinct().OrderBy(x => x, StringComparer.Ordinal));
            Console.WriteLine($"[{++idx:D3}] pages={pages.Count,2} codes={bars.Count} boundary={(boundary ? "Y" : "n")} structKVNR={(hasStruct ? "Y" : "n")}  [{fmts}]");
        }
        catch (Exception ex) { errors++; Console.WriteLine($"[{++idx:D3}] ERROR {ex.GetType().Name}: {ex.Message}"); }
        finally { if (pages is not null) foreach (var p in pages) p.Dispose(); }
    }
    Console.WriteLine($"\n== {decoded}/{files.Count} rendered - {withCode} had >=1 code - {withBoundary} a boundary code - {withStruct} a structural KVNR - {errors} error(s) ==");
    return;
}

// Commands that talk to live SharePoint/Graph: build the host with DI, logging, and a resilient HttpClient.
var builder = Host.CreateApplicationBuilder(args);
// The framework's per-request HttpClient + Polly logging is very noisy for these batch/diagnostic commands and buries
// the command's own output. Keep warnings+ so failures and failed retries still show.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Polly", LogLevel.Warning);
var spOptions = SharePointOptions.Load();
builder.Services.AddSingleton(spOptions);
builder.Services.AddSingleton<TokenCredential>(_ =>
    new ClientSecretCredential(spOptions.TenantId, spOptions.ClientId, spOptions.ClientSecret)); // → ManagedIdentityCredential in prod
builder.Services.AddHttpClient<GraphPatientDirectory>()
    .AddStandardResilienceHandler(); // retry + circuit breaker + timeout (handles Graph 429/5xx)
builder.Services.AddTransient<IPatientDirectory>(sp => sp.GetRequiredService<GraphPatientDirectory>()); // concrete also resolvable (review search)
builder.Services.AddSingleton(AiOptions.Load());
builder.Services.AddHttpClient<OpenAiClient>().AddStandardResilienceHandler(o =>
{
    // gpt-4o vision/extraction calls take tens of seconds — the default 10s attempt / 30s total cancels them.
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240); // must be >= 2 x AttemptTimeout
});
builder.Services.AddTransient<IExtractor, AzureOpenAiExtractor>();
builder.Services.AddTransient<ITranscriber, VisionTranscriber>();
builder.Services.AddHttpClient<DocIntelligencePdf>().AddStandardResilienceHandler(o =>
{
    // Searchable-PDF generation on multi-page scans can take tens of seconds; give it the same headroom as vision.
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
});
builder.Services.AddTransient<IPdfTextLayer>(sp => sp.GetRequiredService<DocIntelligencePdf>());
builder.Services.AddHttpClient<SharePointSink>().AddStandardResilienceHandler();
builder.Services.AddTransient<IDocumentSink>(sp => sp.GetRequiredService<SharePointSink>());
var auditDb = Environment.GetEnvironmentVariable("ADELAIDE_AUDIT_DB") ?? "adelaide-audit.db";
builder.Services.AddSingleton(_ => new SqliteAuditStore(auditDb)); // → Azure SQL ledger in prod
builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<SqliteAuditStore>());
builder.Services.AddSingleton<ILedger>(sp => sp.GetRequiredService<SqliteAuditStore>());
builder.Services.AddSingleton<IAuditStore>(sp => sp.GetRequiredService<SqliteAuditStore>()); // the seam — new commands depend on it, not the concrete

using var host = builder.Build();

switch (command)
{
    case "match-smoke":
        await SmokeTests.RunAsync(host.Services);
        break;
    case "ai-ping":
        Console.WriteLine(await host.Services.GetRequiredService<OpenAiClient>().AskAsync("Reply with the single word: ok"));
        break;
    case "extract-demo":
        await ExtractDemo.RunAsync(host.Services);
        break;
    case "ingest":
        await IngestDemo.RunAsync(host.Services, args.Length > 1 ? args[1] : "");
        break;
    case "sink-test":
        await host.Services.GetRequiredService<SharePointSink>().SelfTestAsync();
        break;
    case "site-info":
    {
        // Resolve the site's URL (and display name) from the configured Graph composite site id — for the PnP -SiteUrl.
        var spOpt = host.Services.GetRequiredService<SharePointOptions>();
        var cred = host.Services.GetRequiredService<TokenCredential>();
        var token = await cred.GetTokenAsync(new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), default);
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        var resp = await http.GetAsync($"https://graph.microsoft.com/v1.0/sites/{spOpt.SiteId}?$select=id,webUrl,displayName,name");
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) { Console.Error.WriteLine($"Graph {(int)resp.StatusCode}: {body}"); break; }
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var r = doc.RootElement;
        string G(string n) => r.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
        Console.WriteLine($"displayName : {G("displayName")}");
        Console.WriteLine($"webUrl      : {G("webUrl")}   <-- use this as PnP -SiteUrl");
        break;
    }
    case "audit-demo":
        await AuditDemo.RunAsync(host.Services);
        break;
    case "audit-show":
        await AuditShow.RunAsync(host.Services, args.Length > 1 ? args[1] : "");
        break;
    case "ensure-volltext-column":
        try
        {
            Console.WriteLine(await host.Services.GetRequiredService<SharePointSink>().EnsureVolltextColumnAsync()
                ? "Created 'Volltext' (multi-line text) column on 'Patient Records'."
                : "'Volltext' column already exists — nothing to do.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("403") || ex.Message.Contains("accessDenied"))
        {
            Console.WriteLine("Cannot create the column: the app registration lacks schema permission (column creation needs Sites.Manage.All; it currently has item read/write only).");
            Console.WriteLine("Either grant Sites.Manage.All to the app and re-run, OR add it once by hand in SharePoint:");
            Console.WriteLine("  Patient Records → Settings → Create column → 'Multiple lines of text' → name it exactly 'Volltext' → Plain text → Save.");
            Console.WriteLine("Writing the transcript into it works with the current permissions, so filing will populate it as soon as the column exists.");
        }
        break;
    case "di-pdf-test":
    {
        // Isolated check of the searchable-PDF round-trip (Document Intelligence) — no SharePoint involved.
        var path = args.Length > 1 ? args[1] : "";
        if (!File.Exists(path)) { Console.Error.WriteLine($"File not found: '{path}'. Usage: di-pdf-test <pdf>"); break; }
        var conv = host.Services.GetRequiredService<DocIntelligencePdf>();
        var input = await File.ReadAllBytesAsync(path);
        Console.WriteLine($"Input: {input.Length:N0} bytes — calling Document Intelligence (prebuilt-read, output=pdf)…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outp = await conv.MakeSearchableAsync(input);
        sw.Stop();
        if (outp is null) { Console.WriteLine($"Returned null after {sw.Elapsed.TotalSeconds:F1}s (DI not configured or failed) — filing would fall back to the original PDF."); break; }
        var dst = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + ".searchable.pdf");
        await File.WriteAllBytesAsync(dst, outp);
        Console.WriteLine($"Searchable PDF: {outp.Length:N0} bytes in {sw.Elapsed.TotalSeconds:F1}s → {dst}");
        break;
    }
    case "patient-search":
    {
        var q = args.Length > 1 ? args[1] : "";
        var hits = await host.Services.GetRequiredService<GraphPatientDirectory>().SearchAsync(q);
        Console.WriteLine($"Query '{q}': {hits.Count} match(es)."); // PHI-light: count only
        break;
    }
    case "patient-detail":
    {
        // Debug: print the directory record(s) exactly as GraphMapping maps them, to compare against extraction.
        var q = args.Length > 1 ? args[1] : "";
        var hits = await host.Services.GetRequiredService<GraphPatientDirectory>().SearchAsync(q, 10);
        foreach (var p in hits)
            Console.WriteLine($"listItem {p.ListItemId} | KVNR='{p.Kvnr}' | {p.LastName}, {p.FirstName} | DOB={(p.BirthDate.HasValue ? p.BirthDate.Value.ToString("yyyy-MM-dd") : "(null)")} | Status='{p.Status}' | Klient='{p.KlientId}' | PatNr='{p.PatientNr}'");
        if (hits.Count == 0) Console.WriteLine("(no matches)");
        if (args.Length > 2) Console.WriteLine($"Kvnr.IsValid('{args[2]}') = {Adelaide.Core.Kvnr.IsValid(args[2])}");
        break;
    }
    case "patient-raw":
    {
        var q = args.Length > 1 ? args[1] : "";
        var raw = await host.Services.GetRequiredService<GraphPatientDirectory>().DebugRawAsync(q);
        Console.WriteLine(raw is null ? "(no match)" : $"raw InsuranceNumber='{raw.Value.Kvnr}'  raw DateOfBirth='{raw.Value.Dob}'");
        break;
    }
    case "extract-only":
    {
        // Transcribe + extract a PDF and print the result + checkbox-relevant transcript lines — NO SharePoint write.
        var path = args.Length > 1 ? args[1] : "";
        if (!File.Exists(path)) { Console.Error.WriteLine($"File not found: '{path}'"); break; }
        var transcriber = host.Services.GetRequiredService<ITranscriber>();
        var extractor = host.Services.GetRequiredService<IExtractor>();
        var doc = new IncomingDocument("debug", path, SourceChannel.Api, Path.GetFileName(path));
        var t = await transcriber.TranscribeAsync(doc);
        var unit = new DocumentUnit("debug", 1, t.PageCount, t, path);
        var data = await extractor.ExtractAsync(unit);
        Console.WriteLine($"pages={t.PageCount} legibility={t.QualityScore:F2}");
        foreach (var f in data.Fields) Console.WriteLine($"  {f.Name,-18}= '{f.Value}'  (conf {f.Confidence:F2}, {f.Source})");
        Console.WriteLine("--- transcript lines mentioning Erst/Folge/checkbox ---");
        foreach (var line in t.Markdown.Split('\n').Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, "erordnung|Erstver|Folgever|Folge|☒|☑|⊠|☐|\\[[ xX]\\]|Ankreuz", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Take(16))
            Console.WriteLine("   " + line.Trim());
        break;
    }
    case "barcode-dump":
    {
        // Decode every barcode on a PDF and print the FULL payload — answers "is the form content in the barcode?"
        var path = args.Length > 1 ? args[1] : "";
        if (!File.Exists(path)) { Console.Error.WriteLine($"File not found: '{path}'"); break; }
        var pages = Adelaide.Ai.PdfRenderer.Render(path, 300, 15);
        try
        {
            var bars = Adelaide.Ai.BarcodeScanner.ScanRaw(pages);
            Console.WriteLine($"{bars.Count} distinct barcode(s) across {pages.Count} page(s):");
            var n = 0;
            foreach (var (fmt, text, bytes) in bars)
            {
                Console.WriteLine($"===== [{++n}] {fmt}  ({bytes} bytes, {text.Length} chars) =====");
                Console.WriteLine(text);
                Console.WriteLine();
            }
        }
        finally { foreach (var p in pages) p.Dispose(); }
        break;
    }
    case "bfb-test":
    {
        // Decode the Muster 12 BFB barcode and print the structured fields — validates BfbBarcode.Parse on real docs.
        var path = args.Length > 1 ? args[1] : "";
        if (!File.Exists(path)) { Console.Error.WriteLine($"File not found: '{path}'"); break; }
        var pages = Adelaide.Ai.PdfRenderer.Render(path, 300, 15);
        try
        {
            var bars = Adelaide.Ai.BarcodeScanner.ScanRaw(pages);
            var bfb = bars.Select(b => BfbBarcode.Parse(b.Text)).FirstOrDefault(x => x is not null);
            if (bfb is null) { Console.WriteLine("No Muster 12 BFB barcode parsed on this document."); break; }
            Console.WriteLine($"Name        : {bfb.LastName}, {bfb.FirstName}");
            Console.WriteLine($"BirthDate   : {bfb.BirthDate}");
            Console.WriteLine($"KVNR        : {bfb.Kvnr}");
            Console.WriteLine($"IssueDate   : {bfb.IssueDate}");
            Console.WriteLine($"Diagnosis   : {bfb.Diagnosis}");
            Console.WriteLine($"Order       : {bfb.PrescriptionOrder}");
            Console.WriteLine($"Zeitraum    : {bfb.ValidFrom} - {bfb.ValidUntil}");
            Console.WriteLine($"Kompr. side : {bfb.KompressionSide}");
            Console.WriteLine($"Leistungen  : {bfb.LeistungenSummary}");
        }
        finally { foreach (var p in pages) p.Dispose(); }
        break;
    }
    case "layout-dump":
    {
        // layout-dump <pdf> [pages] — run DI prebuilt-layout and print every selection mark (checkbox) with
        // its state, confidence and nearest label, plus Verordnung-relevant lines. Proves what DI can read.
        var path = args.Length > 1 ? args[1] : "";
        if (!File.Exists(path)) { Console.Error.WriteLine($"File not found: '{path}'"); break; }
        var pagesArg = args.Length > 2 ? args[2] : null; // optional range, e.g. "1-2", to limit DI billing
        var di = host.Services.GetRequiredService<DocIntelligencePdf>();
        var bytes = await File.ReadAllBytesAsync(path);
        Console.WriteLine($"Analyzing {bytes.Length:N0} bytes with prebuilt-layout (pages={pagesArg ?? "all"})…");
        var lay = await di.AnalyzeLayoutAsync(bytes, pagesArg);
        if (lay is null) { Console.WriteLine("No layout result (DI not configured or failed)."); break; }
        Console.WriteLine($"{lay.Lines.Count} text line(s), {lay.Marks.Count} selection mark(s):");
        foreach (var (m, label, gap) in lay.WithLabels())
            Console.WriteLine($"  p{m.Page} [{(m.Selected ? "X" : " ")}] conf={m.Confidence:F2} @({m.Cx:F3},{m.Cy:F3}) gap={gap:F3} -> \"{label}\"");
        Console.WriteLine("--- Verordnung-relevant lines ---");
        foreach (var l in lay.Lines.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l.Content,
                     "rstverordnung|olgeverordnung|ompression|eidseit|rechts|links|stützende|stabilisierende|Verbände|Erst|Folge",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Take(24))
            Console.WriteLine($"  p{l.Page} @({l.Cx:F3},{l.Cy:F3}) \"{l.Content}\"");
        break;
    }
    case "match-test":
    {
        // match-test <lastName> <dob yyyy-MM-dd> [kvnr] — run MatchAsync with synthetic fields.
        var fields = new List<ExtractedField>();
        if (args.Length > 1) fields.Add(new("LastName", args[1], 1, "test"));
        if (args.Length > 2) fields.Add(new("BirthDate", args[2], 1, "test"));
        if (args.Length > 3) fields.Add(new("KVNR", args[3], 1, "test"));
        var m = await host.Services.GetRequiredService<IPatientDirectory>().MatchAsync(new ExtractedData(fields));
        Console.WriteLine($"MatchAsync -> {m.Outcome} (method={m.Method})" + (m.Patient is { } p ? $", listItem {p.ListItemId}" : ""));
        break;
    }
    case "verify-chain":
    {
        // READ-ONLY end-to-end hash-chain recompute on the configured ledger (the live SQLite DB via ADELAIDE_AUDIT_DB).
        var v = await host.Services.GetRequiredService<SqliteAuditStore>().VerifyChainAsync();
        Console.WriteLine($"Chain: intact={v.Intact} events={v.EventCount} headSeq={v.HeadSeq} firstBrokenSeq={v.FirstBrokenSeq} headHash={v.HeadHash[..Math.Min(16, v.HeadHash.Length)]}");
        break;
    }
    default:
        Console.Error.WriteLine($"Unknown command '{command}'. Use: demo | match-smoke | ai-ping | extract-demo | ingest <pdf> | sink-test | audit-demo | audit-show <id> | di-pdf-test <pdf> | ensure-volltext-column | patient-search <q> | extract-only <pdf> | barcode-dump <pdf> | bfb-test <pdf> | layout-dump <pdf> [pages] | match-test <name> <dob> [kvnr] | verify-chain | barcode-probe <pdf|dir> [maxDocs]");
        break;
}
