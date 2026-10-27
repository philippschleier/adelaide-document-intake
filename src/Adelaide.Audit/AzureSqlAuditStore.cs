using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Adelaide.Core;
using Microsoft.Data.SqlClient;

namespace Adelaide.Audit;

/// <summary>
/// Azure SQL-backed audit store: the production substrate behind <see cref="IAuditStore"/>. Same contract and SAME
/// hash-chain semantics as <see cref="SqliteAuditStore"/> — identical <c>Canon</c>, identical storage representations
/// (<c>At</c> is the exact <c>o</c>-format string that gets hashed) — so the chain verifies the same way and the SQLite
/// history migrates byte-for-byte. The <c>events</c> table is an APPEND-ONLY LEDGER table (DB-layer tamper-evidence +
/// digest anchoring against even a DBA), the app hash chain is the portable, provider-independent second layer.
///
/// Keyless: connects with <c>Authentication=Active Directory Default</c> (the App Service managed identity); no secret.
/// Schema + the MI database user are provisioned ONCE by an admin (infra/sql/init.sql) — the app's identity has data
/// read/write only, never DDL; the ledger enforces append-only on <c>events</c> regardless of permissions.
/// </summary>
public sealed class AzureSqlAuditStore : IAuditStore, ILedger, IDisposable
{
    private const string Genesis = "GENESIS";
    private readonly string _conn;
    private readonly string _actor;
    private readonly SemaphoreSlim _appendGate = new(1, 1); // single-writer: serialize appends → a consistent hash chain

    // Transient Azure SQL error numbers worth a reconnect (idle disconnects, failovers, throttling). Conservative set.
    private static readonly HashSet<int> Transient =
        [49920, 49919, 49918, 41839, 41325, 41305, 41302, 41301, 40613, 40501, 40197, 10936, 10929, 10928, 10060, 10054, 10053, 4221, 4060, 1205, 233, 121, 64, 0];

    public AzureSqlAuditStore(string connectionString, string actor = "adelaide-worker")
    {
        _conn = connectionString;
        _actor = actor;
    }

    /// <summary>Build the connection string from env, or null if Azure SQL isn't configured (→ caller falls back to SQLite).
    /// Either a full <c>ADELAIDE_SQL_CONNECTION_STRING</c>, or <c>ADELAIDE_SQL_SERVER</c> + <c>ADELAIDE_SQL_DATABASE</c>
    /// (keyless: Active Directory Default = the App Service managed identity).</summary>
    public static string? ConnectionStringFromEnv()
    {
        var full = Environment.GetEnvironmentVariable("ADELAIDE_SQL_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(full)) return full;
        var server = Environment.GetEnvironmentVariable("ADELAIDE_SQL_SERVER");
        var db = Environment.GetEnvironmentVariable("ADELAIDE_SQL_DATABASE");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(db)) return null;
        return new SqlConnectionStringBuilder
        {
            DataSource = server.Contains('.') ? server : $"{server}.database.windows.net",
            InitialCatalog = db,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault, // App Service MI / DefaultAzureCredential — keyless
            ConnectTimeout = 30,
        }.ConnectionString;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        const int maxTries = 4;
        for (int attempt = 1; ; attempt++)
        {
            var c = new SqlConnection(_conn);
            try { await c.OpenAsync(ct); return c; }
            catch (SqlException ex) when (attempt < maxTries && ex.Errors.Cast<SqlError>().Any(e => Transient.Contains(e.Number)))
            {
                c.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt * attempt), ct); // backoff; the append tx itself runs once
            }
            catch { c.Dispose(); throw; }
        }
    }

    // ILedger — pipeline state transitions become audit events.
    public Task SetStateAsync(string id, ProcessingState state, CancellationToken ct = default) =>
        AppendAsync(new AuditEntry(id, state.ToString(), _actor), ct);

    public async Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await _appendGate.WaitAsync(ct);
        try
        {
            using var c = await OpenAsync(ct);
            // Serializable + UPDLOCK on the MAX(Seq) read: defence-in-depth so even a second writer (the invariant says
            // there isn't one) can't assign a duplicate Seq — it blocks rather than races. The whole append is atomic, so
            // a transient fault rolls it back and the caller re-drives; nothing half-writes the chain.
            using var tx = (SqlTransaction)await c.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

            // SEALED GUARD — the Azure twin of SqliteAuditStore's (see there for the full rationale): a sealed
            // document (DocumentStates.Sealed) rejects every append, structurally, inside the append tx at the
            // single choke point. Appending Purged to a not-yet-sealed doc stays legal (the sweep's seal).
            using (var sealedChk = c.CreateCommand())
            {
                sealedChk.Transaction = tx;
                sealedChk.CommandText = "SELECT State FROM documents WHERE DocumentId=@id;";
                P(sealedChk, "@id", entry.DocumentId);
                if (await sealedChk.ExecuteScalarAsync(ct) is string sealedState && DocumentStates.IsSealed(sealedState))
                    throw new InvalidOperationException($"Document {entry.DocumentId} is sealed ({sealedState}); the ledger accepts no further events for it.");
            }

            var seq = await NextSeqAsync(c, tx, ct);
            var prev = await LastHashAsync(c, tx, ct);
            var atStr = DateTimeOffset.UtcNow.ToString("o");
            var details = entry.Details ?? "{}";
            var hash = Sha256Hex(Canon(prev, seq, entry.DocumentId, entry.UnitId, entry.Type, atStr, entry.Actor, entry.CorrelationId, details));

            using (var ins = c.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO events (Seq, DocumentId, UnitId, Type, At, Actor, CorrelationId, Details, PrevHash, Hash)
                    VALUES (@seq, @doc, @unit, @type, @at, @actor, @corr, @details, @prev, @hash);
                    """;
                P(ins, "@seq", seq); P(ins, "@doc", entry.DocumentId); P(ins, "@unit", entry.UnitId);
                P(ins, "@type", entry.Type); P(ins, "@at", atStr); P(ins, "@actor", entry.Actor);
                P(ins, "@corr", entry.CorrelationId); P(ins, "@details", details); P(ins, "@prev", prev); P(ins, "@hash", hash);
                await ins.ExecuteNonQueryAsync(ct);
            }

            await UpsertProjectionAsync(c, tx, entry, atStr, details, ct);
            await tx.CommitAsync(ct);
            return new AuditEvent(seq, entry.DocumentId, entry.UnitId, entry.Type, DateTimeOffset.Parse(atStr, CultureInfo.InvariantCulture), entry.Actor, entry.CorrelationId, details, hash);
        }
        finally { _appendGate.Release(); }
    }

    public async Task<DocumentAudit?> GetDocumentAsync(string documentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + " WHERE DocumentId=@id";
        P(cmd, "@id", documentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    public async Task<IReadOnlyList<DocumentAudit>> ListDocumentsAsync(int limit = 5000, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP (@n) " + ProjectionColumns + " FROM documents ORDER BY LastUpdated DESC";
        P(cmd, "@n", limit);
        return await ReadDocsAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<DocumentAudit>> ListActiveAsync(int limit = 300, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP (@n) " + ProjectionColumns + " FROM documents" + AuditQueryPredicates.ActiveWhere + // terminal states (incl. Segmented) excluded — ONE source in AuditQueryPredicates
            " ORDER BY CASE State WHEN 'NeedsReview' THEN 0 WHEN 'DuplicateSuspect' THEN 1 WHEN 'DeadLetter' THEN 2 ELSE 3 END," +
            " FirstSeen DESC";
        P(cmd, "@n", limit);
        return await ReadDocsAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<DocumentAudit>> ListReconcilableAsync(int limit = 100_000, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP (@n) " + ProjectionColumns + " FROM documents" + AuditQueryPredicates.ReconcileWhere + // terminal + human-pending excluded — ONE source in AuditQueryPredicates
            " ORDER BY FirstSeen";
        P(cmd, "@n", limit);
        return await ReadDocsAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<DocumentAudit>> ListByStateAsync(string state, int limit = 5000, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP (@n) " + ProjectionColumns + " FROM documents WHERE State=@s ORDER BY LastUpdated DESC";
        P(cmd, "@s", state); P(cmd, "@n", limit);
        return await ReadDocsAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<DocumentAudit>> ListDuplicatesAsync(int limit = 5000, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP (@n) " + ProjectionColumns + " FROM documents" + AuditQueryPredicates.DuplicateWhere + " ORDER BY LastUpdated DESC";
        P(cmd, "@n", limit);
        return await ReadDocsAsync(cmd, ct);
    }

    public async Task<int> CountDuplicatesAsync(CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM documents" + AuditQueryPredicates.DuplicateWhere;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountsByStateAsync(CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT State, COUNT(*) FROM documents GROUP BY State";
        var counts = new Dictionary<string, int>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) counts[r.GetString(0)] = r.GetInt32(1);
        return counts;
    }

    /// <summary>The stalled-document gauge. The SQLite store's queries, differing only where T-SQL forces it
    /// (TOP instead of LIMIT) — same predicate constant, same cutoff formatting, same row shaping. The two
    /// substrates are contract-tested for identical results: a gauge that disagreed between localhost and
    /// production would be worse than none.</summary>
    public async Task<StalledDocuments> CountStalledAsync(DateTimeOffset activeOlderThan, DateTimeOffset queuedOlderThan,
        CancellationToken ct = default)
    {
        var active = activeOlderThan.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        var queued = queuedOlderThan.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        using var c = await OpenAsync(ct);
        using var agg = c.CreateCommand();
        agg.CommandText = "SELECT State, COUNT(*), MIN(LastUpdated) FROM documents" + AuditQueryPredicates.StalledWhere + " GROUP BY State";
        P(agg, "@activeBefore", active); P(agg, "@queuedBefore", queued);

        using var sample = c.CreateCommand();
        // ASCENDING, unlike every other projection query in this file — the sample exists for exactly the rows that
        // "most recently updated first" buries.
        sample.CommandText = "SELECT TOP (" + StalledDocuments.SampleSize + ") DocumentId FROM documents"
            + AuditQueryPredicates.StalledWhere + " ORDER BY LastUpdated";
        P(sample, "@activeBefore", active); P(sample, "@queuedBefore", queued);

        return await SqliteAuditStore.ReadStalledAsync(agg, sample, ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string documentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Seq,DocumentId,UnitId,Type,At,Actor,CorrelationId,Details,Hash FROM events WHERE DocumentId=@id ORDER BY Seq";
        P(cmd, "@id", documentId);
        var list = new List<AuditEvent>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<ChainVerification> VerifyChainAsync(CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Seq,DocumentId,UnitId,Type,At,Actor,CorrelationId,Details,Hash FROM events ORDER BY Seq";
        var prev = Genesis;
        var headHash = Genesis;
        long count = 0, headSeq = 0, firstBroken = 0;
        var intact = true;
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var e = Read(r);
            // Recompute with the EXACT append-time inputs — At read raw (r.GetString(4)), the same string AppendAsync
            // hashed, NOT the round-tripped DateTimeOffset, so formatting can never cause a false mismatch.
            if (intact && Sha256Hex(Canon(prev, e.Seq, e.DocumentId, e.UnitId, e.Type, r.GetString(4), e.Actor, e.CorrelationId, e.Details)) != e.Hash)
            {
                intact = false;
                firstBroken = e.Seq;
            }
            prev = e.Hash;
            headHash = e.Hash;
            headSeq = e.Seq;
            count++;
        }
        return new ChainVerification(intact, count, headSeq, headHash, firstBroken);
    }

    /// <summary>Distinct documents with an event of this type since the instant — the capture-health denominator.
    /// At holds the same "o" strings as the SQLite store, so lexical comparison is chronological here too.</summary>
    public async Task<int> CountDocumentsWithEventSinceAsync(string type, DateTimeOffset since, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT DocumentId) FROM events WHERE Type=@t AND At > @since";
        P(cmd, "@t", type); P(cmd, "@since", since.UtcDateTime.ToString("o"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Distinct documents review-filed since the instant — the review-lane label expectation.</summary>
    public async Task<int> CountReviewedThenFiledSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT DocumentId) FROM events WHERE Type='Filed' AND JSON_VALUE(Details,'$.disposition')='ReviewedThenFiled' AND At > @since";
        P(cmd, "@since", since.UtcDateTime.ToString("o"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<string?> FindDocumentIdByDetailAsync(string needle, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 DocumentId FROM events WHERE Details LIKE @needle ORDER BY Seq DESC";
        P(cmd, "@needle", "%" + Like(needle) + "%");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>C6: the authoritative name lookup — names live only in the projection now. LIKE over the display
    /// columns, newest first, wildcards escaped. Same query shape as the SQLite twin; CASE semantics follow each
    /// substrate (SQLite LIKE folds ASCII only, this store's CI collation also folds accented chars) — an accepted
    /// divergence for a diagnostic lookup, documented rather than papered over.</summary>
    public async Task<string?> FindDocumentIdByNameAsync(string needle, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT TOP 1 DocumentId FROM documents
            WHERE OriginalName LIKE @n ESCAPE '\' OR FileName LIKE @n ESCAPE '\' OR SourceRecordId LIKE @n ESCAPE '\'
            ORDER BY LastUpdated DESC
            """;
        P(cmd, "@n", "%" + Like(needle) + "%");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<DocumentAudit?> FindBySha256Async(string sha256, string excludeDocumentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 " + ProjectionColumns + " FROM documents WHERE Sha256=@h AND DocumentId<>@x AND State NOT IN ('Discarded','Superseded','Purged') ORDER BY LastUpdated DESC"; // a superseded doc's file is deleted → never dedup against it; Purged likewise (post-window discard, artifacts released)
        P(cmd, "@h", sha256); P(cmd, "@x", excludeDocumentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    public async Task<DocumentAudit?> FindFiledBySha256Async(string sha256, string excludeDocumentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 " + ProjectionColumns + " FROM documents WHERE Sha256=@h AND State='Filed' AND DocumentId<>@x ORDER BY LastUpdated DESC";
        P(cmd, "@h", sha256); P(cmd, "@x", excludeDocumentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    public async Task<DocumentAudit?> FindBySourceRecordIdAsync(string sourceRecordId, string excludeDocumentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 " + ProjectionColumns + " FROM documents WHERE SourceRecordId=@s AND DocumentId<>@x AND State NOT IN ('Discarded','Superseded','Purged') ORDER BY LastUpdated DESC"; // superseded source-record → gone, don't re-match; Purged likewise
        P(cmd, "@s", sourceRecordId); P(cmd, "@x", excludeDocumentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    public async Task<DocumentAudit?> FindFiledByFileNamePrefixAsync(string prefix, string excludeDocumentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 " + ProjectionColumns + " FROM documents WHERE State='Filed' AND FileName LIKE @p ESCAPE '\\' AND DocumentId<>@x ORDER BY LastUpdated DESC";
        P(cmd, "@p", Like(prefix) + "%"); P(cmd, "@x", excludeDocumentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    // ---- helpers ----
    private const string ProjectionColumns =
        "DocumentId,State,OriginalName,FileName,ContentType,PatientListItemId,Disposition,Reasons,Sha256,DuplicateOf,FirstSeen,LastUpdated,EventCount,SourceRecordId,ReasonCodes,Confidence,DriveItemId,CorrectedFields,ContentTypeChange";
    private const string ProjectionSelect = "SELECT " + ProjectionColumns + " FROM documents";

    // SQL Server LIKE treats %, _, [ and the escape \ as special — escape them for a LITERAL match (SQLite needed only %, _, \).
    private static string Like(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static async Task<IReadOnlyList<DocumentAudit>> ReadDocsAsync(SqlCommand cmd, CancellationToken ct)
    {
        var list = new List<DocumentAudit>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadDoc(r));
        return list;
    }

    private static AuditEvent Read(SqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
        DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.GetString(7), r.GetString(8));

    private static DocumentAudit ReadDoc(SqlDataReader r) => new(
        r.GetString(0), r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
        DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture), r.GetInt32(12),
        r.IsDBNull(13) ? null : r.GetString(13),
        r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(15) ? null : r.GetDouble(15),
        r.IsDBNull(16) ? null : r.GetString(16),
        r.IsDBNull(17) ? null : r.GetString(17),
        r.IsDBNull(18) ? null : r.GetString(18));

    private static async Task UpsertProjectionAsync(SqlConnection c, SqlTransaction tx, AuditEntry entry, string atStr, string details, CancellationToken ct)
    {
        var (file, ctype, pid, disp, original, reasons, sha256, duplicateOf, sourceRecordId, reasonCodes, confidence, driveItemId, correctedFields, contentTypeChange) = KnownFields(details);
        // C6: display values arrive via the side-channel (never in Details); parsed values remain the back-compat
        // path for pre-C6 event shapes. An override, when present, wins — including "" to clear a prior value.
        original = entry.Projection?.OriginalName ?? original;
        file = entry.Projection?.FileName ?? file;
        sourceRecordId = entry.Projection?.SourceRecordId ?? sourceRecordId;
        reasons = entry.Projection?.Reasons ?? reasons;
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        // MERGE = SQLite's INSERT ... ON CONFLICT DO UPDATE. COALESCE(@x, t.col) keeps a prior value when a later event
        // omits it (mirrors the SQLite projection exactly). HOLDLOCK guards the match/insert race (defence-in-depth).
        cmd.CommandText = """
            MERGE documents WITH (HOLDLOCK) AS t
            USING (SELECT @id AS DocumentId) AS s ON (t.DocumentId = s.DocumentId)
            WHEN MATCHED THEN UPDATE SET
                -- ANNOTATION events pass @state = NULL and therefore leave State alone (AuditEventTypes.Advances),
                -- giving State the same keep-prior-on-null discipline every other column below already has. Must stay
                -- byte-for-byte equivalent to the SQLite store — the substrates are contract-tested for this.
                State=COALESCE(@state, t.State),
                OriginalName=COALESCE(@original, t.OriginalName),
                FileName=COALESCE(@file, t.FileName),
                ContentType=COALESCE(@ct, t.ContentType),
                PatientListItemId=COALESCE(@pid, t.PatientListItemId),
                Disposition=COALESCE(@disp, t.Disposition),
                Reasons=COALESCE(@reasons, t.Reasons),
                Sha256=COALESCE(@sha, t.Sha256),
                DuplicateOf=COALESCE(@dup, t.DuplicateOf),
                SourceRecordId=COALESCE(@srid, t.SourceRecordId),
                ReasonCodes=COALESCE(@reasonCodes, t.ReasonCodes),
                Confidence=COALESCE(@confidence, t.Confidence),
                DriveItemId=COALESCE(@driveItemId, t.DriveItemId),
                CorrectedFields=COALESCE(@correctedFields, t.CorrectedFields),
                ContentTypeChange=COALESCE(@contentTypeChange, t.ContentTypeChange),
                -- Annotations are neutral to LIFECYCLE TIMERS as well as to State: @lastAt is NULL for them, so the
                -- aged-input grace window and any other cutoff measured from LastUpdated are not silently regranted by
                -- a bookkeeping append. EventCount still increments — an annotation IS an event.
                LastUpdated=COALESCE(@lastAt, t.LastUpdated),
                EventCount=t.EventCount+1
            WHEN NOT MATCHED THEN INSERT
                (DocumentId, State, OriginalName, FileName, ContentType, PatientListItemId, Disposition, Reasons, Sha256, DuplicateOf, SourceRecordId, ReasonCodes, Confidence, DriveItemId, CorrectedFields, ContentTypeChange, FirstSeen, LastUpdated, EventCount)
                -- No prior row to keep: an annotation as a document's FIRST event has no State to preserve, so fall
                -- back to its own type rather than inserting NULL into a NOT NULL column.
                VALUES (@id, COALESCE(@state, @type), @original, @file, @ct, @pid, @disp, @reasons, @sha, @dup, @srid, @reasonCodes, @confidence, @driveItemId, @correctedFields, @contentTypeChange, @at, @at, 1);
            """;
        var advances = Adelaide.Core.AuditEventTypes.Advances(entry.Type);
        P(cmd, "@id", entry.DocumentId);
        P(cmd, "@state", advances ? entry.Type : null);
        P(cmd, "@type", entry.Type);
        P(cmd, "@lastAt", advances ? atStr : null);
        P(cmd, "@original", original); P(cmd, "@file", file); P(cmd, "@ct", ctype);
        P(cmd, "@pid", pid); P(cmd, "@disp", disp); P(cmd, "@reasons", reasons);
        P(cmd, "@sha", sha256); P(cmd, "@dup", duplicateOf); P(cmd, "@srid", sourceRecordId);
        P(cmd, "@reasonCodes", reasonCodes); P(cmd, "@confidence", confidence); P(cmd, "@driveItemId", driveItemId); P(cmd, "@correctedFields", correctedFields); P(cmd, "@contentTypeChange", contentTypeChange); P(cmd, "@at", atStr);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static (string? file, string? ctype, int? pid, string? disp, string? original, string? reasons, string? sha256, string? duplicateOf, string? sourceRecordId, string? reasonCodes, double? confidence, string? driveItemId, string? correctedFields, string? contentTypeChange) KnownFields(string detailsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null, null, null, null, null, null, null, null, null, null, null, null);
            string? S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? I(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            double? D(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            string? RawArr(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetRawText() : null; // store the JSON array verbatim; the endpoint re-parses it
            string? RawObj(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object ? v.GetRawText() : null; // store the JSON object ({from,to}) verbatim
            string? Reasons() => root.TryGetProperty("reasons", out var v) && v.ValueKind == JsonValueKind.Array
                ? string.Join("; ", v.EnumerateArray().Select(e => e.GetString()))
                : S("error");
            return (S("fileName"), S("contentType"), I("patientListItemId"), S("disposition"), S("original"), Reasons(), S("sha256"), S("duplicateOf"), S("sourceRecordId"), RawArr("reasonCodes"), D("confidence"), S("driveItemId"), RawArr("correctedFields"), RawObj("contentTypeChange"));
        }
        catch { return (null, null, null, null, null, null, null, null, null, null, null, null, null, null); }
    }

    private static async Task<long> NextSeqAsync(SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(Seq),0)+1 FROM events WITH (UPDLOCK, HOLDLOCK)";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<string> LastHashAsync(SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT TOP 1 Hash FROM events ORDER BY Seq DESC";
        return await cmd.ExecuteScalarAsync(ct) as string ?? Genesis;
    }

    // MUST stay byte-identical to SqliteAuditStore.Canon / Sha256Hex — the hash chain and the SQLite->Azure migration
    // both depend on these producing exactly the same bytes for the same inputs.
    private static string Canon(string prev, long seq, string doc, string? unit, string type, string atStr, string actor, string? corr, string details) =>
        $"{prev}|{seq}|{doc}|{unit}|{type}|{atStr}|{actor}|{corr}|{details}";

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static void P(SqlCommand cmd, string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public void Dispose() => _appendGate.Dispose();
}
