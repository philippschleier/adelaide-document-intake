using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Adelaide.Core;
using Microsoft.Data.Sqlite;

namespace Adelaide.Audit;

/// <summary>
/// SQLite-backed audit log: an append-only event stream secured by a SHA-256 hash chain (tamper-evident),
/// plus a one-row-per-document projection derived from it (event sourcing + CQRS). Events are never updated
/// or deleted — corrections are new events. Also implements <see cref="ILedger"/> so the pipeline's state
/// transitions flow into the same log. Production swap: Azure SQL ledger tables behind <see cref="IAuditLog"/>.
/// </summary>
public sealed class SqliteAuditStore : IAuditStore, ILedger, IDisposable
{
    private const string Genesis = "GENESIS";
    private readonly string _conn;
    private readonly string _actor;
    private readonly SemaphoreSlim _appendGate = new(1, 1); // serialize appends → a consistent hash chain

    public SqliteAuditStore(string dbPath, string actor = "adelaide-worker")
    {
        _conn = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        _actor = actor;
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS events (
                    Seq INTEGER PRIMARY KEY, DocumentId TEXT NOT NULL, UnitId TEXT, Type TEXT NOT NULL,
                    At TEXT NOT NULL, Actor TEXT NOT NULL, CorrelationId TEXT, Details TEXT NOT NULL,
                    PrevHash TEXT NOT NULL, Hash TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_events_doc ON events(DocumentId);
                CREATE TABLE IF NOT EXISTS documents (
                    DocumentId TEXT PRIMARY KEY, State TEXT NOT NULL, OriginalName TEXT, FileName TEXT, ContentType TEXT,
                    PatientListItemId INTEGER, Disposition TEXT, Reasons TEXT, Sha256 TEXT, DuplicateOf TEXT, SourceRecordId TEXT,
                    ReasonCodes TEXT, Confidence REAL, DriveItemId TEXT, CorrectedFields TEXT, ContentTypeChange TEXT,
                    FirstSeen TEXT NOT NULL, LastUpdated TEXT NOT NULL, EventCount INTEGER NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        // Migrate older DBs that predate these columns (ignored if already present).
        foreach (var col in new[] { "OriginalName TEXT", "Reasons TEXT", "Sha256 TEXT", "DuplicateOf TEXT", "SourceRecordId TEXT", "ReasonCodes TEXT", "Confidence REAL", "DriveItemId TEXT", "CorrectedFields TEXT", "ContentTypeChange TEXT" })
            try { using var m = c.CreateCommand(); m.CommandText = $"ALTER TABLE documents ADD COLUMN {col}"; m.ExecuteNonQuery(); }
            catch { /* column already exists */ }
        // Discard-recovery: the undiscard event's correlation id is the join key between the immutable event and
        // the erasable projection Reasons prose carrying the reviewer's why. Minted as a GUID, but a collision
        // must fail the append LOUDLY rather than silently attach one reason to two events. Partial (nulls
        // exempt) because every pre-existing event carries NULL here — measured 0 non-null rows at introduction
        // (2026-08-15) — so the index is purely additive. NOTE the trade-off, deliberately accepted: this
        // repurposes CorrelationId as a unique per-event tag; a future flow that wants to SHARE one id across a
        // request's events must first retire this index. Azure twin: ux_events_correlation in infra/sql/init.sql.
        using (var ix = c.CreateCommand())
        {
            ix.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_events_correlation ON events(CorrelationId) WHERE CorrelationId IS NOT NULL";
            ix.ExecuteNonQuery();
        }
        // C6: the one-query readable ledger. Post-C6 event payloads are structural only (no filenames, no prose);
        // this view re-joins the erasable display columns from the projection so name-enriched reads stay a single
        // SELECT. Recreated (not IF NOT EXISTS) after the column migration above so its shape can never go stale.
        using (var v = c.CreateCommand())
        {
            v.CommandText = """
                DROP VIEW IF EXISTS events_readable;
                CREATE VIEW events_readable AS
                SELECT e.Seq, e.DocumentId, e.UnitId, e.Type, e.At, e.Actor, e.CorrelationId, e.Details,
                       d.State, d.OriginalName, d.FileName, d.SourceRecordId, d.Reasons, d.DriveItemId
                FROM events e LEFT JOIN documents d ON d.DocumentId = e.DocumentId;
                """;
            v.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_conn);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA busy_timeout=5000;"; // wait on a lock instead of failing (worker writes vs dashboard reads)
        p.ExecuteNonQuery();
        return c;
    }

    // ILedger — pipeline state transitions become audit events.
    public Task SetStateAsync(string id, ProcessingState state, CancellationToken ct = default) =>
        AppendAsync(new AuditEntry(id, state.ToString(), _actor), ct);

    public async Task<AuditEvent> AppendAsync(AuditEntry entry, CancellationToken ct = default)
    {
        await _appendGate.WaitAsync(ct);
        try
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            // SEALED GUARD (discard-recovery): a sealed document (DocumentStates.Sealed — Purged) accepts no
            // further events. Enforced HERE, inside the append transaction at the store's single choke point
            // (SetStateAsync funnels through this method too), so no endpoint — present or future — can append
            // over a sealed state by forgetting a check. The projection row is the authority: State is the
            // reduction of the latest advancing event, and a sealed state is reachable only by an explicit
            // Purged append, so a sealed row can never revert. Appending the Purged event ITSELF to a
            // not-yet-sealed document stays legal — that is how the sweep seals. Byte-for-byte the same rule
            // in AzureSqlAuditStore; pinned by SealedAppendGuardTests on both substrates.
            using (var sealedChk = c.CreateCommand())
            {
                sealedChk.Transaction = tx;
                sealedChk.CommandText = "SELECT State FROM documents WHERE DocumentId=@id";
                P(sealedChk, "@id", entry.DocumentId);
                if (sealedChk.ExecuteScalar() is string sealedState && DocumentStates.IsSealed(sealedState))
                    throw new InvalidOperationException($"Document {entry.DocumentId} is sealed ({sealedState}); the ledger accepts no further events for it.");
            }

            var seq = NextSeq(c, tx);
            var prev = LastHash(c, tx);
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
                ins.ExecuteNonQuery();
            }

            UpsertProjection(c, tx, entry, atStr, details);
            tx.Commit();
            return new AuditEvent(seq, entry.DocumentId, entry.UnitId, entry.Type, DateTimeOffset.Parse(atStr, CultureInfo.InvariantCulture), entry.Actor, entry.CorrelationId, details, hash);
        }
        finally { _appendGate.Release(); }
    }

    public async Task<DocumentAudit?> GetDocumentAsync(string documentId, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + " WHERE DocumentId=@id";
        P(cmd, "@id", documentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    public async Task<IReadOnlyList<DocumentAudit>> ListDocumentsAsync(int limit = 5000, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + " ORDER BY LastUpdated DESC LIMIT @n";
        P(cmd, "@n", limit);
        var list = new List<DocumentAudit>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadDoc(r));
        return list;
    }

    /// <summary>The actionable + in-flight documents (everything except the terminal-done Filed/Discarded), most-recent
    /// first — the default dashboard list, so it stays bounded and stable even under a bulk backfill.</summary>
    public async Task<IReadOnlyList<DocumentAudit>> ListActiveAsync(int limit = 300, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Cluster the human-actionable states at the top (review / duplicate / error), in-flight below, and order each
        // group by arrival (FirstSeen) — a STABLE order, so rows don't jump around as documents change state mid-poll.
        cmd.CommandText = ProjectionSelect + AuditQueryPredicates.ActiveWhere + // terminal states (incl. Segmented) excluded — ONE source in AuditQueryPredicates
            " ORDER BY CASE State WHEN 'NeedsReview' THEN 0 WHEN 'DuplicateSuspect' THEN 1 WHEN 'DeadLetter' THEN 2 ELSE 3 END," +
            " FirstSeen DESC LIMIT @n";
        P(cmd, "@n", limit);
        var list = new List<DocumentAudit>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadDoc(r));
        return list;
    }

    /// <summary>Every document still owned by the queue — the in-flight states only (NOT Filed, NOT the human-pending
    /// review/duplicate/dead-letter states, NOT Discarded), oldest first. Startup reconciliation re-enqueues these after
    /// a restart drops the in-memory queue; UNCAPPED by default so a large backlog can't strand docs beyond a page limit.</summary>
    public async Task<IReadOnlyList<DocumentAudit>> ListReconcilableAsync(int limit = 100_000, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + AuditQueryPredicates.ReconcileWhere + // terminal + human-pending excluded — ONE source in AuditQueryPredicates
            " ORDER BY FirstSeen LIMIT @n";
        P(cmd, "@n", limit);
        var list = new List<DocumentAudit>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadDoc(r));
        return list;
    }

    /// <summary>Recent documents in a single state, most-recent first — backs the dashboard's per-state filter (e.g.
    /// click "Abgelegt" to see the filed ones, which the bounded all-states list pushes off-screen during a busy backfill).</summary>
    public async Task<IReadOnlyList<DocumentAudit>> ListByStateAsync(string state, int limit = 5000, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + " WHERE State=@s ORDER BY LastUpdated DESC LIMIT @n";
        P(cmd, "@s", state); P(cmd, "@n", limit);
        var list = new List<DocumentAudit>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadDoc(r));
        return list;
    }

    /// <summary>Open possible-duplicates: exact-byte suspects (DuplicateSuspect) AND business-key flags (any non-terminal
    /// doc carrying a DuplicateOf), most-recent first. The two dedup layers land in different states, so this spans them.</summary>
    public async Task<IReadOnlyList<DocumentAudit>> ListDuplicatesAsync(int limit = 5000, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + AuditQueryPredicates.DuplicateWhere + " ORDER BY LastUpdated DESC LIMIT @n";
        P(cmd, "@n", limit);
        var list = new List<DocumentAudit>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadDoc(r));
        return list;
    }

    /// <summary>Count of open possible-duplicates — the "Duplikat?" chip badge; matches ListDuplicatesAsync.</summary>
    public async Task<int> CountDuplicatesAsync(CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM documents" + AuditQueryPredicates.DuplicateWhere;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Document counts grouped by state — the dashboard summary, so totals of everything stay visible even when
    /// the listed rows are bounded. One cheap aggregate over the one-row-per-document projection.</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountsByStateAsync(CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT State, COUNT(*) FROM documents GROUP BY State";
        var counts = new Dictionary<string, int>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) counts[r.GetString(0)] = r.GetInt32(1);
        return counts;
    }

    /// <summary>The stalled-document gauge. Two bounded reads on ONE connection: the aggregate (count, per-state
    /// breakdown, oldest) and the oldest-first id sample. Both scan the same one-row-per-document projection that
    /// /api/stats already scans unfiltered for CountsByStateAsync, so this adds no new cost class. Must stay
    /// equivalent to the Azure SQL store — the substrates are contract-tested for identical results.</summary>
    public async Task<StalledDocuments> CountStalledAsync(DateTimeOffset activeOlderThan, DateTimeOffset queuedOlderThan,
        CancellationToken ct = default)
    {
        var active = activeOlderThan.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        var queued = queuedOlderThan.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        using var c = Open();
        using var agg = c.CreateCommand();
        agg.CommandText = "SELECT State, COUNT(*), MIN(LastUpdated) FROM documents" + AuditQueryPredicates.StalledWhere + " GROUP BY State";
        P(agg, "@activeBefore", active); P(agg, "@queuedBefore", queued);

        using var sample = c.CreateCommand();
        // ASCENDING, unlike every other projection query in this file — the whole point of the sample is the rows
        // that "most recently updated first" buries.
        sample.CommandText = "SELECT DocumentId FROM documents" + AuditQueryPredicates.StalledWhere
            + " ORDER BY LastUpdated LIMIT " + StalledDocuments.SampleSize;
        P(sample, "@activeBefore", active); P(sample, "@queuedBefore", queued);

        return await ReadStalledAsync(agg, sample, ct);
    }

    /// <summary>Shared shaping of the stalled reads, so the two substrates cannot drift in how they fold the
    /// per-state groups into a total, an oldest and a sample.</summary>
    internal static async Task<StalledDocuments> ReadStalledAsync(DbCommand agg, DbCommand sample, CancellationToken ct)
    {
        var byState = new Dictionary<string, int>(StringComparer.Ordinal);
        DateTimeOffset? oldest = null;
        string? oldestState = null;
        using (var r = await agg.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var state = r.GetString(0);
                byState[state] = r.GetInt32(1);
                // MIN over a GROUP BY is never NULL for a group that exists (LastUpdated is NOT NULL), but parse
                // defensively rather than trust it: a malformed value must not take the stats endpoint down.
                if (!r.IsDBNull(2) && DateTimeOffset.TryParse(r.GetString(2), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var at) && (oldest is null || at < oldest))
                    (oldest, oldestState) = (at, state);
            }

        var ids = new List<string>(StalledDocuments.SampleSize);
        using (var r = await sample.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) ids.Add(r.GetString(0));

        return new StalledDocuments(byState.Values.Sum(), byState, oldest, oldestState, ids);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string documentId, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Seq,DocumentId,UnitId,Type,At,Actor,CorrelationId,Details,Hash FROM events WHERE DocumentId=@id ORDER BY Seq";
        P(cmd, "@id", documentId);
        var list = new List<AuditEvent>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(Read(r));
        return list;
    }

    /// <summary>Chain verification WITHOUT constructing the store: opens the file strictly READ-ONLY and runs no
    /// schema DDL (the constructor's WAL switch / column migration / view recreate would MUTATE the file). For
    /// tooling that must verify frozen evidence copies — the C9 seed verifies the R6-census chains this way.</summary>
    public static async Task<ChainVerification> VerifyChainReadOnlyAsync(string path, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await c.OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Seq,DocumentId,UnitId,Type,At,Actor,CorrelationId,Details,Hash FROM events ORDER BY Seq";
        var prev = Genesis;
        var headHash = Genesis;
        long count = 0, headSeq = 0, firstBroken = 0;
        var intact = true;
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var seq = r.GetInt64(0);
            var hash = r.GetString(8);
            if (intact && Sha256Hex(Canon(prev, seq, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7))) != hash)
            {
                intact = false;
                firstBroken = seq;
            }
            prev = hash;
            headHash = hash;
            headSeq = seq;
            count++;
        }
        return new ChainVerification(intact, count, headSeq, headHash, firstBroken);
    }

    public async Task<ChainVerification> VerifyChainAsync(CancellationToken ct = default)
    {
        using var c = Open();
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
            // Recompute with the EXACT append-time inputs — At is read raw (r.GetString(4)), the same string AppendAsync
            // hashed, NOT the round-tripped DateTimeOffset, so formatting can never cause a false mismatch.
            if (intact && Sha256Hex(Canon(prev, e.Seq, e.DocumentId, e.UnitId, e.Type, r.GetString(4), e.Actor, e.CorrelationId, e.Details)) != e.Hash)
            {
                intact = false;
                firstBroken = e.Seq;   // first break only; keep scanning so count/head describe the whole table
            }
            prev = e.Hash;             // chain on the STORED hash, mirroring AppendAsync's prev := LastHash()
            headHash = e.Hash;
            headSeq = e.Seq;
            count++;
        }
        return new ChainVerification(intact, count, headSeq, headHash, firstBroken);
    }

    /// <summary>Distinct documents with an event of this type since the instant — the capture-health denominator.
    /// At is stored as the round-trip "o" string, so lexical comparison IS chronological comparison.</summary>
    public async Task<int> CountDocumentsWithEventSinceAsync(string type, DateTimeOffset since, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT DocumentId) FROM events WHERE Type=@t AND At > @since";
        P(cmd, "@t", type); P(cmd, "@since", since.UtcDateTime.ToString("o"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Distinct documents review-filed since the instant — the review-lane label expectation.</summary>
    public async Task<int> CountReviewedThenFiledSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT DocumentId) FROM events WHERE Type='Filed' AND json_extract(Details,'$.disposition')='ReviewedThenFiled' AND At > @since";
        P(cmd, "@since", since.UtcDateTime.ToString("o"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Find the most recent document whose events mention <paramref name="needle"/> in Details (e.g. an original filename).</summary>
    public async Task<string?> FindDocumentIdByDetailAsync(string needle, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DocumentId FROM events WHERE Details LIKE @needle ORDER BY Seq DESC LIMIT 1";
        P(cmd, "@needle", "%" + needle + "%");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>C6: the authoritative name lookup — names live only in the projection now. LIKE over the display
    /// columns, newest first, wildcards in the needle escaped (a literal search, not a pattern language).</summary>
    public async Task<string?> FindDocumentIdByNameAsync(string needle, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT DocumentId FROM documents
            WHERE OriginalName LIKE @n ESCAPE '\' OR FileName LIKE @n ESCAPE '\' OR SourceRecordId LIKE @n ESCAPE '\'
            ORDER BY LastUpdated DESC LIMIT 1
            """;
        var escaped = needle.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        P(cmd, "@n", "%" + escaped + "%");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ---- helpers ----
    private const string ProjectionSelect =
        "SELECT DocumentId,State,OriginalName,FileName,ContentType,PatientListItemId,Disposition,Reasons,Sha256,DuplicateOf,FirstSeen,LastUpdated,EventCount,SourceRecordId,ReasonCodes,Confidence,DriveItemId,CorrectedFields,ContentTypeChange FROM documents";

    private static AuditEvent Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
        DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
        r.GetString(7), r.GetString(8));

    private static DocumentAudit ReadDoc(SqliteDataReader r) => new(
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

    private static void UpsertProjection(SqliteConnection c, SqliteTransaction tx, AuditEntry entry, string atStr, string details)
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
        cmd.CommandText = """
            INSERT INTO documents (DocumentId, State, OriginalName, FileName, ContentType, PatientListItemId, Disposition, Reasons, Sha256, DuplicateOf, SourceRecordId, ReasonCodes, Confidence, DriveItemId, CorrectedFields, ContentTypeChange, FirstSeen, LastUpdated, EventCount)
            -- No prior row to keep: an annotation as a document's FIRST event has no State to preserve, so fall back
            -- to its own type rather than inserting NULL into a NOT NULL column.
            VALUES (@id, COALESCE(@state, @type), @original, @file, @ct, @pid, @disp, @reasons, @sha, @dup, @srid, @reasonCodes, @confidence, @driveItemId, @correctedFields, @contentTypeChange, @at, @at, 1)
            ON CONFLICT(DocumentId) DO UPDATE SET
                -- ANNOTATION events pass @state = NULL and therefore leave State alone (AuditEventTypes.Advances),
                -- giving State the same keep-prior-on-null discipline every other column below already has. Must stay
                -- byte-for-byte equivalent to the Azure SQL store — the substrates are contract-tested for this.
                State=COALESCE(@state, State),
                OriginalName=COALESCE(@original, OriginalName),
                FileName=COALESCE(@file, FileName),
                ContentType=COALESCE(@ct, ContentType),
                PatientListItemId=COALESCE(@pid, PatientListItemId),
                Disposition=COALESCE(@disp, Disposition),
                Reasons=COALESCE(@reasons, Reasons),
                Sha256=COALESCE(@sha, Sha256),
                DuplicateOf=COALESCE(@dup, DuplicateOf),
                SourceRecordId=COALESCE(@srid, SourceRecordId),
                ReasonCodes=COALESCE(@reasonCodes, ReasonCodes),
                Confidence=COALESCE(@confidence, Confidence),
                DriveItemId=COALESCE(@driveItemId, DriveItemId),
                CorrectedFields=COALESCE(@correctedFields, CorrectedFields),
                ContentTypeChange=COALESCE(@contentTypeChange, ContentTypeChange),
                -- Annotations are neutral to LIFECYCLE TIMERS as well as to State: @lastAt is NULL for them, so the
                -- aged-input grace window and any other cutoff measured from LastUpdated are not silently regranted by
                -- a bookkeeping append. EventCount still increments — an annotation IS an event.
                LastUpdated=COALESCE(@lastAt, LastUpdated),
                EventCount=EventCount+1;
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
        cmd.ExecuteNonQuery();
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
                : S("error"); // a DeadLetter's error message doubles as the visible reason
            return (S("fileName"), S("contentType"), I("patientListItemId"), S("disposition"), S("original"), Reasons(), S("sha256"), S("duplicateOf"), S("sourceRecordId"), RawArr("reasonCodes"), D("confidence"), S("driveItemId"), RawArr("correctedFields"), RawObj("contentTypeChange"));
        }
        catch { return (null, null, null, null, null, null, null, null, null, null, null, null, null, null); }
    }

    /// <summary>Exact-duplicate lookup: the most recent non-discarded document with the same content hash.</summary>
    public async Task<DocumentAudit?> FindBySha256Async(string sha256, string excludeDocumentId, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + " WHERE Sha256=@h AND DocumentId<>@x AND State NOT IN ('Discarded','Superseded','Purged') ORDER BY LastUpdated DESC LIMIT 1"; // a superseded doc's file is deleted → never dedup against it; Purged likewise (post-window discard, artifacts released)
        P(cmd, "@h", sha256); P(cmd, "@x", excludeDocumentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    /// <summary>Exact-byte duplicate among ALREADY-FILED documents: a filed doc (not this one) with the same content
    /// hash. The filing-time guard that catches a re-send/duplicate even when the business key is null (no clean date/KVNR).</summary>
    public async Task<DocumentAudit?> FindFiledBySha256Async(string sha256, string excludeDocumentId, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + " WHERE Sha256=@h AND State='Filed' AND DocumentId<>@x ORDER BY LastUpdated DESC LIMIT 1";
        P(cmd, "@h", sha256); P(cmd, "@x", excludeDocumentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    /// <summary>Idempotency lookup for at-least-once source channels (the drain): the most recent non-discarded
    /// document with the same origin SourceRecordId. A re-delivery of the same source file matches here and is
    /// skipped; identical-but-distinct source records (same bytes, different SourceRecordId) are NOT collapsed.</summary>
    public async Task<DocumentAudit?> FindBySourceRecordIdAsync(string sourceRecordId, string excludeDocumentId, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + " WHERE SourceRecordId=@s AND DocumentId<>@x AND State NOT IN ('Discarded','Superseded','Purged') ORDER BY LastUpdated DESC LIMIT 1"; // superseded source-record → gone, don't re-match; Purged likewise
        P(cmd, "@s", sourceRecordId); P(cmd, "@x", excludeDocumentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    /// <summary>Business-key duplicate lookup: a FILED document whose canonical filename starts with
    /// "{date}_{type}_{kvnr}_" — i.e. same patient, content type, and document date.</summary>
    public async Task<DocumentAudit?> FindFiledByFileNamePrefixAsync(string prefix, string excludeDocumentId, CancellationToken ct = default)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ProjectionSelect + " WHERE State='Filed' AND FileName LIKE @p ESCAPE '\\' AND DocumentId<>@x ORDER BY LastUpdated DESC LIMIT 1";
        var escaped = prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"); // literal match — '_' is a LIKE wildcard
        P(cmd, "@p", escaped + "%"); P(cmd, "@x", excludeDocumentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadDoc(r) : null;
    }

    private static long NextSeq(SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(Seq),0)+1 FROM events";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string LastHash(SqliteConnection c, SqliteTransaction tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Hash FROM events ORDER BY Seq DESC LIMIT 1";
        return cmd.ExecuteScalar() as string ?? Genesis;
    }

    private static string Canon(string prev, long seq, string doc, string? unit, string type, string atStr, string actor, string? corr, string details) =>
        $"{prev}|{seq}|{doc}|{unit}|{type}|{atStr}|{actor}|{corr}|{details}";

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static void P(SqliteCommand cmd, string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public void Dispose() => _appendGate.Dispose();
}
