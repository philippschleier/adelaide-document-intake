using System.Data;
using System.Data.Common;

namespace Adelaide.Migrate;

/// <summary>
/// Two migration modes for the SQLite → Azure SQL cutover, matching decision D2 (migration plan):
///
/// <b>SeedProjectionAsync — THE cutover path (C9).</b> D2 decided the cloud chain starts EMPTY (the localhost
/// history's pre-C6 event payloads carry names the cloud ledger must never hold). What the cloud instance needs
/// from history is its DEDUP knowledge, and every dedup/idempotency lookup reads the <c>documents</c> projection —
/// so the seed copies projection rows only (union of the R6-census chains, insert-if-absent), never an event.
///
/// <b>MigrateAsync — the full verbatim copy, retained but NOT the plan of record.</b> Copies the <c>events</c>
/// ledger row-for-row with Seq/At/PrevHash/Hash preserved EXACTLY, so the chain re-verifies identically on the
/// target (the byte-identical storage contract the two stores share), plus the projection. Kept in case D2 is ever
/// revisited; NOT idempotent (refuses a populated target ledger — duplicate Seq would corrupt the chain).
///
/// Driver-agnostic on purpose: both paths operate on ADO.NET <see cref="DbConnection"/>, so the EXACT code that is
/// unit-tested SQLite→SQLite runs for real SQLite→Azure SQL.
/// </summary>
public static class Migrator
{
    public sealed record Result(long EventsCopied, long DocumentsCopied);

    public sealed record SeedResult(int UnionCount, int Inserted, int SkippedExisting);

    private sealed record RawEvent(long Seq, string DocumentId, string? UnitId, string Type, string At, string Actor,
        string? CorrelationId, string Details, string PrevHash, string Hash);

    private sealed record RawDoc(string DocumentId, string State, string? OriginalName, string? FileName, string? ContentType,
        int? PatientListItemId, string? Disposition, string? Reasons, string? Sha256, string? DuplicateOf, string? SourceRecordId,
        string? ReasonCodes, double? Confidence, string? DriveItemId, string? CorrectedFields, string? ContentTypeChange,
        string FirstSeen, string LastUpdated, int EventCount);

    public static async Task<Result> MigrateAsync(DbConnection source, DbConnection target, CancellationToken ct = default)
    {
        // Pre-flight: NEVER migrate into a populated ledger — duplicate Seq keys would corrupt the hash chain.
        var existing = Convert.ToInt64(await ScalarAsync(target, "SELECT COUNT(*) FROM events", ct));
        if (existing > 0)
            throw new InvalidOperationException(
                $"Target 'events' already has {existing} row(s). Refusing to migrate into a populated ledger — that would " +
                "duplicate Seq keys and corrupt the chain. Migrate only into a fresh ledger (drop+recreate per infra/sql/init.sql).");

        // Read the whole source into memory first (an audit history is modest), so the source reader is not held open
        // across slow cross-network inserts to the target.
        var events = await ReadEventsAsync(source, ct);
        var docs = await ReadDocsAsync(source, ct);

        await using var tx = await target.BeginTransactionAsync(ct);
        foreach (var e in events) await InsertEventAsync(target, tx, e, ct);
        foreach (var d in docs) await InsertDocAsync(target, tx, d, ct);
        await tx.CommitAsync(ct);

        return new Result(events.Count, docs.Count);
    }

    private static async Task<List<RawEvent>> ReadEventsAsync(DbConnection src, CancellationToken ct)
    {
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT Seq, DocumentId, UnitId, Type, At, Actor, CorrelationId, Details, PrevHash, Hash FROM events ORDER BY Seq";
        var list = new List<RawEvent>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RawEvent(
                r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.GetString(7), r.GetString(8), r.GetString(9)));
        return list;
    }

    private static readonly string[] DocColumns =
    {
        // The FULL projection column set — DriveItemId in particular is the durable filed-artifact locator the
        // compare view / reclassify / viewer resolve seeded docs by; the earlier 14-column copy silently dropped it.
        "DocumentId", "State", "OriginalName", "FileName", "ContentType", "PatientListItemId", "Disposition",
        "Reasons", "Sha256", "DuplicateOf", "SourceRecordId", "ReasonCodes", "Confidence", "DriveItemId",
        "CorrectedFields", "ContentTypeChange", "FirstSeen", "LastUpdated", "EventCount",
    };

    private static async Task<List<RawDoc>> ReadDocsAsync(DbConnection src, CancellationToken ct)
    {
        // Schema-tolerant read (verification finding): a frozen shadow DB from before the newer projection columns
        // must be readable WITHOUT relying on anything having ALTERed it first — sources are opened strictly
        // read-only, so probe which columns exist and select NULL for the missing ones.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var probe = src.CreateCommand())
        {
            probe.CommandText = "SELECT * FROM documents WHERE 1=0";
            await using var pr = await probe.ExecuteReaderAsync(System.Data.CommandBehavior.SchemaOnly, ct);
            for (var i = 0; i < pr.FieldCount; i++) present.Add(pr.GetName(i));
        }
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT " + string.Join(", ", DocColumns.Select(col => present.Contains(col) ? col : $"NULL AS {col}")) + " FROM documents";
        var list = new List<RawDoc>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new RawDoc(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetDouble(12),
                r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15),
                r.GetString(16), r.GetString(17), r.GetInt32(18)));
        return list;
    }

    /// <summary>C9 — the dedup seed (migration plan). Populates the target's <c>documents</c> PROJECTION ONLY —
    /// never a single event row — from the UNION of the given sources (first source wins per DocumentId; pass the
    /// main ledger first, then the shadow chains the R6 census identified). The cloud chain starts empty by
    /// decision D2, but its dedup/idempotency lookups all read the projection: with the seed in place, a re-upload
    /// of already-filed bytes flags as a duplicate (Sha256), a re-delivered staging file dedups (SourceRecordId),
    /// the business-key guard sees history (FileName), and the compare view can resolve the filed artifact
    /// (DriveItemId). INSERT-only-if-absent: a live cloud row is never touched, so seeding is safe at any point and
    /// idempotent. Names/paths land only in this updatable, erasable table — C6's boundary holds.</summary>
    /// <summary>Non-terminal source states transmute to <c>SeededHistory</c> at seed time (verification finding):
    /// a seeded row has no events, no inbox PDF, and often no driveItemId — copied verbatim, localhost's review
    /// tail (NeedsReview/DeadLetter/in-flight) would surface in the cloud dashboard as PINNED, unresolvable ghost
    /// work. <c>SeededHistory</c> is in <c>AuditQueryPredicates.Settled</c> (the shared predicate source), so it
    /// is invisible to active/duplicate/reconcile queries while every dedup lookup still matches it (they exclude
    /// only Discarded/Superseded). Terminal source states (Filed etc.) keep their state — Filed rows carry the
    /// driveItemId the compare view / reclassify need. MUST stay in sync with AuditQueryPredicates.Settled.</summary>
    private static readonly HashSet<string> SettledStates =
        new(StringComparer.Ordinal) { "Filed", "Discarded", "Superseded", "Segmented", "SeededHistory", "Purged" };

    /// <summary>Azure SQL column bounds (infra/sql/init.sql) — SQLite TEXT is unbounded, so an overlong historical
    /// value would abort the whole seed transaction server-side with an error that ECHOES the value (PHI). The
    /// preflight reports violations as DocumentId + column + length only, never the value.</summary>
    private static readonly (string Column, Func<RawDoc, string?> Get, int Max)[] Bounds =
    {
        ("DocumentId", d => d.DocumentId, 64), ("State", d => d.State, 64),
        ("OriginalName", d => d.OriginalName, 512), ("FileName", d => d.FileName, 512),
        ("ContentType", d => d.ContentType, 128), ("Disposition", d => d.Disposition, 64),
        ("Sha256", d => d.Sha256, 64), ("DuplicateOf", d => d.DuplicateOf, 64),
        ("SourceRecordId", d => d.SourceRecordId, 256), ("DriveItemId", d => d.DriveItemId, 256),
        ("FirstSeen", d => d.FirstSeen, 40), ("LastUpdated", d => d.LastUpdated, 40),
    };

    public sealed record Preflight(int UnionCount, IReadOnlyList<string> Violations);

    /// <summary>The exact timestamp shape both audit stores write: <c>DateTimeOffset.ToString("o")</c>, always
    /// UTC — 33 characters, 'T' at index 10, "+00:00" at the end.</summary>
    private static bool IsRoundTripUtc(string v) =>
        v.Length == 33 && v[10] == 'T' && v.EndsWith("+00:00", StringComparison.Ordinal);

    /// <summary>Read the union (read-only) and check every bounded column against the Azure SQL limits — runs in
    /// BOTH dry-run and execute, so an overlong value surfaces BEFORE any write, as an id + column + length line.
    ///
    /// It also checks the SHAPE of the two timestamp columns, which length alone cannot. FirstSeen/LastUpdated land
    /// in NVARCHAR(40) and are compared as STRINGS — <c>ORDER BY LastUpdated</c> on every dashboard list, and
    /// <c>LastUpdated &lt; @stalledBefore</c> in the stalled-document gauge. Those comparisons are sound only
    /// because every value shares one fixed-width skeleton, so only digits ever differ. That uniformity was
    /// DERIVED (both stores write "o") rather than ENFORCED, and this seed is the one path that copies timestamps
    /// in verbatim from an arbitrary source database. A 28-character "…Z" or a "+02:00" value is under 40, passes
    /// the length bound, and then sorts wrongly forever — silently, since nothing errors. Compare
    /// infra/sql/capture-init.sql, which pins an explicit BIN2 collation on exactly this reasoning: behaviour must
    /// not be a property of the input.</summary>
    public static async Task<Preflight> SeedPreflightAsync(IReadOnlyList<DbConnection> sources, CancellationToken ct = default)
    {
        var union = await ReadUnionAsync(sources, ct);
        var violations = new List<string>();
        foreach (var d in union.Values)
        {
            foreach (var (column, get, max) in Bounds)
                if (get(d) is { } v && v.Length > max)
                    violations.Add($"{d.DocumentId}: {column} length {v.Length} exceeds NVARCHAR({max})");

            // Shape only — never the value. A timestamp is not PHI, but this preflight's whole discipline is that
            // its output is safe to paste into a ticket, so it stays id + column + what is wrong.
            foreach (var (column, get) in new (string, Func<RawDoc, string?>)[]
                     { ("FirstSeen", d => d.FirstSeen), ("LastUpdated", d => d.LastUpdated) })
                if (get(d) is { } v && !IsRoundTripUtc(v))
                    violations.Add($"{d.DocumentId}: {column} is not round-trip UTC (length {v.Length}) — string ordering would be wrong");
        }
        return new Preflight(union.Count, violations);
    }

    private static async Task<Dictionary<string, RawDoc>> ReadUnionAsync(IReadOnlyList<DbConnection> sources, CancellationToken ct)
    {
        var union = new Dictionary<string, RawDoc>(StringComparer.Ordinal);
        foreach (var src in sources)
            foreach (var d in await ReadDocsAsync(src, ct))
                union.TryAdd(d.DocumentId, d); // first source wins — list order is precedence
        return union;
    }

    public static async Task<SeedResult> SeedProjectionAsync(IReadOnlyList<DbConnection> sources, DbConnection target, CancellationToken ct = default)
    {
        var union = await ReadUnionAsync(sources, ct);

        var inserted = 0;
        var skipped = 0;
        await using var tx = await target.BeginTransactionAsync(ct);
        foreach (var d in union.Values)
        {
            using (var check = target.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT 1 FROM documents WHERE DocumentId=@id";
                P(check, "@id", d.DocumentId);
                if (await check.ExecuteScalarAsync(ct) is not null) { skipped++; continue; } // live/already-seeded row — never clobber
            }
            await InsertDocAsync(target, tx, SettledStates.Contains(d.State) ? d : d with { State = "SeededHistory" }, ct);
            inserted++;
        }
        await tx.CommitAsync(ct);
        return new SeedResult(union.Count, inserted, skipped);
    }

    private static async Task InsertEventAsync(DbConnection tgt, DbTransaction tx, RawEvent e, CancellationToken ct)
    {
        using var cmd = tgt.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events (Seq, DocumentId, UnitId, Type, At, Actor, CorrelationId, Details, PrevHash, Hash) " +
                          "VALUES (@Seq,@DocumentId,@UnitId,@Type,@At,@Actor,@CorrelationId,@Details,@PrevHash,@Hash)";
        P(cmd, "@Seq", e.Seq, DbType.Int64); P(cmd, "@DocumentId", e.DocumentId); P(cmd, "@UnitId", e.UnitId); P(cmd, "@Type", e.Type);
        P(cmd, "@At", e.At); P(cmd, "@Actor", e.Actor); P(cmd, "@CorrelationId", e.CorrelationId); P(cmd, "@Details", e.Details);
        P(cmd, "@PrevHash", e.PrevHash); P(cmd, "@Hash", e.Hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertDocAsync(DbConnection tgt, DbTransaction tx, RawDoc d, CancellationToken ct)
    {
        using var cmd = tgt.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO documents (DocumentId, State, OriginalName, FileName, ContentType, PatientListItemId, " +
                          "Disposition, Reasons, Sha256, DuplicateOf, SourceRecordId, ReasonCodes, Confidence, DriveItemId, " +
                          "CorrectedFields, ContentTypeChange, FirstSeen, LastUpdated, EventCount) " +
                          "VALUES (@DocumentId,@State,@OriginalName,@FileName,@ContentType,@PatientListItemId,@Disposition,@Reasons,@Sha256,@DuplicateOf,@SourceRecordId,@ReasonCodes,@Confidence,@DriveItemId,@CorrectedFields,@ContentTypeChange,@FirstSeen,@LastUpdated,@EventCount)";
        P(cmd, "@DocumentId", d.DocumentId); P(cmd, "@State", d.State); P(cmd, "@OriginalName", d.OriginalName);
        P(cmd, "@FileName", d.FileName); P(cmd, "@ContentType", d.ContentType);
        P(cmd, "@PatientListItemId", d.PatientListItemId, DbType.Int32); P(cmd, "@Disposition", d.Disposition);
        P(cmd, "@Reasons", d.Reasons); P(cmd, "@Sha256", d.Sha256); P(cmd, "@DuplicateOf", d.DuplicateOf);
        P(cmd, "@SourceRecordId", d.SourceRecordId);
        P(cmd, "@ReasonCodes", d.ReasonCodes); P(cmd, "@Confidence", d.Confidence, DbType.Double);
        P(cmd, "@DriveItemId", d.DriveItemId); P(cmd, "@CorrectedFields", d.CorrectedFields); P(cmd, "@ContentTypeChange", d.ContentTypeChange);
        P(cmd, "@FirstSeen", d.FirstSeen); P(cmd, "@LastUpdated", d.LastUpdated);
        P(cmd, "@EventCount", d.EventCount, DbType.Int32);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(DbConnection c, string sql, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }

    // Driver-agnostic parameter add. DbType is set explicitly for the integer columns so a NULL still binds as the right
    // type on Azure SQL (a DBNull with no declared type defaults to nvarchar, which is fine for the text columns).
    private static void P(DbCommand cmd, string name, object? value, DbType? dbType = null)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        if (dbType.HasValue) p.DbType = dbType.Value;
        cmd.Parameters.Add(p);
    }
}
