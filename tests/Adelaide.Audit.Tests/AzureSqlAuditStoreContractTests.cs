using Adelaide.Core;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Adelaide.Audit.Tests;

/// <summary>C6 on the PRODUCTION substrate (verification finding: the Azure half shipped untested while the
/// SQLite twin carried all the pins). Runs when ADELAIDE_TEST_SQL_CONNECTION names a server (the CI container);
/// self-skips otherwise.
///
/// CONFIRMED EXECUTING IN CI (checked 2026-08-11, run 31477282303): .github/workflows/tests.yml provisions an
/// mssql:2022 service container and sets ADELAIDE_TEST_SQL_CONNECTION for the whole test step. Note the skip is a
/// bare `return`, which xUnit counts as PASSED — so a green run alone proves nothing. The discriminator is DURATION:
/// this project takes ~50s in CI versus ~7s locally, because each contract test provisions a fresh database and
/// applies the schema. If that ever collapses toward the local timing, these tests have silently stopped running. Provisions a fresh database per test and applies the SCHEMA portion of infra/sql/init.sql
/// (everything before the managed-identity user section, which needs Entra) — so the ledger DDL and the
/// events_readable view are under test too, exactly like the capture store's contract tests.</summary>
public sealed class AzureSqlAuditStoreContractTests : IDisposable
{
    private static readonly string? ServerConn = Environment.GetEnvironmentVariable("ADELAIDE_TEST_SQL_CONNECTION");
    private string? _dbName;
    private string? _dbConn;

    private AzureSqlAuditStore? Create()
    {
        if (string.IsNullOrWhiteSpace(ServerConn)) return null;   // self-skip: no SQL server in this environment
        _dbName = $"audit_{Guid.NewGuid():N}";
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
            // The managed-identity user needs Entra; the CI container has none. Everything above the marker is the
            // schema under test (events LEDGER table, documents projection, indexes, events_readable view).
            var cut = full.IndexOf("-- 3) App managed-identity", StringComparison.Ordinal);
            ddl.CommandText = cut > 0 ? full[..cut] : full;
            ddl.ExecuteNonQuery();
        }
        return new AzureSqlAuditStore(_dbConn);
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

    /// <summary>ANNOTATION events on the PRODUCTION substrate. The SQLite twin carries the pins for this mechanism;
    /// the Azure half is a hand-edited MERGE (COALESCE(@state, t.State) in WHEN MATCHED, COALESCE(@state, @type) in
    /// WHEN NOT MATCHED) that nothing else exercises — exactly the "Azure half shipped untested" gap this file exists
    /// to close. Asserts the FULL state string, since truncation is the failure class a prefix check would miss.</summary>
    [Fact]
    public async Task Annotation_leaves_State_and_LastUpdated_alone_on_azure_sql()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("annot1", "Received", "test"));
        await s.AppendAsync(new AuditEntry("annot1", "Filed", "test", Details: "{\"driveItemId\":\"drv-az-1\"}"));
        var filed = await s.GetDocumentAsync("annot1");

        await Task.Delay(15);
        await s.AppendAsync(new AuditEntry("annot1", AuditEventTypes.SharePointItemRemoved, "reconcile",
            Details: "{\"driveItemIdDeleted\":\"drv-az-old\"}"));

        var d = await s.GetDocumentAsync("annot1");
        Assert.Equal("Filed", d!.State);                     // full string — catches MERGE truncation, not just drift
        Assert.Equal(3, d.EventCount);                       // it IS an event
        Assert.Equal("drv-az-1", d.DriveItemId);             // other columns untouched
        Assert.Equal(filed!.LastUpdated, d.LastUpdated);     // lifecycle timers not regranted
        Assert.True((await s.VerifyChainAsync()).Intact);    // and it chains

        // A later ADVANCING event must still take over — the silent-freeze failure mode.
        await s.AppendAsync(new AuditEntry("annot1", "Discarded", "test"));
        Assert.Equal("Discarded", (await s.GetDocumentAsync("annot1"))!.State);
    }

    /// <summary>The WHEN NOT MATCHED fallback: an annotation with no prior row must not write NULL into the NOT NULL
    /// State column. Unreachable in production (DriveReclassifyAsync refuses to annotate a document with no projection
    /// row) but the MERGE must not fail.</summary>
    [Fact]
    public async Task Annotation_as_first_event_does_not_violate_the_not_null_state_column_on_azure_sql()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("annot2", AuditEventTypes.SharePointItemRemoved, "reconcile"));

        var d = await s.GetDocumentAsync("annot2");
        Assert.NotNull(d);
        Assert.Equal(AuditEventTypes.SharePointItemRemoved, d!.State);
    }

    /// <summary>The stalled-document gauge on the PRODUCTION substrate. Two things here are substrate-specific and
    /// unverifiable against SQLite, which is the whole reason this test exists rather than trusting the shared
    /// predicate constant:
    ///
    /// (1) THE STRING COMPARISON. LastUpdated is NVARCHAR(40) holding an "o"-format timestamp, and
    ///     <c>LastUpdated &lt; @stalledBefore</c> is therefore a COLLATION-dependent comparison, not a temporal one.
    ///     It is sound only because every value is fixed-width with the punctuation at identical positions, so only
    ///     digits ever differ — but "sound because of an argument" is what a test is for. SQLite's BINARY collation
    ///     would pass regardless of whether SQL Server's does.
    ///
    /// (2) THE STATE MATCH. The predicate's IN-lists are rendered string literals compared under the database's
    ///     collation, which in Azure SQL defaults to CASE-INSENSITIVE. A state whose casing drifted would be
    ///     excluded here and included on SQLite — a gauge that disagrees between localhost and production.</summary>
    [Fact]
    public async Task Stalled_gauge_ages_and_excludes_the_same_documents_on_azure_sql()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("st1", "Transcribed", "test"));    // active lane: tight cutoff
        await s.AppendAsync(new AuditEntry("st2", "Filing", "test"));         // active lane
        await s.AppendAsync(new AuditEntry("st3", "NeedsReview", "test"));    // human-pending: a queue, never a stall
        await s.AppendAsync(new AuditEntry("st4", "Filed", "test"));          // terminal: settled
        await s.AppendAsync(new AuditEntry("st5", "Received", "test"));       // QUEUED lane: loose cutoff
        var transcribedAt = (await s.GetDocumentAsync("st1"))!.LastUpdated;
        var future = DateTimeOffset.UtcNow.AddHours(1);
        var past = DateTimeOffset.UtcNow.AddHours(-1);

        // Both cutoffs in the future: every row above is "older than" its own, so only the predicate decides.
        var stalled = await s.CountStalledAsync(future, future);
        Assert.Equal(3, stalled.Count);
        Assert.Equal(1, stalled.ByState["Transcribed"]);
        Assert.Equal(1, stalled.ByState["Filing"]);
        Assert.Equal(1, stalled.ByState["Received"]);
        Assert.False(stalled.ByState.ContainsKey("NeedsReview"));
        Assert.False(stalled.ByState.ContainsKey("Filed"));

        // Both in the past: the SAME rows, and now nothing counts — which is what proves the comparison is
        // actually ordering these strings rather than matching everything or nothing.
        Assert.Equal(0, (await s.CountStalledAsync(past, past)).Count);

        // SPLIT — the T-SQL risk a SQLite test cannot vouch for: a CASE inside a WHERE selecting between two
        // differently-bound parameters, per row. Tight cutoff passed, loose one not: the active lane counts and the
        // queued document does not, from one query over one set of rows.
        var split = await s.CountStalledAsync(activeOlderThan: future, queuedOlderThan: past);
        Assert.Equal(2, split.Count);
        Assert.False(split.ByState.ContainsKey("Received"));

        // And the oldest survives the NVARCHAR round trip as the instant it went in, with its lane.
        Assert.Equal(transcribedAt, stalled.Oldest);
        Assert.Equal("Transcribed", stalled.OldestState);

        // The sample is the third substrate-specific risk: TOP (n) ... ORDER BY is a different construct from
        // LIMIT, and getting the ordering backwards yields the LEAST stuck documents — output that looks right.
        Assert.Equal(["st1", "st2", "st5"], stalled.Sample);

        // An annotation must leave the document stalled: it chains, but it did not advance anything.
        await Task.Delay(15);
        await s.AppendAsync(new AuditEntry("st1", AuditEventTypes.SharePointItemRemoved, "reconcile"));
        var after = await s.CountStalledAsync(future, future);
        Assert.Equal(3, after.Count);
        Assert.Equal(transcribedAt, after.Oldest);
    }

    [Fact]
    public async Task Overrides_reach_projection_but_never_the_event()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("doc1", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"abc123"}""",
            Projection: new ProjectionOverrides(OriginalName: "Muster_Erika_A123456789.pdf", SourceRecordId: "scans/2026/Muster_Erika_A123456789.pdf")));

        var d = await s.GetDocumentAsync("doc1");
        Assert.Equal("Muster_Erika_A123456789.pdf", d!.OriginalName);
        Assert.Equal("scans/2026/Muster_Erika_A123456789.pdf", d.SourceRecordId);

        var e = Assert.Single(await s.GetEventsAsync("doc1"));
        Assert.DoesNotContain("Muster", e.Details);
        Assert.DoesNotContain("scans/", e.Details);
        Assert.Contains("\"sha256\":\"abc123\"", e.Details);
    }

    [Fact]
    public async Task Override_wins_empty_clears_and_the_chain_verifies()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("doc2", "Received", "test",
            Details: """{"original":"from-details.pdf"}""",
            Projection: new ProjectionOverrides(OriginalName: "from-override.pdf")));
        Assert.Equal("from-override.pdf", (await s.GetDocumentAsync("doc2"))!.OriginalName);

        // The T-SQL MERGE's COALESCE must treat "" as a value (clear), not as NULL (keep) — the deliberate
        // stale-prose reset every re-entry event relies on.
        await s.AppendAsync(new AuditEntry("doc2", "DuplicateSuspect", "dedup",
            Details: """{"duplicateOf":"other1"}""",
            Projection: new ProjectionOverrides(Reasons: "inhaltsgleich (SHA-256) mit „other.pdf“")));
        await s.AppendAsync(new AuditEntry("doc2", "Received", "test",
            Details: """{"channel":"Retry"}""",
            Projection: new ProjectionOverrides(Reasons: "")));
        Assert.Equal("", (await s.GetDocumentAsync("doc2"))!.Reasons);

        var v = await s.VerifyChainAsync();
        Assert.True(v.Intact); // the side-channel never participates in hashing
    }

    [Fact]
    public async Task Old_shape_details_still_populate_the_projection()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("doc3", "Received", "test",
            Details: """{"channel":"Upload","original":"legacy-name.pdf","sha256":"ff00"}"""));
        var d = await s.GetDocumentAsync("doc3");
        Assert.Equal("legacy-name.pdf", d!.OriginalName);
        Assert.Equal("ff00", d.Sha256);
    }

    [Fact]
    public async Task Source_record_idempotency_and_name_lookup_work_from_the_projection_alone()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("doc5", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"beef"}""",
            Projection: new ProjectionOverrides(OriginalName: "azure-name-probe.pdf", SourceRecordId: "share/sub/file-a.pdf")));

        Assert.Equal("doc5", (await s.FindBySourceRecordIdAsync("share/sub/file-a.pdf", ""))!.DocumentId);
        Assert.Equal("doc5", await s.FindDocumentIdByNameAsync("azure-name-probe"));

        // Newest-match: a second doc reusing the same name must win (TOP 1 + ORDER BY LastUpdated DESC on the
        // NVARCHAR ISO timestamps — the SQL-side ordering the SQLite twin's test cannot pin).
        await s.AppendAsync(new AuditEntry("doc5b", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"beefb"}""",
            Projection: new ProjectionOverrides(OriginalName: "azure-name-probe.pdf")));
        Assert.Equal("doc5b", await s.FindDocumentIdByNameAsync("azure-name-probe"));

        // Wildcard-literalness under SQL Server LIKE + ESCAPE '\': %, _ and the SQL-Server-only [ must be literals.
        await s.AppendAsync(new AuditEntry("doc5c", "Received", "test",
            Details: """{"channel":"Upload","sha256":"cc"}""",
            Projection: new ProjectionOverrides(OriginalName: "100%_rabatt[1].pdf")));
        Assert.Equal("doc5c", await s.FindDocumentIdByNameAsync("100%_rabatt[1]"));
        Assert.Null(await s.FindDocumentIdByNameAsync("100X_rabatt"));   // '_' is not a wildcard
        Assert.Null(await s.FindDocumentIdByNameAsync("rabatt[2]"));     // '[' is not a character class
    }

    [Fact]
    public async Task Events_readable_view_joins_the_erasable_names_onto_the_chain()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("doc6", "Received", "drain-poller",
            Details: """{"channel":"Drain","sha256":"aa11"}""",
            Projection: new ProjectionOverrides(OriginalName: "readable-join.pdf")));

        using var c = new SqlConnection(_dbConn);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT OriginalName, Details FROM dbo.events_readable WHERE DocumentId='doc6'";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("readable-join.pdf", r.GetString(0));      // joined from the projection…
        Assert.DoesNotContain("readable-join", r.GetString(1)); // …not present in the event payload
    }

    [Fact]
    public async Task Ledger_table_rejects_update_and_delete()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("doc7", "Received", "test", Details: """{"channel":"Upload"}"""));

        using var c = new SqlConnection(_dbConn);
        c.Open();
        foreach (var sql in new[] { "UPDATE dbo.events SET Actor='tamper'", "DELETE FROM dbo.events" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            Assert.Throws<SqlException>(() => cmd.ExecuteNonQuery()); // APPEND_ONLY ledger enforces immutability at the DB layer
        }
    }

    /// <summary>The SEALED append guard on the PRODUCTION substrate (discard-recovery). The rule lives inside
    /// each store's AppendAsync — a hand-edited pair, exactly the class of twin this file exists to keep from
    /// diverging: a Purged document rejects every append (advancing, annotation, SetStateAsync), while sealing
    /// itself and appends over merely-settled states stay legal. SQLite pins the full matrix
    /// (SealedAppendGuardTests); this covers the SqlClient path end-to-end.</summary>
    [Fact]
    public async Task Sealed_document_rejects_all_appends_on_azure_sql()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("sealed1", "Received", "test"));
        await s.AppendAsync(new AuditEntry("sealed1", "Discarded", "test"));
        await s.AppendAsync(new AuditEntry("sealed1", "Purged", "discard-sweep", Details: """{"graceDays":30}"""));
        var before = (await s.GetDocumentAsync("sealed1"))!;

        foreach (var type in new[] { "Received", "Filed", "Purged", AuditEventTypes.SharePointItemRemoved })
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => s.AppendAsync(new AuditEntry("sealed1", type, "test")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((ILedger)s).SetStateAsync("sealed1", ProcessingState.Received));

        var after = (await s.GetDocumentAsync("sealed1"))!;
        Assert.Equal("Purged", after.State);
        Assert.Equal(before.EventCount, after.EventCount);   // nothing landed
        Assert.True((await s.VerifyChainAsync()).Intact);

        // Merely-settled stays appendable (the undiscard shape) — the guard is about Sealed, not Settled.
        await s.AppendAsync(new AuditEntry("sealed2", "Received", "test"));
        await s.AppendAsync(new AuditEntry("sealed2", "Discarded", "test"));
        await s.AppendAsync(new AuditEntry("sealed2", "NeedsReview", "test", Details: """{"channel":"Undiscard"}"""));
        Assert.Equal("NeedsReview", (await s.GetDocumentAsync("sealed2"))!.State);
    }

    /// <summary>The dedup exclusion of Purged on the PRODUCTION substrate — the WHERE lists are hand-edited
    /// twins of the SQLite ones (DedupExclusionTests), exactly the divergence class this file guards.</summary>
    [Fact]
    public async Task Purged_documents_are_invisible_to_dedup_lookups_on_azure_sql()
    {
        using var s = Create(); if (s is null) return;
        await s.AppendAsync(new AuditEntry("dxaz1", "Received", "test", Details: """{"sha256":"az-aaa","sourceRecordId":"az-srid-1"}"""));
        await s.AppendAsync(new AuditEntry("dxaz1", "Discarded", "test"));
        await s.AppendAsync(new AuditEntry("dxaz1", "Purged", "discard-sweep"));

        Assert.Null(await s.FindBySha256Async("az-aaa", "someone-else"));
        Assert.Null(await s.FindBySourceRecordIdAsync("az-srid-1", ""));

        await s.AppendAsync(new AuditEntry("dxaz2", "Received", "test", Details: """{"sha256":"az-aaa"}"""));
        Assert.Equal("dxaz2", (await s.FindBySha256Async("az-aaa", "someone-else"))!.DocumentId);
    }
}
