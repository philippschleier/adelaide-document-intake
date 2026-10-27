using Adelaide.Core;
using Microsoft.Data.SqlClient;

namespace Adelaide.Audit;

/// <summary>
/// Azure SQL-backed capture store: the cloud substrate behind <see cref="ITranscriptStore"/> (migration plan C3,
/// decision D1). Same contract and semantics as the SQLite <see cref="TranscriptStore"/> — the invariants both
/// implementations must keep are pinned by TranscriptStoreContractTests, which runs this class against a real SQL
/// server in CI.
///
/// Layout: the <c>capture</c> schema holds document CONTENT (transcripts, artifacts, verbatim model-call bodies,
/// adjudicated values, attempt marks — erasable, window-aged) and the <c>measure</c> schema holds the non-PHI
/// measurement layer (corpus, corpus_fields, erasure_log — exempt from DeleteAsync and the window purge, plan C2),
/// both inside the SAME database as the audit ledger. Co-location is safe by the engine: <c>dbo.events</c> is an
/// append-only ledger table no DELETE here can touch. Timestamps are native <c>datetimeoffset(7)</c> (no hash chain
/// covers these rows, and Phase 6 analytics wants real types).
///
/// Keyless: <c>Authentication=Active Directory Default</c> (the App Service managed identity). Schema is provisioned
/// ONCE by an admin (infra/sql/capture-init.sql) — the app's identity has data read/write only, never DDL.
/// </summary>
public sealed class AzureSqlTranscriptStore(string connectionString) : ITranscriptStore
{
    // Transient Azure SQL error numbers worth a reconnect — the same conservative set as AzureSqlAuditStore
    // (duplicated deliberately: the two stores share no base class, and this list changes ~never).
    private static readonly HashSet<int> Transient =
        [49920, 49919, 49918, 41839, 41325, 41305, 41302, 41301, 40613, 40501, 40197, 10936, 10929, 10928, 10060, 10054, 10053, 4221, 4060, 1205, 233, 121, 64, 0];

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        const int maxTries = 4;
        for (int attempt = 1; ; attempt++)
        {
            var c = new SqlConnection(connectionString);
            try { await c.OpenAsync(ct); return c; }
            catch (SqlException ex) when (attempt < maxTries && ex.Errors.Cast<SqlError>().Any(e => Transient.Contains(e.Number)))
            {
                c.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt * attempt), ct);
            }
            catch { c.Dispose(); throw; }
        }
    }

    private static void P(SqlCommand cmd, string name, object? value)
    {
        // UTC-normalize timestamps for readback parity with the SQLite twin (which stores UtcDateTime 'o' strings):
        // datetimeoffset(7) would otherwise preserve the caller's offset and GetXxx would return offset-different
        // (instant-equal) values across substrates.
        if (value is DateTimeOffset dto) value = dto.ToUniversalTime();
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public async Task PutAsync(string documentId, string transcript, string sha256, DateTimeOffset at, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            MERGE capture.transcripts WITH (HOLDLOCK) AS t
            USING (SELECT @id AS DocumentId) AS s ON (t.DocumentId = s.DocumentId)
            WHEN MATCHED THEN UPDATE SET Transcript=@tx, TranscriptSha256=@sha, At=@at
            WHEN NOT MATCHED THEN INSERT (DocumentId, Transcript, TranscriptSha256, At) VALUES (@id, @tx, @sha, @at);
            """;
        P(cmd, "@id", documentId); P(cmd, "@tx", transcript); P(cmd, "@sha", sha256); P(cmd, "@at", at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string Transcript, string Sha256, DateTimeOffset At)?> GetAsync(string documentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Transcript, TranscriptSha256, At FROM capture.transcripts WHERE DocumentId=@id;";
        P(cmd, "@id", documentId);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task PutArtifactsAsync(string documentId, string? pageMarkdownJson, string? barcodePayloadsJson,
        string? checkboxEvidence, DateTimeOffset at, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            MERGE capture.transcript_artifacts WITH (HOLDLOCK) AS t
            USING (SELECT @id AS DocumentId) AS s ON (t.DocumentId = s.DocumentId)
            WHEN MATCHED THEN UPDATE SET PageMarkdown=@pm, BarcodePayloads=@bc, CheckboxEvidence=@cb, At=@at
            WHEN NOT MATCHED THEN INSERT (DocumentId, PageMarkdown, BarcodePayloads, CheckboxEvidence, At)
                VALUES (@id, @pm, @bc, @cb, @at);
            """;
        P(cmd, "@id", documentId); P(cmd, "@pm", pageMarkdownJson); P(cmd, "@bc", barcodePayloadsJson);
        P(cmd, "@cb", checkboxEvidence); P(cmd, "@at", at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Append-only, attempt-numbered per (DocumentId, CallSite) when the caller passes 0 — the UPDLOCK/
    /// HOLDLOCK on the MAX read makes the numbering race-safe even if the single-writer invariant ever slips.</summary>
    public async Task PutModelCallAsync(ModelCallRecord r, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO capture.model_calls (DocumentId, CallSite, Attempt, At, Deployment, ServedModel, SystemFingerprint,
                                             PromptHash, ReasoningEffort, RequestBody, RenderParams, SourcePdfSha256,
                                             ResponseBody, OutputSha256, OutputBytes, OutputRef,
                                             PromptTokens, CompletionTokens, ReasoningTokens, LatencyMs, RequestId, HttpStatus, Error)
            SELECT @id, @site,
                   CASE WHEN @att > 0 THEN @att
                        ELSE COALESCE((SELECT MAX(Attempt) FROM capture.model_calls WITH (UPDLOCK, HOLDLOCK)
                                       WHERE DocumentId=@id AND CallSite=@site), 0) + 1 END,
                   @at, @dep, @served, @fp, @ph, @re, @req, @render, @srcsha,
                   @resp, @outsha, @outbytes, @outref, @pt, @ct2, @rt, @lat, @reqid, @status, @err;
            """;
        P(cmd, "@id", r.Context.DocumentId);
        P(cmd, "@site", ModelCallSites.Wire(r.Context.Site));
        P(cmd, "@att", r.Context.Attempt);
        P(cmd, "@at", r.At);
        P(cmd, "@dep", r.Deployment); P(cmd, "@served", r.ServedModel); P(cmd, "@fp", r.SystemFingerprint);
        P(cmd, "@ph", r.Context.PromptHash); P(cmd, "@re", r.ReasoningEffort);
        P(cmd, "@req", r.RequestBody); P(cmd, "@render", r.Context.RenderParamsJson); P(cmd, "@srcsha", r.Context.SourcePdfSha256);
        P(cmd, "@resp", r.ResponseBody); P(cmd, "@outsha", r.OutputSha256); P(cmd, "@outbytes", r.OutputBytes); P(cmd, "@outref", r.OutputRef);
        P(cmd, "@pt", r.PromptTokens); P(cmd, "@ct2", r.CompletionTokens); P(cmd, "@rt", r.ReasoningTokens);
        P(cmd, "@lat", r.LatencyMs); P(cmd, "@reqid", r.RequestId); P(cmd, "@status", r.HttpStatus); P(cmd, "@err", r.Error);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ModelCallReadback>> GetModelCallsAsync(string documentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT Id, DocumentId, CallSite, Attempt, At, Deployment, ServedModel, SystemFingerprint, PromptHash,
                   ReasoningEffort, RequestBody, RenderParams, SourcePdfSha256, ResponseBody, OutputSha256, OutputBytes,
                   OutputRef, PromptTokens, CompletionTokens, ReasoningTokens, LatencyMs, RequestId, HttpStatus, Error
            FROM capture.model_calls WHERE DocumentId=@id ORDER BY Id;
            """;
        P(cmd, "@id", documentId);
        var outp = new List<ModelCallReadback>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            outp.Add(new ModelCallReadback(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                r.GetFieldValue<DateTimeOffset>(4),
                S(5), S(6), S(7), S(8), S(9), S(10), S(11), S(12), S(13), S(14),
                r.IsDBNull(15) ? null : r.GetInt64(15), S(16), I(17), I(18), I(19),
                r.GetInt64(20), S(21), I(22), S(23)));
        }
        return outp;
    }

    public async Task<int> ModelCallCountAsync(CancellationToken ct = default) =>
        await ScalarIntAsync("SELECT COUNT(*) FROM capture.model_calls;", ct);

    public async Task MarkAttemptStartAsync(string documentId, DateTimeOffset at, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO capture.attempt_starts (DocumentId, At) VALUES (@id, @at);";
        P(cmd, "@id", documentId); P(cmd, "@at", at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> AttemptStartsSinceAsync(string documentId, DateTimeOffset since, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM capture.attempt_starts WHERE DocumentId=@id AND At > @since;";
        P(cmd, "@id", documentId); P(cmd, "@since", since);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Label-preserving upsert: the update list deliberately excludes Label/LabelDomain/LabelAt — a
    /// re-processed document refreshes the measurement facts without un-labelling (same as the SQLite twin).</summary>
    public async Task PutCorpusAsync(CorpusRow row, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            MERGE measure.corpus WITH (HOLDLOCK) AS t
            USING (SELECT @id AS DocumentId) AS s ON (t.DocumentId = s.DocumentId)
            WHEN MATCHED THEN UPDATE SET
                At=@at, TranscriptSha256=@sha, Model=@model, ModelVersion=@ver, PromptHash=@ph,
                ReasoningEffort=@re, SystemFingerprint=@fp, Pages=@pages, Legibility=@leg, Barcodes=@bc,
                TranscriptChars=@chars, ModelDomain=@md, ModelContentType=@mct, ModelConfidence=@mc,
                DriveItemId=@drv, ContentSha256=@csha, ReviewState=@rs, ReasonCodes=@rc
            WHEN NOT MATCHED THEN INSERT
                (DocumentId, At, TranscriptSha256, Model, ModelVersion, PromptHash, ReasoningEffort, SystemFingerprint,
                 Pages, Legibility, Barcodes, TranscriptChars, ModelDomain, ModelContentType, ModelConfidence,
                 DriveItemId, ContentSha256, ReviewState, ReasonCodes)
                VALUES (@id, @at, @sha, @model, @ver, @ph, @re, @fp, @pages, @leg, @bc, @chars, @md, @mct, @mc,
                        @drv, @csha, @rs, @rc);
            """;
        P(cmd, "@id", row.DocumentId); P(cmd, "@at", row.At); P(cmd, "@sha", row.TranscriptSha256);
        P(cmd, "@model", row.Model); P(cmd, "@ver", row.ModelVersion); P(cmd, "@ph", row.PromptHash);
        P(cmd, "@re", row.ReasoningEffort); P(cmd, "@fp", row.SystemFingerprint);
        P(cmd, "@pages", row.Pages); P(cmd, "@leg", row.Legibility); P(cmd, "@bc", row.Barcodes);
        P(cmd, "@chars", row.TranscriptChars); P(cmd, "@md", row.ModelDomain); P(cmd, "@mct", row.ModelContentType);
        P(cmd, "@mc", row.ModelConfidence); P(cmd, "@drv", row.DriveItemId); P(cmd, "@csha", row.ContentSha256);
        P(cmd, "@rs", row.ReviewState); P(cmd, "@rc", row.ReasonCodesJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Outcome-preserving upsert (the Label-preserving pattern applied per field).</summary>
    public async Task PutCorpusFieldsAsync(string documentId, IEnumerable<CorpusFieldRow> rows, DateTimeOffset at, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                MERGE measure.corpus_fields WITH (HOLDLOCK) AS t
                USING (SELECT @id AS DocumentId, @f AS FieldName) AS s
                    ON (t.DocumentId = s.DocumentId AND t.FieldName = s.FieldName COLLATE Latin1_General_100_CI_AS)
                WHEN MATCHED THEN UPDATE SET Proposed=@p, Confidence=@conf, Source=@src, At=@at
                WHEN NOT MATCHED THEN INSERT (DocumentId, FieldName, Proposed, Confidence, Source, At)
                    VALUES (@id, @f, @p, @conf, @src, @at);
                """;
            P(cmd, "@id", documentId); P(cmd, "@f", row.FieldName); P(cmd, "@p", row.Proposed);
            P(cmd, "@conf", row.Confidence); P(cmd, "@src", row.Source); P(cmd, "@at", at);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task SetFieldOutcomesAsync(string documentId, IReadOnlyList<(string FieldName, string Outcome)> outcomes,
        string? remainingOutcome, DateTimeOffset at, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        foreach (var (field, outcome) in outcomes)
        {
            int updated;
            using (var upd = c.CreateCommand())
            {
                upd.Transaction = tx;
                // Case-insensitive field match (the review diff camelCases column names) — explicit COLLATE so the
                // behavior never depends on the database's default collation.
                upd.CommandText = """
                    UPDATE measure.corpus_fields SET Outcome=@o, OutcomeAt=@at
                    WHERE DocumentId=@id AND FieldName = @f COLLATE Latin1_General_100_CI_AS;
                    """;
                P(upd, "@o", outcome); P(upd, "@at", at); P(upd, "@id", documentId); P(upd, "@f", field);
                updated = await upd.ExecuteNonQueryAsync(ct);
            }
            if (updated == 0)
            {
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO measure.corpus_fields (DocumentId, FieldName, Proposed, Confidence, Source, Outcome, OutcomeAt, At)
                    VALUES (@id, @f, 0, NULL, NULL, @o, @at, @at);
                    """;
                P(ins, "@id", documentId); P(ins, "@f", field); P(ins, "@o", outcome); P(ins, "@at", at);
                await ins.ExecuteNonQueryAsync(ct);
            }
        }
        if (remainingOutcome is not null)
        {
            using var rest = c.CreateCommand();
            rest.Transaction = tx;
            rest.CommandText = "UPDATE measure.corpus_fields SET Outcome=@o, OutcomeAt=@at WHERE DocumentId=@id AND Outcome IS NULL;";
            P(rest, "@o", remainingOutcome); P(rest, "@at", at); P(rest, "@id", documentId);
            await rest.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CorpusFieldReadback>> GetCorpusFieldsAsync(string documentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT FieldName, Proposed, Confidence, Source, Outcome, OutcomeAt FROM measure.corpus_fields WHERE DocumentId=@id ORDER BY FieldName;";
        P(cmd, "@id", documentId);
        var outp = new List<CorpusFieldReadback>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            outp.Add(new CorpusFieldReadback(r.GetString(0), r.GetBoolean(1),
                r.IsDBNull(2) ? null : r.GetDouble(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5)));
        return outp;
    }

    public async Task<IReadOnlyDictionary<string, int>> CorpusFieldOutcomeCountsAsync(CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(Outcome, 'pending'), COUNT(*) FROM measure.corpus_fields GROUP BY COALESCE(Outcome, 'pending');";
        var outp = new Dictionary<string, int>(StringComparer.Ordinal);
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) outp[r.GetString(0)] = r.GetInt32(1);
        return outp;
    }

    /// <summary>Provenance-preserving upsert: a NULL incoming Provenance never clears a recorded one (a reconcile
    /// recovery replay must not erase what the inline submit observed).</summary>
    public async Task PutAdjudicationsAsync(string documentId, IEnumerable<FieldAdjudicationRow> rows, DateTimeOffset at, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                MERGE capture.field_adjudications WITH (HOLDLOCK) AS t
                USING (SELECT @id AS DocumentId, @f AS FieldName) AS s
                    ON (t.DocumentId = s.DocumentId AND t.FieldName = s.FieldName COLLATE Latin1_General_100_CI_AS)
                WHEN MATCHED THEN UPDATE SET ProposedValue=@pv, FinalValue=@fv, Signal=@sig,
                    Provenance=COALESCE(@prov, t.Provenance), At=@at
                WHEN NOT MATCHED THEN INSERT (DocumentId, FieldName, ProposedValue, FinalValue, Signal, Provenance, At)
                    VALUES (@id, @f, @pv, @fv, @sig, @prov, @at);
                """;
            P(cmd, "@id", documentId); P(cmd, "@f", row.FieldName); P(cmd, "@pv", row.ProposedValue);
            P(cmd, "@fv", row.FinalValue); P(cmd, "@sig", row.Signal); P(cmd, "@prov", row.Provenance); P(cmd, "@at", at);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FieldAdjudicationRow>> GetAdjudicationsAsync(string documentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT FieldName, ProposedValue, FinalValue, Signal, Provenance FROM capture.field_adjudications WHERE DocumentId=@id ORDER BY FieldName;";
        P(cmd, "@id", documentId);
        var outp = new List<FieldAdjudicationRow>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            outp.Add(new FieldAdjudicationRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return outp;
    }

    public async Task<bool> SetLabelAsync(string documentId, string? label, string? labelDomain, string? driveItemId,
        DateTimeOffset labelAt, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE measure.corpus SET Label=@l, LabelDomain=@d, LabelAt=@at,
                                      DriveItemId=COALESCE(@drv, DriveItemId)
            WHERE DocumentId=@id;
            """;
        P(cmd, "@id", documentId); P(cmd, "@l", label); P(cmd, "@d", labelDomain);
        P(cmd, "@drv", driveItemId); P(cmd, "@at", labelAt);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<(string DocumentId, string Label, string? DriveItemId)>> ScoreableSinceAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DocumentId, Label, DriveItemId FROM measure.corpus WHERE At > @since AND Label IS NOT NULL ORDER BY At;";
        P(cmd, "@since", since);
        var outp = new List<(string, string, string?)>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) outp.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return outp;
    }

    public async Task<IReadOnlyList<(string Label, int Count)>> LabelCountsAsync(CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Label, COUNT(*) n FROM measure.corpus WHERE Label IS NOT NULL GROUP BY Label ORDER BY n DESC;";
        var outp = new List<(string, int)>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) outp.Add((r.GetString(0), r.GetInt32(1)));
        return outp;
    }

    public async Task<int> CountAsync(CancellationToken ct = default) =>
        await ScalarIntAsync("SELECT COUNT(*) FROM capture.transcripts;", ct);

    public async Task<int> CorpusRowCountAsync(CancellationToken ct = default) =>
        await ScalarIntAsync("SELECT COUNT(*) FROM measure.corpus;", ct);

    public async Task<int> LabeledRowCountAsync(CancellationToken ct = default) =>
        await ScalarIntAsync("SELECT COUNT(*) FROM measure.corpus WHERE Label IS NOT NULL;", ct);

    public async Task<int> AdjudicationCountAsync(CancellationToken ct = default) =>
        await ScalarIntAsync("SELECT COUNT(*) FROM capture.field_adjudications;", ct);

    /// <summary>Epoch = the earliest capture activity of ANY kind (corpus, transcripts, model calls) — not just
    /// corpus rows, so a store where the corpus write path specifically regressed still anchors the
    /// is-capture-keeping-up denominator at the first successful write instead of resetting to "now" forever
    /// (verification finding: MIN(corpus.At) alone was blind in exactly the corpus-starvation case the stat
    /// exists to catch). Null only when NOTHING has ever been captured.</summary>
    public async Task<DateTimeOffset?> EpochAsync(CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(v) FROM (SELECT MIN(At) AS v FROM measure.corpus
                                UNION ALL SELECT MIN(At) FROM capture.transcripts
                                UNION ALL SELECT MIN(At) FROM capture.model_calls) u;
            """;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DateTimeOffset dto ? dto : null;
    }

    /// <summary>Content erasure — capture.* rows only; the measurement rows survive, and so do the attempt marks
    /// (the redrive bound's safety counter, not capture content — see ITranscriptStore / TranscriptStore).</summary>
    public async Task<bool> DeleteAsync(string documentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM capture.transcripts WHERE DocumentId=@id;
            DELETE FROM capture.transcript_artifacts WHERE DocumentId=@id;
            DELETE FROM capture.model_calls WHERE DocumentId=@id;
            DELETE FROM capture.field_adjudications WHERE DocumentId=@id;
            """;
        P(cmd, "@id", documentId);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return n > 0;
    }

    /// <summary>Full removal — measurement rows included; the erasure log deliberately survives (the record OF it).</summary>
    public async Task<bool> ForgetAsync(string documentId, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM capture.transcripts WHERE DocumentId=@id;
            DELETE FROM capture.transcript_artifacts WHERE DocumentId=@id;
            DELETE FROM capture.model_calls WHERE DocumentId=@id;
            DELETE FROM capture.field_adjudications WHERE DocumentId=@id;
            DELETE FROM capture.attempt_starts WHERE DocumentId=@id;
            DELETE FROM measure.corpus WHERE DocumentId=@id;
            DELETE FROM measure.corpus_fields WHERE DocumentId=@id;
            """;
        P(cmd, "@id", documentId);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return n > 0;
    }

    /// <summary>Window purge — CONTENT + attempt marks only, never measure.* (plan C2). Returns transcripts removed.</summary>
    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        int removed;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM capture.transcripts WHERE At < @cutoff;";
            P(cmd, "@cutoff", cutoff);
            removed = await cmd.ExecuteNonQueryAsync(ct);
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM capture.transcript_artifacts WHERE At < @cutoff;
                DELETE FROM capture.model_calls WHERE At < @cutoff;
                DELETE FROM capture.field_adjudications WHERE At < @cutoff;
                DELETE FROM capture.attempt_starts WHERE At < @cutoff;
                """;
            P(cmd, "@cutoff", cutoff);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return removed;
    }

    public async Task LogErasureAsync(string documentId, string scope, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO measure.erasure_log (DocumentId, Scope, Actor, At) VALUES (@id, @scope, @actor, @at);";
        P(cmd, "@id", documentId); P(cmd, "@scope", scope); P(cmd, "@actor", actor); P(cmd, "@at", at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ScalarIntAsync(string sql, CancellationToken ct)
    {
        using var c = await OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
