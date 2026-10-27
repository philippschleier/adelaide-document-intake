using Microsoft.Data.Sqlite;

namespace Adelaide.Audit;

/// <summary>Durable home for the identity oracle: for each document and identity field, whether the model's extracted
/// value agreed with the matched patient record, and — when it did not — a STRUCTURAL descriptor of the difference.
///
/// WHY A STORE RATHER THAN READING SIDECARS AT REPORT TIME: the review sidecars this is derived from are deleted at
/// filing. A report computed live would therefore shrink every day and could never be compared to itself. Persisting
/// the derived measurement freezes it while the source is still there — which is the whole point of doing this before
/// a model migration.
///
/// WHY NOT THE LEDGER: this is derived data, recomputable and correctable. The audit chain is append-only and must
/// hold only what actually happened. A backfill is an analysis artefact, not an event.
///
/// NOT PHI: rows carry a document id, a field name, a boolean, and a descriptor like "len 9-&gt;10" or "d-&gt;a@0"
/// (lengths and character classes, never characters). That is deliberate — it means this store has none of the
/// erasure burden the transcript store carries.
///
/// IDEMPOTENT: the primary key is (DocumentId, Field), so re-running a backfill upserts rather than accumulating.</summary>
public sealed class IdentityOracleStore
{
    /// <summary>Where a row came from. Backfilled rows are derived from a sidecar after the fact; live rows are
    /// recorded by the review path at the time. A reader must be able to tell them apart — the backfill can only see
    /// documents whose sidecar still exists, so it is not a random sample of the corpus.</summary>
    public const string SourceBackfill = "sidecar-backfill";
    public const string SourceLive = "review";

    private readonly string _conn;

    public IdentityOracleStore(string dbPath)
    {
        _conn = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS identity_oracle (
                DocumentId TEXT NOT NULL,
                Field      TEXT NOT NULL,
                Agreed     INTEGER NOT NULL,
                Shape      TEXT,
                Source     TEXT NOT NULL,
                At         TEXT NOT NULL,
                PRIMARY KEY (DocumentId, Field)
            );
            CREATE INDEX IF NOT EXISTS ix_identity_oracle_field ON identity_oracle(Field);
            """;
        cmd.ExecuteNonQuery();
    }

    public static string DefaultPathFor(string auditDbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(auditDbPath)) ?? ".", "adelaide-identity-oracle.db");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_conn);
        c.Open();
        return c;
    }

    /// <summary>Upsert one sample. Re-running the same backfill overwrites in place — running it twice cannot
    /// double-count, which is what makes the measurement safe to re-derive.</summary>
    public async Task PutAsync(string documentId, string field, bool agreed, string? shape, string source, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity_oracle (DocumentId, Field, Agreed, Shape, Source, At)
            VALUES ($id, $f, $a, $s, $src, $at)
            ON CONFLICT(DocumentId, Field) DO UPDATE SET
                Agreed=excluded.Agreed, Shape=excluded.Shape, Source=excluded.Source, At=excluded.At;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$f", field);
        cmd.Parameters.AddWithValue("$a", agreed ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", (object?)shape ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Per-field tally, optionally restricted to one source.</summary>
    public async Task<IReadOnlyList<(string Field, int Agreed, int Total, string Source)>> TallyAsync(string? source = null, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = source is null
            ? "SELECT Field, SUM(Agreed), COUNT(*), Source FROM identity_oracle GROUP BY Field, Source ORDER BY Field;"
            : "SELECT Field, SUM(Agreed), COUNT(*), Source FROM identity_oracle WHERE Source=$src GROUP BY Field, Source ORDER BY Field;";
        if (source is not null) cmd.Parameters.AddWithValue("$src", source);
        var outp = new List<(string, int, int, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) outp.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)));
        return outp;
    }

    /// <summary>The disagreement shapes, most common first — the actionable half: it says HOW the model misreads.</summary>
    public async Task<IReadOnlyList<(string Field, string Shape, int Count)>> ShapesAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Field, Shape, COUNT(*) n FROM identity_oracle WHERE Agreed=0 AND Shape IS NOT NULL GROUP BY Field, Shape ORDER BY n DESC;";
        var outp = new List<(string, string, int)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) outp.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return outp;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM identity_oracle;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
