using Adelaide.Audit;
using Adelaide.Core;
using Adelaide.Migrate;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

// ─────────────────────────────────────────────────────────────────────────────
// SQLite → Azure SQL cutover tooling. TWO modes; DRY-RUN by default (pass --execute to write).
//
// --seed  (C9 — THE plan of record, decision D2: the cloud chain starts EMPTY):
//   Copies the DOCUMENTS PROJECTION ONLY — never an event — as the cloud instance's dedup/idempotency seed
//   (Sha256 re-upload dedup, SourceRecordId drain idempotency, FileName business-key guard, DriveItemId artifact
//   resolution). Union of the given sources, FIRST wins per DocumentId (pass the main ledger via --sqlite, the
//   R6-census shadow chains via --also). INSERT-only-if-absent: a live cloud row is never touched → idempotent,
//   safe to run any time before or after intake starts.
//
//   Adelaide.Migrate --seed --sqlite <main.db> [--also <shadow.db>]... (--sql-conn <cs> | --sql-server <fqdn> --sql-db <name>) [--execute]
//
// (default, no --seed) — the FULL verbatim chain copy, retained but NOT the plan of record (see Migrator docs):
//   Adelaide.Migrate --sqlite <path.db> (--sql-conn <cs> | --sql-server <fqdn> --sql-db <name>) [--execute]
//
// Auth to Azure SQL is keyless (Active Directory Default): run logged in as a member of the SQL AAD admin group
// (e.g. `az login` as an Adelaide-SQL-Admins member). Every SOURCE chain is verified first and the run ABORTS on a
// broken one — a tampered ledger must not seed anything.
// ─────────────────────────────────────────────────────────────────────────────

string? sqlite = null, server = null, db = null, conn = null;
bool execute = false, seed = false;
var also = new List<string>();

var q = new Queue<string>(args);
while (q.Count > 0)
{
    var a = q.Dequeue();
    switch (a)
    {
        case "--sqlite": sqlite = Req(q, a); break;
        case "--also": also.Add(Req(q, a)); break;
        case "--sql-server": server = Req(q, a); break;
        case "--sql-db": db = Req(q, a); break;
        case "--sql-conn": conn = Req(q, a); break;
        case "--seed": seed = true; break;
        case "--execute": execute = true; break;
        case "-h" or "--help": return Usage();
        default: Console.Error.WriteLine($"unknown argument: {a}"); return Usage();
    }
}

if (string.IsNullOrWhiteSpace(sqlite) || !File.Exists(sqlite))
{
    Console.Error.WriteLine("--sqlite <path> is required and must point to an existing SQLite audit DB");
    return Usage();
}
if (also.Count > 0 && !seed)
{
    // Verification finding: silently ignoring --also would let a forgotten --seed run the FULL name-bearing chain
    // copy into the append-only cloud ledger — the exact outcome the seed mode exists to prevent. Refuse loudly.
    Console.Error.WriteLine("--also is only valid with --seed (the full-copy mode migrates a single --sqlite source). Did you forget --seed?");
    return Usage();
}
if (conn is null && (server is null || db is null))
{
    Console.Error.WriteLine("provide --sql-conn <connstr> OR both --sql-server <fqdn> and --sql-db <name>");
    return Usage();
}

conn ??= new SqlConnectionStringBuilder
{
    DataSource = server!.Contains('.') ? server : $"{server}.database.windows.net",
    InitialCatalog = db!,
    Encrypt = SqlConnectionEncryptOption.Mandatory,
    Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault, // keyless: az login / managed identity
    ConnectTimeout = 30,
}.ConnectionString;

if (seed)
{
    // ── C9: the projection-only dedup seed. Sources are opened STRICTLY READ-ONLY throughout — verification
    // included — so frozen census evidence files are never mutated (the store constructor's WAL switch / column
    // migration / view DDL would write; VerifyChainReadOnlyAsync avoids it, and the union read is schema-tolerant). ──
    var sources = new List<string> { sqlite! };
    foreach (var extra in also)
    {
        if (!File.Exists(extra)) { Console.Error.WriteLine($"--also source not found: {extra}"); return 2; }
        sources.Add(extra);
    }

    // Verify EVERY source chain — a tampered ledger must not seed anything.
    foreach (var s in sources)
    {
        var v = await SqliteAuditStore.VerifyChainReadOnlyAsync(s);
        Console.WriteLine($"Source : {s}");
        Console.WriteLine($"  chain  : intact={v.Intact}  events={v.EventCount}  head=seq {v.HeadSeq} / {Short(v.HeadHash)}");
        if (!v.Intact) { Console.Error.WriteLine($"  ABORT — chain BROKEN (first broken seq {v.FirstBrokenSeq})."); return 1; }
    }

    Console.WriteLine($"Target : {(server is not null ? $"{server} / {db}" : "(connection string)")}");
    await using var seedTgt = new SqlConnection(conn);
    await seedTgt.OpenAsync();
    var tgtEventsBefore = Convert.ToInt64(await Scalar(seedTgt, "SELECT COUNT(*) FROM events"));
    var tgtDocsBefore = Convert.ToInt64(await Scalar(seedTgt, "SELECT COUNT(*) FROM documents"));
    Console.WriteLine($"  events: {tgtEventsBefore} (informational — the seed has no code path that writes events)   documents: {tgtDocsBefore}");

    // Preflight runs in BOTH modes: the full union read + the Azure SQL length bounds, so an overlong historical
    // value (a deep share path in SourceRecordId, say) surfaces here as id+column+length — never as a server-side
    // truncation error that would abort the transaction AND echo the PHI value into tooling output.
    var srcConns = new List<System.Data.Common.DbConnection>();
    try
    {
        foreach (var s in sources)
        {
            var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = s, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await c.OpenAsync();
            srcConns.Add(c);
        }
        var pre = await Migrator.SeedPreflightAsync(srcConns);
        Console.WriteLine($"  union  : {pre.UnionCount} distinct document(s) across {sources.Count} source(s)");
        if (pre.Violations.Count > 0)
        {
            Console.Error.WriteLine($"\nABORT — {pre.Violations.Count} value(s) fail the Azure SQL column bounds or the timestamp shape (resolve at the source first):");
            foreach (var vio in pre.Violations) Console.Error.WriteLine($"  {vio}");
            return 1;
        }

        if (!execute)
        {
            Console.WriteLine("\nDRY RUN — existing target rows would be skipped; non-terminal source states seed as 'SeededHistory'; events are never written.");
            Console.WriteLine("Re-run with --execute to apply.");
            return 0;
        }

        Console.WriteLine("\nSeeding...");
        var seeded = await Migrator.SeedProjectionAsync(srcConns, seedTgt);
        var tgtEventsAfter = Convert.ToInt64(await Scalar(seedTgt, "SELECT COUNT(*) FROM events"));
        Console.WriteLine($"  inserted: {seeded.Inserted}   skipped (already present): {seeded.SkippedExisting}");
        if (tgtEventsAfter != tgtEventsBefore)
            // The seed provably cannot write events (its only statements are a SELECT 1 probe and INSERT INTO
            // documents — pinned by MigratorTests); a delta here is the LIVE APP appending during the seed window,
            // which is inside the advertised envelope ("safe to run any time"). Inform, never fail.
            Console.WriteLine($"  note    : events grew {tgtEventsBefore} -> {tgtEventsAfter} during the seed — expected when intake is live; the seed itself cannot write events.");
        Console.WriteLine("\nSUCCESS — projection seeded; the ledger was not touched by this tool.");
        return 0;
    }
    finally { foreach (var c in srcConns) await c.DisposeAsync(); }
}

// 1) Verify the SOURCE chain — never migrate a broken/tampered source.
Console.WriteLine($"Source : {sqlite}");
ChainVerification src;
using (var srcStore = new SqliteAuditStore(sqlite!))
    src = await srcStore.VerifyChainAsync();
Console.WriteLine($"  chain  : intact={src.Intact}  events={src.EventCount}  head=seq {src.HeadSeq} / {Short(src.HeadHash)}");
if (!src.Intact)
{
    Console.Error.WriteLine($"  ABORT — source chain is BROKEN (first broken seq {src.FirstBrokenSeq}). Not migrating a broken ledger.");
    return 1;
}
if (src.EventCount == 0) { Console.WriteLine("  nothing to migrate (0 events)."); return 0; }

// 2) Inspect the TARGET — must be an empty ledger.
Console.WriteLine($"Target : {(server is not null ? $"{server} / {db}" : "(connection string)")}");
await using var tgt = new SqlConnection(conn);
await tgt.OpenAsync();
var existing = Convert.ToInt64(await Scalar(tgt, "SELECT COUNT(*) FROM events"));
Console.WriteLine($"  events already present: {existing}");
if (existing > 0)
{
    Console.Error.WriteLine("  ABORT — target events ledger is not empty. Migrate only into a fresh ledger (drop+recreate per infra/sql/init.sql).");
    return 1;
}

// 3) Dry-run unless --execute.
if (!execute)
{
    Console.WriteLine($"\nDRY RUN — would copy {src.EventCount} event(s) + the documents projection into the target ledger.");
    Console.WriteLine("Re-run with --execute to apply. (Append-only: once written, events cannot be removed.)");
    return 0;
}

// 4) Execute the verbatim copy.
Console.WriteLine("\nMigrating...");
await using var srcConn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sqlite }.ToString());
await srcConn.OpenAsync();
var result = await Migrator.MigrateAsync(srcConn, tgt);
Console.WriteLine($"  copied : {result.EventsCopied} event(s), {result.DocumentsCopied} document(s)");

// 5) Verify the TARGET chain matches the source EXACTLY.
var after = await new AzureSqlAuditStore(conn).VerifyChainAsync();
Console.WriteLine($"  target chain: intact={after.Intact}  events={after.EventCount}  head=seq {after.HeadSeq} / {Short(after.HeadHash)}");
bool ok = after.Intact && after.EventCount == src.EventCount && after.HeadHash == src.HeadHash && after.HeadSeq == src.HeadSeq;
Console.WriteLine(ok
    ? "\nSUCCESS — target chain is intact and matches the source (same count + head hash). Migration verified."
    : "\nFAILURE — target chain does NOT match the source. Investigate before trusting the migrated ledger.");
return ok ? 0 : 1;

static string Req(Queue<string> q, string flag) =>
    q.Count > 0 ? q.Dequeue() : throw new ArgumentException($"missing value after {flag}");

static async Task<object?> Scalar(SqlConnection c, string sql)
{
    await using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    return await cmd.ExecuteScalarAsync();
}

static string Short(string h) => h.Length <= 14 ? h : h[..14] + "…";

static int Usage()
{
    Console.Error.WriteLine(
        "usage: Adelaide.Migrate [--seed] --sqlite <path.db> [--also <path.db>]... (--sql-conn <connstr> | --sql-server <fqdn> --sql-db <name>) [--execute]\n" +
        "  --seed : C9 dedup seed — copy the documents PROJECTION only (union of sources, insert-if-absent; the plan of record)\n" +
        "  (none) : full verbatim chain copy into an EMPTY target ledger (retained; not the plan of record)");
    return 2;
}
