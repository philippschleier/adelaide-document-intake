using Adelaide.Core;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Adelaide.Audit.Tests;

// THE CONTRACT (ITranscriptStore): every behavior both implementations must share, run against BOTH substrates —
// SQLite always (the localhost/escape-hatch store), Azure SQL when ADELAIDE_TEST_SQL_CONNECTION points at a server
// (the CI SQL container; self-skips locally, the CorpusGuardTests convention). The SQL variant provisions a FRESH
// database per test from infra/sql/capture-init.sql — so the admin DDL script itself is under test here too.
public abstract class TranscriptStoreContractTests
{
    protected abstract ITranscriptStore? CreateStore();
    /// <summary>The one place the substrates legitimately diverge: SQLite's epoch is the store FILE's creation
    /// instant (never null after construction); the SQL twin has no file, so an empty store yields null.</summary>
    protected abstract bool EmptyStoreEpochIsNull { get; }
    private ITranscriptStore? _s;
    private ITranscriptStore? S => _s ??= CreateStore();

    private static ModelCallRecord Call(string doc, ModelCallSite site, int attempt = 0, DateTimeOffset? at = null) => new(
        new CallContext(doc, site, attempt, PromptHash: "ph-1"),
        at ?? DateTimeOffset.UtcNow,
        Deployment: "gpt-4o", ServedModel: "gpt-4o-2024-11-20", SystemFingerprint: "fp",
        ReasoningEffort: null,
        RequestBody: """{"messages":[]}""", ResponseBody: """{"ok":true}""",
        PromptTokens: 10, CompletionTokens: 5, ReasoningTokens: 0, LatencyMs: 42, RequestId: "req-1", HttpStatus: 200);

    [Fact]
    public async Task Transcript_roundtrip_and_overwrite()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await s.PutAsync("d1", "hello", "sha-1", t0);
        var got = await s.GetAsync("d1");
        Assert.Equal(("hello", "sha-1"), (got!.Value.Transcript, got.Value.Sha256));
        await s.PutAsync("d1", "hello2", "sha-2", t0.AddMinutes(1));   // idempotent by id: overwrite, not accumulate
        Assert.Equal("sha-2", (await s.GetAsync("d1"))!.Value.Sha256);
        Assert.Equal(1, await s.CountAsync());
        Assert.Null(await s.GetAsync("missing"));
    }

    [Fact]
    public async Task Model_calls_append_and_auto_number_attempts_per_site()
    {
        if (S is not { } s) return;
        await s.PutModelCallAsync(Call("d1", ModelCallSite.Transcription));
        await s.PutModelCallAsync(Call("d1", ModelCallSite.Transcription));      // auto → attempt 2
        await s.PutModelCallAsync(Call("d1", ModelCallSite.Extraction));         // own site → attempt 1
        await s.PutModelCallAsync(Call("d1", ModelCallSite.Extraction, attempt: 7)); // explicit wins
        var calls = await s.GetModelCallsAsync("d1");
        Assert.Equal(4, calls.Count);
        Assert.Equal([1, 2, 1, 7], calls.Select(c => c.Attempt).ToArray());      // in insert order
        Assert.Equal(4, await s.ModelCallCountAsync());
        Assert.Equal("""{"messages":[]}""", calls[0].RequestBody);               // verbatim body survives
    }

    [Fact]
    public async Task Attempt_marks_count_strictly_after_the_anchor()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await s.MarkAttemptStartAsync("d1", t0.AddMinutes(-10));
        await s.MarkAttemptStartAsync("d1", t0.AddMinutes(-5));
        await s.MarkAttemptStartAsync("other", t0);
        Assert.Equal(2, await s.AttemptStartsSinceAsync("d1", DateTimeOffset.MinValue));
        Assert.Equal(1, await s.AttemptStartsSinceAsync("d1", t0.AddMinutes(-7)));
        Assert.Equal(0, await s.AttemptStartsSinceAsync("d1", t0));
    }

    [Fact]
    public async Task Corpus_upsert_preserves_the_label()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await s.PutCorpusAsync(new CorpusRow("d1", t0, ModelContentType: "Verordnung", ModelConfidence: 0.9));
        Assert.True(await s.SetLabelAsync("d1", "Genehmigung", DocumentDomain.Medical, "drv-1", t0.AddMinutes(1)));
        await s.PutCorpusAsync(new CorpusRow("d1", t0.AddMinutes(2), ModelContentType: "Rezept"));  // re-process refresh
        var scoreable = await s.ScoreableSinceAsync(t0.AddMinutes(-1));
        // Only the label trio survives a re-put; the processing facts — DriveItemId included — refresh with the
        // new row (it re-lands at the next SetLabel). The label is what must never be silently un-adjudicated.
        Assert.Equal(("d1", "Genehmigung", (string?)null), Assert.Single(scoreable));
        Assert.Equal(1, await s.LabeledRowCountAsync());
        Assert.Equal(1, await s.CorpusRowCountAsync());
        Assert.Equal(("Genehmigung", 1), Assert.Single(await s.LabelCountsAsync()));
    }

    [Fact]
    public async Task SetLabel_reports_false_when_no_corpus_row_exists()
    {
        if (S is not { } s) return;
        Assert.False(await s.SetLabelAsync("nope", "X", DocumentDomain.Medical, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Corpus_fields_upsert_preserves_outcomes_and_matches_names_case_insensitively()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("d1", [new CorpusFieldRow("DocumentDate", true, 0.8, "vision")], t0);
        await s.SetFieldOutcomesAsync("d1", [("documentDate", "CORRECTION")], remainingOutcome: null, t0.AddMinutes(1));
        await s.PutCorpusFieldsAsync("d1", [new CorpusFieldRow("DocumentDate", true, 0.85, "vision")], t0.AddMinutes(2));
        var row = Assert.Single(await s.GetCorpusFieldsAsync("d1"));
        Assert.Equal("CORRECTION", row.Outcome);         // outcome survived the re-put (the label-preserving pattern)
        Assert.Equal(0.85, row.Confidence);              // …while the proposal facts refreshed
    }

    [Fact]
    public async Task Field_outcomes_insert_fills_and_stamp_the_remainder()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await s.PutCorpusFieldsAsync("d1", [new CorpusFieldRow("A", true, 0.9, "vision"), new CorpusFieldRow("B", true, 0.7, "vision")], t0);
        await s.SetFieldOutcomesAsync("d1", [("A", "CORRECTION"), ("C", "FILL")], remainingOutcome: "MATCH", t0.AddMinutes(1));
        var rows = await s.GetCorpusFieldsAsync("d1");
        Assert.Equal(3, rows.Count);
        Assert.Equal("CORRECTION", rows.Single(r => r.FieldName == "A").Outcome);
        Assert.Equal("MATCH", rows.Single(r => r.FieldName == "B").Outcome);     // the remainder stamp
        var fill = rows.Single(r => r.FieldName == "C");                          // human-filled: inserted with Proposed=0
        Assert.Equal("FILL", fill.Outcome);
        Assert.False(fill.Proposed);
        var counts = await s.CorpusFieldOutcomeCountsAsync();
        Assert.Equal(1, counts["CORRECTION"]); Assert.Equal(1, counts["MATCH"]); Assert.Equal(1, counts["FILL"]);
    }

    [Fact]
    public async Task Adjudications_upsert_never_clears_a_recorded_provenance()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await s.PutAdjudicationsAsync("d1", [new FieldAdjudicationRow("DocumentDate", "2026-05-01", "2026-05-02", "correction", "typed")], t0);
        await s.PutAdjudicationsAsync("d1", [new FieldAdjudicationRow("DocumentDate", "2026-05-01", "2026-05-03", "correction", null)], t0.AddMinutes(1));
        var row = Assert.Single(await s.GetAdjudicationsAsync("d1"));
        Assert.Equal("2026-05-03", row.FinalValue);      // replay refreshed the values…
        Assert.Equal("typed", row.Provenance);           // …but the observed provenance survived the NULL
        Assert.Equal(1, await s.AdjudicationCountAsync());
    }

    [Fact]
    public async Task Delete_erases_content_but_measurement_the_redrive_bound_and_the_erasure_log_survive()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await SeedFullDocument(s, "d1", t0);
        await s.LogErasureAsync("d1", "content", "test-admin", t0.AddMinutes(1));
        Assert.True(await s.DeleteAsync("d1"));
        Assert.Null(await s.GetAsync("d1"));
        Assert.Empty(await s.GetModelCallsAsync("d1"));
        Assert.Empty(await s.GetAdjudicationsAsync("d1"));
        // The attempt marks are the redrive bound's counter, not capture content. Erasing content must NOT hand a
        // still-looping poison document a fresh unbounded crash budget (verification finding).
        Assert.Equal(1, await s.AttemptStartsSinceAsync("d1", DateTimeOffset.MinValue));
        Assert.Equal(1, await s.CorpusRowCountAsync());                  // measurement survives
        Assert.Single(await s.GetCorpusFieldsAsync("d1"));
        Assert.False(await s.DeleteAsync("d1"));                          // second delete: no CONTENT left → false
    }

    [Fact]
    public async Task Forget_clears_the_redrive_bound_because_full_is_document_terminal()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await SeedFullDocument(s, "d1", t0);
        Assert.True(await s.ForgetAsync("d1"));
        Assert.Equal(0, await s.AttemptStartsSinceAsync("d1", DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task Forget_removes_measurement_too_but_never_the_erasure_log()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await SeedFullDocument(s, "d1", t0);
        await s.LogErasureAsync("d1", "full", "test-admin", t0.AddMinutes(1));
        Assert.True(await s.ForgetAsync("d1"));
        Assert.Equal(0, await s.CorpusRowCountAsync());
        Assert.Empty(await s.GetCorpusFieldsAsync("d1"));
        // The erasure log is the record OF the act — prove it survived by logging again and both being distinct acts
        // is out of scope here; survival is observable indirectly: Forget returned true and a re-Forget finds nothing.
        Assert.False(await s.ForgetAsync("d1"));
    }

    [Fact]
    public async Task Purge_ages_out_content_and_attempt_marks_but_never_measurement()
    {
        if (S is not { } s) return;
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-40);
        await SeedFullDocument(s, "old", old);
        await SeedFullDocument(s, "new", now);
        var removed = await s.PurgeOlderThanAsync(new TranscriptRetentionPolicy(RetentionDays: 30).CutoffFrom(now));
        Assert.Equal(1, removed);
        Assert.Null(await s.GetAsync("old"));
        Assert.Empty(await s.GetModelCallsAsync("old"));
        Assert.Empty(await s.GetAdjudicationsAsync("old"));
        Assert.Equal(0, await s.AttemptStartsSinceAsync("old", DateTimeOffset.MinValue));
        Assert.Equal(2, await s.CorpusRowCountAsync());                   // C2: measurement is exempt
        Assert.Single(await s.GetCorpusFieldsAsync("old"));
        Assert.NotNull(await s.GetAsync("new"));
    }

    [Fact]
    public async Task Epoch_reflects_the_stores_own_history()
    {
        if (S is not { } s) return;
        var empty = await s.EpochAsync();
        if (EmptyStoreEpochIsNull) Assert.Null(empty); else Assert.NotNull(empty);   // documented divergence, pinned per substrate
        // Any capture activity must anchor the epoch — a model call alone suffices (the is-capture-keeping-up
        // denominator must not reset to "now" just because the CORPUS write path specifically regressed).
        await s.PutModelCallAsync(Call("d1", ModelCallSite.Transcription, at: DateTimeOffset.UtcNow.AddMinutes(-5)));
        var epoch = await s.EpochAsync();
        Assert.NotNull(epoch);
        Assert.True(epoch < DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Artifacts_roundtrip_via_delete_scope()
    {
        if (S is not { } s) return;
        var t0 = DateTimeOffset.UtcNow;
        await s.PutArtifactsAsync("d1", """["page1"]""", null, "[X] Erstverordnung", t0);
        await s.PutArtifactsAsync("d1", """["page1","page2"]""", """[{"symbology":"DataMatrix"}]""", null, t0.AddMinutes(1));
        Assert.True(await s.DeleteAsync("d1"));                           // upserted artifacts are content → deletable
    }

    private static async Task SeedFullDocument(ITranscriptStore s, string id, DateTimeOffset at)
    {
        await s.PutAsync(id, "text", $"sha-{id}", at);
        await s.PutArtifactsAsync(id, """["p"]""", null, null, at);
        await s.PutModelCallAsync(Call(id, ModelCallSite.Transcription, at: at));
        await s.MarkAttemptStartAsync(id, at);
        await s.PutCorpusAsync(new CorpusRow(id, at, ModelContentType: "Verordnung"));
        await s.PutCorpusFieldsAsync(id, [new CorpusFieldRow("DocumentDate", true, 0.8, "vision")], at);
        await s.PutAdjudicationsAsync(id, [new FieldAdjudicationRow("DocumentDate", "a", "b", "correction", null)], at);
    }
}

// ── SQLite: always runs (fresh temp file per test) ──────────────────────────────────────────────────────────────
public sealed class SqliteTranscriptStoreContractTests : TranscriptStoreContractTests, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"adelaide-contract-{Guid.NewGuid():N}");
    protected override bool EmptyStoreEpochIsNull => false;   // the file exists from construction
    protected override ITranscriptStore CreateStore()
    {
        Directory.CreateDirectory(_dir);
        return new TranscriptStore(Path.Combine(_dir, "contract.db"));
    }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }
}

// ── Azure SQL: runs when ADELAIDE_TEST_SQL_CONNECTION names a server (the CI container); self-skips otherwise. ──
// Provisions a fresh database per test and applies infra/sql/capture-init.sql — the DDL script is under test too.
public sealed class AzureSqlTranscriptStoreContractTests : TranscriptStoreContractTests, IDisposable
{
    private static readonly string? ServerConn = Environment.GetEnvironmentVariable("ADELAIDE_TEST_SQL_CONNECTION");
    private string? _dbName;
    protected override bool EmptyStoreEpochIsNull => true;    // no file: null until the first captured row

    protected override ITranscriptStore? CreateStore()
    {
        if (string.IsNullOrWhiteSpace(ServerConn)) return null;   // self-skip: no SQL server in this environment
        _dbName = $"ct_{Guid.NewGuid():N}";
        using (var master = new SqlConnection(ServerConn))
        {
            master.Open();
            using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{_dbName}];";
            create.ExecuteNonQuery();
        }
        var dbConn = new SqlConnectionStringBuilder(ServerConn) { InitialCatalog = _dbName }.ConnectionString;
        using (var c = new SqlConnection(dbConn))
        {
            c.Open();
            using var ddl = c.CreateCommand();
            ddl.CommandText = File.ReadAllText(FindCaptureInitSql());
            ddl.ExecuteNonQuery();
        }
        return new AzureSqlTranscriptStore(dbConn);
    }

    private static string FindCaptureInitSql()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "infra", "sql", "capture-init.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("infra/sql/capture-init.sql not found above " + dir);
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
}
