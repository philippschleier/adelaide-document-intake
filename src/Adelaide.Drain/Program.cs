using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Files.Shares;
using Microsoft.Data.Sqlite;

// Adelaide.Drain — the temporary scanner-drain consumer (see src/Adelaide.Drain/README.md).
//
// Reads files from the Azure Files staging share (via the Files SDK + SAS, over 443 — the same way azcopy uploads;
// or from a local directory for testing), POSTs each NEW file to Adelaide's authenticated /api/drain/upload, and marks
// it "sent" ONLY after a 2xx + documentId (ledger-after-ack). The idempotency key is the SourceRecordId = the file's
// share-relative path, so a re-delivery is skipped and nothing is lost or double-sent. Single instance (named mutex).
// Run one pass per invocation (schtasks) or pass --loop / set ADELAIDE_DRAIN_LOOP_SECONDS to poll continuously.
//
// Config (env vars):
//   ADELAIDE_DRAIN_SHARE_URL      (*)    Azure Files share (or directory) URL incl. SAS, e.g.
//                                        https://<acct>.file.core.windows.net/scans?<sas>   (needs at least r + l)
//   ADELAIDE_DRAIN_SHARE          (*)    OR a local directory to poll (testing). Set exactly one of these two.
//   ADELAIDE_DRAIN_ENDPOINT       (req)  e.g. https://adelaide.example.com/api/drain/upload
//   ADELAIDE_DRAIN_TENANT         (req)  Entra tenant id for the Drain service principal
//   ADELAIDE_DRAIN_CLIENT_ID      (req)  the Drain SP's app (client) id
//   ADELAIDE_DRAIN_CLIENT_SECRET  (req)  the Drain SP's client secret
//   ADELAIDE_DRAIN_SCOPE          (req)  token scope, e.g. api://<adelaide-app-id>/.default
//   ADELAIDE_DRAIN_LEDGER                sqlite ledger path (default: drain-ledger.db)
//   ADELAIDE_DRAIN_PATTERN               file glob (default: *.pdf)
//   ADELAIDE_DRAIN_SUBDIR                 scope to one folder within the share, e.g. "Scanner" (default: whole share)
//   ADELAIDE_DRAIN_CLEANUP               "delete" = delete from the share after a confirmed send (SAS needs 'd');
//                                        "none" (default) = leave it (the ledger prevents re-sends; purge at teardown)
//   ADELAIDE_DRAIN_MAX_ATTEMPTS          transient retries before dead-letter (default: 5)
//   ADELAIDE_DRAIN_LOOP_SECONDS          if >0 (or --loop), poll every N seconds (default one pass)

static string? Env(string k) => Environment.GetEnvironmentVariable(k);
static string Req(string k) => Env(k) ?? throw new InvalidOperationException($"Missing required env var {k}");

string? shareUrl, shareDir;
string endpoint, tenant, clientId, clientSecret, scope, ledgerPath, pattern, cleanup, subdir;
int maxAttempts, loopSeconds;
bool loop;
try
{
    shareUrl = Env("ADELAIDE_DRAIN_SHARE_URL");
    shareDir = Env("ADELAIDE_DRAIN_SHARE");
    if (string.IsNullOrWhiteSpace(shareUrl) && string.IsNullOrWhiteSpace(shareDir))
        throw new InvalidOperationException("set ADELAIDE_DRAIN_SHARE_URL (Azure Files + SAS) or ADELAIDE_DRAIN_SHARE (local dir)");
    endpoint = Req("ADELAIDE_DRAIN_ENDPOINT");
    tenant = Req("ADELAIDE_DRAIN_TENANT");
    clientId = Req("ADELAIDE_DRAIN_CLIENT_ID");
    clientSecret = Req("ADELAIDE_DRAIN_CLIENT_SECRET");
    scope = Req("ADELAIDE_DRAIN_SCOPE");
    ledgerPath = Env("ADELAIDE_DRAIN_LEDGER") ?? "drain-ledger.db";
    pattern = Env("ADELAIDE_DRAIN_PATTERN") ?? "*.pdf";
    cleanup = (Env("ADELAIDE_DRAIN_CLEANUP") ?? "none").ToLowerInvariant();   // none | delete
    subdir = (Env("ADELAIDE_DRAIN_SUBDIR") ?? "").Trim('/', '\\').Replace('\\', '/');   // e.g. "Scanner" — scope to one folder
    maxAttempts = int.TryParse(Env("ADELAIDE_DRAIN_MAX_ATTEMPTS"), out var ma) ? ma : 5;
    loopSeconds = int.TryParse(Env("ADELAIDE_DRAIN_LOOP_SECONDS"), out var ls) ? ls : 0;
    loop = args.Contains("--loop") || loopSeconds > 0;
}
catch (Exception ex) { Console.Error.WriteLine($"config error: {ex.Message}"); return 2; }

if (!string.IsNullOrWhiteSpace(shareDir) && !Directory.Exists(shareDir)) { Console.Error.WriteLine($"share dir not found: {shareDir}"); return 2; }

IStagingSource source = !string.IsNullOrWhiteSpace(shareUrl)
    ? new AzureFilesStagingSource(new Uri(shareUrl), pattern, subdir)
    : new LocalStagingSource(Path.GetFullPath(shareDir!), pattern, subdir);
Console.WriteLine($"drain source: {source.Describe()}  cleanup={cleanup}  pattern={pattern}");

// Single instance: a pass that overruns the schtasks interval must not be joined by a second concurrent drain.
using var mutex = new Mutex(true, @"Global\Adelaide.Drain.Consumer", out var createdNew);
if (!createdNew) { Console.WriteLine("another drain instance is running; exiting."); return 0; }

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var credential = new ClientSecretCredential(tenant, clientId, clientSecret);
var ledger = new Ledger(ledgerPath);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var exitCode = 0;
do
{
    try { exitCode = await RunPassAsync(cts.Token); }
    catch (OperationCanceledException) { break; }
    if (!loop || cts.IsCancellationRequested) break;
    try { await Task.Delay(TimeSpan.FromSeconds(loopSeconds > 0 ? loopSeconds : 300), cts.Token); }
    catch (OperationCanceledException) { break; }
}
while (true);
return exitCode;

async Task<int> RunPassAsync(CancellationToken ct)
{
    // One token per pass (the credential caches and refreshes internally).
    AccessToken token;
    try { token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct); }
    catch (Exception ex) { Console.Error.WriteLine($"FATAL: could not acquire drain token: {ex.Message}"); return 3; }

    int sent = 0, skipped = 0, transientFail = 0, dead = 0, pending = 0;
    await foreach (var item in source.ListAsync(ct))
    {
        ct.ThrowIfCancellationRequested();
        var srid = item.SourceRecordId;
        if (ledger.GetState(srid) is "sent" or "deadletter") { skipped++; continue; }

        pending++;
        var res = await PostAsync(item, token.Token, ct);
        if (res.Success)
        {
            ledger.MarkSent(srid, res.DocId);                 // ledger-after-ack: only AFTER 2xx + documentId
            if (cleanup == "delete")
                try { await item.DeleteAsync(ct); }           // staging copy removed only once Adelaide owns it
                catch (Exception ex) { Console.Error.WriteLine($"WARN: sent {srid} but could not delete from staging: {ex.Message}"); }
            sent++;
        }
        else if (res.Auth)
        {
            // Auth failure is fatal and affects EVERY file — scream and stop the pass rather than dead-letter good files.
            Console.Error.WriteLine($"FATAL: drain auth rejected ({res.Error}). Check the Drain app role / token. Stopping pass.");
            return 3;
        }
        else if (res.Permanent)
        {
            ledger.MarkDeadletter(srid, res.Error);
            Console.Error.WriteLine($"DEAD-LETTER {srid}: {res.Error}");
            dead++;
        }
        else
        {
            var attempts = ledger.MarkFailed(srid, res.Error);     // transient → retry next pass
            transientFail++;
            if (attempts >= maxAttempts)
            {
                ledger.MarkDeadletter(srid, $"max attempts ({maxAttempts}) exceeded: {res.Error}");
                Console.Error.WriteLine($"DEAD-LETTER {srid}: max attempts exceeded ({res.Error})");
                dead++;
            }
        }
    }

    Console.WriteLine($"pass: sent={sent} skipped={skipped} transient-fail={transientFail} dead-letter={dead}");
    if (sent == 0 && pending > 0)
        Console.Error.WriteLine($"STALL: {pending} file(s) pending but none delivered this pass.");
    if (dead > 0)
        Console.Error.WriteLine($"ALERT: {dead} file(s) dead-lettered this pass — review and clear them in the drain ledger.");
    return 0;
}

async Task<PostResult> PostAsync(StagingItem item, string bearer, CancellationToken ct)
{
    try
    {
        using var content = new MultipartFormDataContent();
        await using var stream = await item.OpenReadAsync(ct);
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", item.FileName);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        // PERCENT-ENCODE: SourceRecordId is a share-relative path that can contain non-ASCII characters (German umlauts in
        // the scanner filename). HTTP header values MUST be ASCII, so an un-encoded value makes HttpClient throw "Request
        // headers must contain only ASCII characters" BEFORE the request is sent — the upload never reaches the server and
        // this is mis-read as transient, retrying to dead-letter and silently dropping the document. The server
        // UnescapeDataString's it back to the original path.
        req.Headers.Add("X-Source-Record-Id", Uri.EscapeDataString(item.SourceRecordId));

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
        {
            var docId = TryReadDocId(body);
            return docId is null
                ? new PostResult(false, null, false, false, $"2xx but no documentId in response: {Trunc(body)}")
                : new PostResult(true, docId, false, false, null);
        }
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new PostResult(false, null, false, true, $"{(int)resp.StatusCode} {resp.StatusCode}");
        // 4xx (except 408/429) = permanent (malformed/oversize/unsupported); 5xx/408/429 = transient.
        var code = (int)resp.StatusCode;
        var permanent = code is >= 400 and < 500
            && resp.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
        return new PostResult(false, null, permanent, false, $"{code} {resp.StatusCode}: {Trunc(body)}");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return new PostResult(false, null, false, false, ex.Message);   // network/timeout → transient
    }
}

static string? TryReadDocId(string body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array && jobs.GetArrayLength() > 0
            && jobs[0].TryGetProperty("documentId", out var d) && d.ValueKind == JsonValueKind.String)
            return d.GetString();
    }
    catch { /* not JSON we recognise */ }
    return null;
}

static string Trunc(string s) => s.Length <= 200 ? s : s[..200];

internal sealed record PostResult(bool Success, string? DocId, bool Permanent, bool Auth, string? Error);

// ---- staging sources: where the drain reads files from. SourceRecordId is the share/dir-relative path (the idempotency key). ----

internal interface IStagingSource
{
    string Describe();                                              // for logs — must NOT include the SAS
    IAsyncEnumerable<StagingItem> ListAsync(CancellationToken ct);
}

internal abstract class StagingItem
{
    public required string SourceRecordId { get; init; }           // share/dir-relative path, '/'-normalised
    public required string FileName { get; init; }
    public abstract Task<Stream> OpenReadAsync(CancellationToken ct);
    public abstract Task DeleteAsync(CancellationToken ct);
}

// Local directory (testing): point ADELAIDE_DRAIN_SHARE at a folder.
internal sealed class LocalStagingSource(string root, string pattern, string subdir) : IStagingSource
{
    public string Describe() => subdir.Length == 0 ? $"local:{root}" : $"local:{root}/{subdir}";

    public async IAsyncEnumerable<StagingItem> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var scanRoot = subdir.Length == 0 ? root : Path.Combine(root, subdir);
        if (!Directory.Exists(scanRoot)) yield break;
        foreach (var full in Directory.EnumerateFiles(scanRoot, pattern, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            yield return new LocalItem(full)
            {
                SourceRecordId = Path.GetRelativePath(root, full).Replace('\\', '/'),
                FileName = Path.GetFileName(full),
            };
        }
        await Task.CompletedTask;
    }

    private sealed class LocalItem(string fullPath) : StagingItem
    {
        public override Task<Stream> OpenReadAsync(CancellationToken ct) => Task.FromResult<Stream>(File.OpenRead(fullPath));
        public override Task DeleteAsync(CancellationToken ct) { File.Delete(fullPath); return Task.CompletedTask; }
    }
}

// Azure Files via the Files SDK + SAS (REST over 443 — same transport as the on-prem azcopy upload).
internal sealed class AzureFilesStagingSource : IStagingSource
{
    private readonly ShareClient _share;
    private readonly string _pattern;
    private readonly string _subdir;

    public AzureFilesStagingSource(Uri shareUriWithSas, string pattern, string subdir)
    {
        _share = new ShareClient(shareUriWithSas);
        _pattern = pattern;
        _subdir = subdir;
    }

    public string Describe()   // path only — never the SAS query
    {
        var path = _share.Uri.GetLeftPart(UriPartial.Path);
        return _subdir.Length == 0 ? path : $"{path}/{_subdir}";
    }

    public IAsyncEnumerable<StagingItem> ListAsync(CancellationToken ct) =>
        WalkAsync(_subdir.Length == 0 ? _share.GetRootDirectoryClient() : _share.GetDirectoryClient(_subdir), _subdir, ct);

    private async IAsyncEnumerable<StagingItem> WalkAsync(ShareDirectoryClient dir, string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in dir.GetFilesAndDirectoriesAsync(cancellationToken: ct))
        {
            ct.ThrowIfCancellationRequested();
            var rel = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.IsDirectory)
            {
                await foreach (var sub in WalkAsync(dir.GetSubdirectoryClient(entry.Name), rel, ct))
                    yield return sub;
            }
            else if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(_pattern, entry.Name))
            {
                yield return new AzureFileItem(dir.GetFileClient(entry.Name)) { SourceRecordId = rel, FileName = entry.Name };
            }
        }
    }

    private sealed class AzureFileItem(ShareFileClient file) : StagingItem
    {
        public override async Task<Stream> OpenReadAsync(CancellationToken ct) => await file.OpenReadAsync(cancellationToken: ct);
        public override Task DeleteAsync(CancellationToken ct) => file.DeleteIfExistsAsync(cancellationToken: ct);
    }
}

// Durable processed-ledger: which source records have been sent (and to which docId), plus transient-failure attempts
// and dead-lettered poison files. Keyed by SourceRecordId. This is the drain's own state — it must outlive a crash and
// be backed up; it is the authority the wind-down reconciliation reads (see src/Adelaide.Drain/README.md).
internal sealed class Ledger
{
    private readonly string _conn;
    public Ledger(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));   // SQLite won't create a missing parent directory
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sent (
                SourceRecordId TEXT PRIMARY KEY, DocumentId TEXT, State TEXT NOT NULL,
                Attempts INTEGER NOT NULL, LastError TEXT, FirstSeen TEXT NOT NULL, LastUpdated TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_conn); c.Open(); return c; }

    public string? GetState(string srid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT State FROM sent WHERE SourceRecordId=@s";
        cmd.Parameters.AddWithValue("@s", srid);
        return cmd.ExecuteScalar() as string;
    }

    public void MarkSent(string srid, string? docId) => Upsert(srid, "sent", docId, null, attemptsDelta: 0);
    public void MarkDeadletter(string srid, string? err) => Upsert(srid, "deadletter", null, err, attemptsDelta: 0);

    public int MarkFailed(string srid, string? err)
    {
        Upsert(srid, "failed", null, err, attemptsDelta: 1);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Attempts FROM sent WHERE SourceRecordId=@s";
        cmd.Parameters.AddWithValue("@s", srid);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Upsert(string srid, string state, string? docId, string? err, int attemptsDelta)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sent (SourceRecordId, DocumentId, State, Attempts, LastError, FirstSeen, LastUpdated)
            VALUES (@s, @d, @st, @att, @e, @now, @now)
            ON CONFLICT(SourceRecordId) DO UPDATE SET
                State=@st,
                DocumentId=COALESCE(@d, DocumentId),
                Attempts=Attempts+@att,
                LastError=@e,
                LastUpdated=@now;
            """;
        cmd.Parameters.AddWithValue("@s", srid);
        cmd.Parameters.AddWithValue("@d", (object?)docId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@st", state);
        cmd.Parameters.AddWithValue("@att", attemptsDelta);
        cmd.Parameters.AddWithValue("@e", (object?)err ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }
}
