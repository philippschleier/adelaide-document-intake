using Adelaide.Audit;
using Adelaide.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Adelaide.Migrate.Tests;

/// <summary>The C9 seed against REAL SQL Server (verification finding: the cutover path of record had only ever
/// executed SQLite→SQLite — its first SQL Server run would have been the production cutover itself). Runs when
/// ADELAIDE_TEST_SQL_CONNECTION names a server (the CI container); self-skips otherwise. Fresh database per test
/// from the schema slice of infra/sql/init.sql — so the 19-column INSERT is pinned against the real DDL (NVARCHAR
/// bounds, ledger table, CI collation) exactly as it will run at cutover.</summary>
public sealed class AzureSqlSeedContractTests : IDisposable
{
    private static readonly string? ServerConn = Environment.GetEnvironmentVariable("ADELAIDE_TEST_SQL_CONNECTION");
    private readonly string _src = Path.Combine(Path.GetTempPath(), $"adelaide-seed-src-{Guid.NewGuid():N}.db");
    private string? _dbName;
    private string? _dbConn;

    private string? CreateTargetDb()
    {
        if (string.IsNullOrWhiteSpace(ServerConn)) return null;   // self-skip: no SQL server in this environment
        _dbName = $"seed_{Guid.NewGuid():N}";
        using (var master = new SqlConnection(ServerConn))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_dbName}];";
            create.ExecuteNonQuery();
        }
        _dbConn = new SqlConnectionStringBuilder(ServerConn) { InitialCatalog = _dbName }.ConnectionString;
        using (var c = new SqlConnection(_dbConn))
        {
            c.Open();
            using var ddl = c.CreateCommand();
            var full = File.ReadAllText(FindInitSql());
            var cut = full.IndexOf("-- 3) App managed-identity", StringComparison.Ordinal);
            ddl.CommandText = cut > 0 ? full[..cut] : full;
            ddl.ExecuteNonQuery();
        }
        return _dbConn;
    }

    private static string FindInitSql()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "infra", "sql", "init.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("infra/sql/init.sql not found above " + dir);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _src, _src + "-wal", _src + "-shm" })
            try { File.Delete(f); } catch { /* best effort */ }
        if (_dbName is null || string.IsNullOrWhiteSpace(ServerConn)) return;
        try
        {
            using var master = new SqlConnection(ServerConn);
            master.Open();
            using var drop = master.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}];";
            drop.ExecuteNonQuery();
        }
        catch { /* best effort — the CI container is ephemeral anyway */ }
    }

    [Fact]
    public async Task Seed_round_trips_all_columns_transmutes_and_never_clobbers_on_real_sql_server()
    {
        var dbConn = CreateTargetDb(); if (dbConn is null) return;

        // Source: one fully-populated Filed doc (every nullable column non-null), one non-terminal doc, one doc
        // that exists LIVE in the target already.
        using (var src = new SqliteAuditStore(_src))
        {
            await src.AppendAsync(new AuditEntry("sql-doc-1", "Received", "test",
                Details: """{"channel":"Drain","sha256":"aa11"}""",
                Projection: new ProjectionOverrides(OriginalName: "orig.pdf", SourceRecordId: "share/orig.pdf")));
            await src.AppendAsync(new AuditEntry("sql-doc-1", "NeedsReview", "gate",
                Details: """{"reasonCodes":["low_confidence"],"confidence":0.42}"""));
            await src.AppendAsync(new AuditEntry("sql-doc-1", "Reviewed", "user:rev",
                Details: """{"correctedFields":["documentDate"],"contentTypeChange":{"from":"Arztbrief","to":"Genehmigung"}}"""));
            await src.AppendAsync(new AuditEntry("sql-doc-1", "Filed", "test",
                Details: """{"contentType":"Genehmigung","disposition":"ReviewedThenFiled","duplicateOf":"sql-doc-9","driveItemId":"drv-1"}""",
                Projection: new ProjectionOverrides(FileName: "canonical-1.pdf", Reasons: "geprüft")));
            await src.AppendAsync(new AuditEntry("sql-doc-2", "DeadLetter", "worker",
                Details: """{"errorClass":"redrive_limit","reasonCodes":["redrive_limit"]}"""));
            await src.AppendAsync(new AuditEntry("sql-doc-3", "Received", "test", Details: """{"channel":"Upload","sha256":"src3"}"""));
        }
        using var live = new AzureSqlAuditStore(dbConn);
        await live.AppendAsync(new AuditEntry("sql-doc-3", "Received", "cloud", Details: """{"channel":"Upload","sha256":"live3"}"""));

        Migrator.SeedResult seeded;
        await using (var s = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _src, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        await using (var t = new SqlConnection(dbConn))
        {
            await s.OpenAsync();
            await t.OpenAsync();
            seeded = await Migrator.SeedProjectionAsync(new System.Data.Common.DbConnection[] { s }, t);
        }
        Assert.Equal(3, seeded.UnionCount);
        Assert.Equal(2, seeded.Inserted);
        Assert.Equal(1, seeded.SkippedExisting);

        // The full row round-trips through the REAL DDL (NVARCHAR bounds, ledger DB, CI collation).
        var d1 = await live.GetDocumentAsync("sql-doc-1");
        Assert.Equal("Filed", d1!.State);
        Assert.Equal("orig.pdf", d1.OriginalName);
        Assert.Equal("canonical-1.pdf", d1.FileName);
        Assert.Equal("Genehmigung", d1.ContentType);
        Assert.Equal("ReviewedThenFiled", d1.Disposition);
        Assert.Equal("geprüft", d1.Reasons);
        Assert.Equal("aa11", d1.Sha256);
        Assert.Equal("sql-doc-9", d1.DuplicateOf);
        Assert.Equal("share/orig.pdf", d1.SourceRecordId);
        Assert.Contains("low_confidence", d1.ReasonCodes!);
        Assert.Equal(0.42, d1.Confidence);
        Assert.Equal("drv-1", d1.DriveItemId);
        Assert.Contains("documentDate", d1.CorrectedFields!);
        Assert.Contains("Arztbrief", d1.ContentTypeChange!);

        // Non-terminal transmutes; NULLs bind for every remaining nullable column.
        var d2 = await live.GetDocumentAsync("sql-doc-2");
        Assert.Equal("SeededHistory", d2!.State);
        Assert.Null(d2.DriveItemId);

        // The live row was never clobbered, and the target ledger was never touched.
        Assert.Equal("live3", (await live.GetDocumentAsync("sql-doc-3"))!.Sha256);
        Assert.Equal(1, (await live.VerifyChainAsync()).EventCount);

        // The seeded rows serve the dedup lookups but never the active dashboard.
        Assert.Equal("sql-doc-1", (await live.FindBySourceRecordIdAsync("share/orig.pdf", ""))!.DocumentId);
        Assert.DoesNotContain(await live.ListActiveAsync(), x => x.DocumentId is "sql-doc-1" or "sql-doc-2");
    }
}
