using Microsoft.Data.Sqlite;

namespace Adelaide.Audit;

/// <summary>One measurement row per document — NON-PHI by construction, so analytics can run outside the patient-data
/// boundary and the row survives an Art. 17 erasure of the document's text. <see cref="Label"/> is the human's
/// adjudicated content type and arrives later, at review; a row with a Label is a SCOREABLE row.</summary>
public sealed record CorpusRow(
    string DocumentId,
    DateTimeOffset At,
    string? TranscriptSha256 = null,
    string? Model = null,
    string? ModelVersion = null,
    string? PromptHash = null,
    string? ReasoningEffort = null,
    string? SystemFingerprint = null,
    int? Pages = null,
    double? Legibility = null,
    int? Barcodes = null,
    int? TranscriptChars = null,
    string? ModelDomain = null,
    string? ModelContentType = null,
    double? ModelConfidence = null,
    string? DriveItemId = null,
    string? ContentSha256 = null,
    string? ReviewState = null,
    string? ReasonCodesJson = null);

/// <summary>The deletable transcript store: document id → transcript text, deliberately OUTSIDE the audit chain.
///
/// WHY A SEPARATE DATABASE FILE, not a table in the audit DB:
///   • Erasure. Wiping the whole corpus is deleting one file. A table inside the ledger makes the same operation a
///     DROP/DELETE against the database that also holds the tamper-evident chain — the one file that must never be
///     operated on casually. Keeping them apart means a retention mistake can never cost the audit trail.
///   • Different lifetimes. The ledger is append-only and permanent; this store is deliberately short-lived and
///     purged on a window. Coupling them in one file couples their backups, their restores and their retention.
///   • Different substrates. The ledger moves to Azure SQL in production (SqliteAuditStore vs AzureSqlAuditStore);
///     the transcripts need not follow it, and shouldn't have to.
///   • The chain is untouched by design: no Seq, no PrevHash, no Hash. Deleting from here cannot invalidate anything.
///
/// RESIDENCY: defaults to a sibling file of the audit DB, so it lands on the same volume/mount and inherits the same
/// tenant and residency boundary as the PHI already there. Override with ADELAIDE_TRANSCRIPT_DB.
///
/// The stored text IS PHI. Nothing here logs a transcript, and callers must not either.</summary>
public sealed class TranscriptStore : ITranscriptStore
{
    private readonly string _conn;
    private readonly string _dbPath;

    public TranscriptStore(string dbPath)
    {
        _dbPath = dbPath;
        _conn = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var c = Open();
        using var cmd = c.CreateCommand();
        // TWO tables, split by PHI class rather than by arrival time:
        //
        //   transcripts — everything derived from the document's CONTENT: the transcript, the per-page slices, the
        //                 decoded barcode payloads (a Muster 12 BFB payload is the whole prescription), the checkbox
        //                 evidence. All PHI, all erasable together.
        //   corpus      — the measurement row: labels, model provenance, scores, references. Deliberately NON-PHI, so
        //                 analytics can run entirely outside the patient-data boundary, and so a document's
        //                 measurement row SURVIVES an Art. 17 erasure of its text. Erasing the content must not
        //                 destroy the record that the document existed and how the model performed on it.
        //
        // Arrival time is handled by upsert, not by table shape: transcription writes what it knows, review fills the
        // label in later. One row per document in each table either way.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS transcripts (
                DocumentId       TEXT PRIMARY KEY,
                Transcript       TEXT NOT NULL,
                TranscriptSha256 TEXT NOT NULL,
                At               TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_transcripts_at ON transcripts(At);

            CREATE TABLE IF NOT EXISTS corpus (
                DocumentId       TEXT PRIMARY KEY,
                At               TEXT NOT NULL,   -- transcription time; the retention clock for this row
                TranscriptSha256 TEXT,            -- joins to transcripts AND to the ledger's Transcribed stamp

                -- what produced the transcript
                Model            TEXT,
                ModelVersion     TEXT,            -- the SERVED id (gpt-4o-2024-11-20), not the deployment name
                PromptHash       TEXT,
                ReasoningEffort  TEXT,            -- null on non-reasoning models; a behavioural parameter either way
                SystemFingerprint TEXT,

                -- transcription outcome
                Pages            INTEGER,
                Legibility       REAL,            -- self-reported and uncalibrated; recorded, never used as evidence
                Barcodes         INTEGER,
                TranscriptChars  INTEGER,

                -- what the MODEL said
                ModelDomain      TEXT,
                ModelContentType TEXT,
                ModelConfidence  REAL,

                -- what the HUMAN said (arrives at review, hence nullable; Label IS NOT NULL = scoreable)
                Label            TEXT,
                LabelDomain      TEXT,
                LabelAt          TEXT,

                -- how to get the document back: filed PDFs live in SharePoint, so a reference is enough
                DriveItemId      TEXT,
                ContentSha256    TEXT,

                -- gate outcome
                ReviewState      TEXT,
                ReasonCodes      TEXT             -- JSON array of codes
            );
            CREATE INDEX IF NOT EXISTS ix_corpus_at ON corpus(At);
            CREATE INDEX IF NOT EXISTS ix_corpus_label ON corpus(Label);

            -- Model INPUTS that were previously discarded, so a decoder or Document-Intelligence upgrade has something
            -- to measure against. PHI: a BFB barcode payload is the prescription verbatim, page slices are the text,
            -- and checkbox labels are lifted from the document. Kept beside the transcript and erased with it.
            CREATE TABLE IF NOT EXISTS transcript_artifacts (
                DocumentId       TEXT PRIMARY KEY,
                PageMarkdown     TEXT,            -- JSON array, one entry per page; the segmentation slice primitive
                BarcodePayloads  TEXT,            -- JSON array of {symbology, rawText, checksumValid}
                CheckboxEvidence TEXT,            -- the "[X] label" lines fed to the extractor (DI marks or BFB)
                At               TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_artifacts_at ON transcript_artifacts(At);

            -- THE MODEL BOUNDARY (docs/CAPTURE.md): one row per model-call attempt, request and response verbatim
            -- (image payloads elided to sha256+size markers — see Adelaide.Core.ModelCallElision). PHI: bodies embed
            -- document text, so this lives on the PHI side and is covered by DeleteAsync/ForgetAsync and the window
            -- purge. Failures are rows too (HttpStatus + Error) — a call that happened is a call that is recorded.
            CREATE TABLE IF NOT EXISTS model_calls (
                Id               INTEGER PRIMARY KEY AUTOINCREMENT,
                DocumentId       TEXT NOT NULL,
                CallSite         TEXT NOT NULL,   -- kebab-case ModelCallSite (transcription | domain | classify | …)
                Attempt          INTEGER NOT NULL,-- 1-based per (DocumentId, CallSite); assigned here when the caller passes 0
                At               TEXT NOT NULL,
                Deployment       TEXT,            -- what we asked for (DI: the prebuilt model id)
                ServedModel      TEXT,            -- what the API says ran (DI: model@api-version)
                SystemFingerprint TEXT,
                PromptHash       TEXT,            -- the site's template hash; null for DI (no prompt)
                ReasoningEffort  TEXT,            -- as SENT for this call; null when the model has no such parameter
                RequestBody      TEXT,            -- verbatim wire body, base64 payloads elided to {sha256,bytes}
                RenderParams     TEXT,            -- L1 only: the render parameter VALUES (dpi/maxPages/maxWidth/…)
                SourcePdfSha256  TEXT,            -- L1 only: joins to the ledger's Received.sha256
                ResponseBody     TEXT,            -- verbatim; D1's binary output is captured by hash+size instead
                OutputSha256     TEXT,            -- D1: sha256 of the returned searchable PDF (the filed artifact)
                OutputBytes      INTEGER,
                OutputRef        TEXT,            -- where the output bytes live ('filed-artifact' → join Filed.driveItemId)
                PromptTokens     INTEGER,
                CompletionTokens INTEGER,
                ReasoningTokens  INTEGER,
                LatencyMs        INTEGER,
                RequestId        TEXT,            -- gateway apim-request-id when present
                HttpStatus       INTEGER,
                Error            TEXT             -- null on clean success
            );
            CREATE INDEX IF NOT EXISTS ix_model_calls_doc ON model_calls(DocumentId);
            CREATE INDEX IF NOT EXISTS ix_model_calls_at ON model_calls(At);

            -- Per-field measurement (NON-PHI side of the corpus): name, whether the model proposed a value, the
            -- confidence the pipeline recorded (some are code-assigned, not model-emitted — Source says which), and
            -- what became of it. Outcome NULL = awaiting adjudication; MATCH/CORRECTION/FILL from review;
            -- AUTOFILED = filed on the model's own answer, unlabeled BY DESIGN (never NULL-pending, so "unlabeled
            -- by design" can never masquerade as "unlabeled by bug"). This is the calibration table: (Confidence,
            -- Outcome) pairs per field — the pair that previously never co-persisted anywhere.
            CREATE TABLE IF NOT EXISTS corpus_fields (
                DocumentId TEXT NOT NULL,
                FieldName  TEXT NOT NULL,
                Proposed   INTEGER NOT NULL,   -- 1 = the model proposed a non-empty value
                Confidence REAL,               -- as recorded on ExtractedData.Fields
                Source     TEXT,               -- vision | barcode | di | vision/repaired | review | …
                Outcome    TEXT,               -- NULL until adjudicated; MATCH | CORRECTION | FILL | AUTOFILED
                OutcomeAt  TEXT,
                At         TEXT NOT NULL,      -- extraction time; the purge clock for this row
                PRIMARY KEY (DocumentId, FieldName)
            );
            CREATE INDEX IF NOT EXISTS ix_corpus_fields_at ON corpus_fields(At);

            -- Adjudicated value pairs (PHI side): what the model proposed vs what the human filed, per field —
            -- unchanged pairs included (they are the MATCH class calibration needs), with the structural signal and
            -- (once the client ships it) the OBSERVED provenance of the final value (prefill/patient/typed).
            -- The durable labeled-pairs asset for any future retrieval loop; erased with the document's content.
            CREATE TABLE IF NOT EXISTS field_adjudications (
                DocumentId    TEXT NOT NULL,
                FieldName     TEXT NOT NULL,
                ProposedValue TEXT,            -- NULL = the model proposed nothing (a FILL)
                FinalValue    TEXT,            -- what the human filed (NULL = left empty)
                Signal        TEXT,            -- match | correction | fill (the event's fieldSignals vocabulary)
                Provenance    TEXT,            -- prefill | patient | typed | unchanged; NULL until the client sends it
                At            TEXT NOT NULL,
                PRIMARY KEY (DocumentId, FieldName)
            );
            CREATE INDEX IF NOT EXISTS ix_field_adjudications_at ON field_adjudications(At);

            -- Operational, non-PHI: one row per worker PICKUP of a document. The redrive bound reads these — a
            -- document is parked as DeadLetter only when ATTEMPTS since its last real ingest hit the limit, so a
            -- backlog document merely re-driven by routine restarts (never picked up) can never be mis-parked as
            -- a crasher. Lifecycle: aged by the window purge; removed by ForgetAsync (document-terminal) but NOT by
            -- DeleteAsync — a content erasure must not reset a still-processable document's crash budget.
            CREATE TABLE IF NOT EXISTS attempt_starts (
                DocumentId TEXT NOT NULL,
                At         TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_attempt_starts_doc ON attempt_starts(DocumentId, At);

            -- The audited record of per-document erasures (scope 'content'|'full'): written by the erasure surface
            -- AFTER the store operation; never removed by DeleteAsync/ForgetAsync/window purge — the record OF them.
            CREATE TABLE IF NOT EXISTS erasure_log (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                DocumentId TEXT NOT NULL,
                Scope      TEXT NOT NULL,
                Actor      TEXT NOT NULL,
                At         TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The default location: beside the audit DB, so retained text shares its volume and residency.</summary>
    public static string DefaultPathFor(string auditDbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(auditDbPath)) ?? ".", "adelaide-transcripts.db");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_conn);
        c.Open();
        return c;
    }

    /// <summary>Store (or replace) one transcript. Idempotent by document id, so a reprocessed document overwrites
    /// rather than accumulating copies. <paramref name="at"/> is the retention clock for this row.</summary>
    public async Task PutAsync(string documentId, string transcript, string sha256, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transcripts (DocumentId, Transcript, TranscriptSha256, At) VALUES ($id, $tx, $sha, $at)
            ON CONFLICT(DocumentId) DO UPDATE SET Transcript=excluded.Transcript,
                                                  TranscriptSha256=excluded.TranscriptSha256,
                                                  At=excluded.At;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$tx", transcript);
        cmd.Parameters.AddWithValue("$sha", sha256);
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The retained transcript and the hash it was stored under, or null if nothing is retained for this id
    /// (never retained, or already purged/erased — the store does not distinguish, by design).</summary>
    public async Task<(string Transcript, string Sha256, DateTimeOffset At)?> GetAsync(string documentId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Transcript, TranscriptSha256, At FROM transcripts WHERE DocumentId=$id;";
        cmd.Parameters.AddWithValue("$id", documentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    /// <summary>Model INPUTS that used to be discarded: the per-page slices, the decoded barcode payloads and the
    /// checkbox evidence. Without these a decoder or Document-Intelligence upgrade has nothing to compare against —
    /// the same gap the transcripts had, one layer earlier. PHI, so they live beside the transcript and die with it.</summary>
    public async Task PutArtifactsAsync(string documentId, string? pageMarkdownJson, string? barcodePayloadsJson,
        string? checkboxEvidence, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transcript_artifacts (DocumentId, PageMarkdown, BarcodePayloads, CheckboxEvidence, At)
            VALUES ($id, $pm, $bc, $cb, $at)
            ON CONFLICT(DocumentId) DO UPDATE SET PageMarkdown=excluded.PageMarkdown,
                                                  BarcodePayloads=excluded.BarcodePayloads,
                                                  CheckboxEvidence=excluded.CheckboxEvidence, At=excluded.At;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$pm", (object?)pageMarkdownJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bc", (object?)barcodePayloadsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cb", (object?)checkboxEvidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>One model-call attempt, exactly as the wrapper saw it. Attempt 0 ⇒ assign the next per-(document,
    /// site) ordinal here — a re-processed document appends attempt 2, 3, … rather than overwriting attempt 1,
    /// because every attempt is a call that really happened. Append-only by design (no upsert).</summary>
    public async Task PutModelCallAsync(Adelaide.Core.ModelCallRecord r, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_calls (DocumentId, CallSite, Attempt, At, Deployment, ServedModel, SystemFingerprint,
                                     PromptHash, ReasoningEffort, RequestBody, RenderParams, SourcePdfSha256,
                                     ResponseBody, OutputSha256, OutputBytes, OutputRef,
                                     PromptTokens, CompletionTokens, ReasoningTokens, LatencyMs, RequestId, HttpStatus, Error)
            VALUES ($id, $site,
                    CASE WHEN $att > 0 THEN $att
                         ELSE (SELECT COALESCE(MAX(Attempt), 0) + 1 FROM model_calls WHERE DocumentId=$id AND CallSite=$site) END,
                    $at, $dep, $served, $fp, $ph, $re, $req, $render, $srcsha,
                    $resp, $outsha, $outbytes, $outref, $pt, $ct2, $rt, $lat, $reqid, $status, $err);
            """;
        object? N(object? v) => v ?? DBNull.Value;
        cmd.Parameters.AddWithValue("$id", r.Context.DocumentId);
        cmd.Parameters.AddWithValue("$site", Adelaide.Core.ModelCallSites.Wire(r.Context.Site));
        cmd.Parameters.AddWithValue("$att", r.Context.Attempt);
        cmd.Parameters.AddWithValue("$at", r.At.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$dep", N(r.Deployment));
        cmd.Parameters.AddWithValue("$served", N(r.ServedModel));
        cmd.Parameters.AddWithValue("$fp", N(r.SystemFingerprint));
        cmd.Parameters.AddWithValue("$ph", N(r.Context.PromptHash));
        cmd.Parameters.AddWithValue("$re", N(r.ReasoningEffort));
        cmd.Parameters.AddWithValue("$req", N(r.RequestBody));
        cmd.Parameters.AddWithValue("$render", N(r.Context.RenderParamsJson));
        cmd.Parameters.AddWithValue("$srcsha", N(r.Context.SourcePdfSha256));
        cmd.Parameters.AddWithValue("$resp", N(r.ResponseBody));
        cmd.Parameters.AddWithValue("$outsha", N(r.OutputSha256));
        cmd.Parameters.AddWithValue("$outbytes", N(r.OutputBytes));
        cmd.Parameters.AddWithValue("$outref", N(r.OutputRef));
        cmd.Parameters.AddWithValue("$pt", N(r.PromptTokens));
        cmd.Parameters.AddWithValue("$ct2", N(r.CompletionTokens));
        cmd.Parameters.AddWithValue("$rt", N(r.ReasoningTokens));
        cmd.Parameters.AddWithValue("$lat", r.LatencyMs);
        cmd.Parameters.AddWithValue("$reqid", N(r.RequestId));
        cmd.Parameters.AddWithValue("$status", N(r.HttpStatus));
        cmd.Parameters.AddWithValue("$err", N(r.Error));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A document's captured calls in call order.</summary>
    public async Task<IReadOnlyList<ModelCallReadback>> GetModelCallsAsync(string documentId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT Id, DocumentId, CallSite, Attempt, At, Deployment, ServedModel, SystemFingerprint, PromptHash,
                   ReasoningEffort, RequestBody, RenderParams, SourcePdfSha256, ResponseBody, OutputSha256, OutputBytes,
                   OutputRef, PromptTokens, CompletionTokens, ReasoningTokens, LatencyMs, RequestId, HttpStatus, Error
            FROM model_calls WHERE DocumentId=$id ORDER BY Id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        var outp = new List<ModelCallReadback>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            int? I(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            outp.Add(new ModelCallReadback(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                DateTimeOffset.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                S(5), S(6), S(7), S(8), S(9), S(10), S(11), S(12), S(13), S(14),
                r.IsDBNull(15) ? null : r.GetInt64(15), S(16), I(17), I(18), I(19),
                r.IsDBNull(20) ? 0 : r.GetInt64(20), S(21), I(22), S(23)));
        }
        return outp;
    }

    /// <summary>Record that a worker PICKED UP this document — one row per processing attempt, appended before
    /// the pipeline runs so a process-killing document still leaves durable evidence that an attempt began.
    /// The UploadProcessor's redrive bound counts these, never mere re-drives.</summary>
    public async Task MarkAttemptStartAsync(string documentId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO attempt_starts (DocumentId, At) VALUES ($id, $at);";
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Attempt starts recorded strictly after <paramref name="since"/> — "processing actually began"
    /// counts for the redrive bound (the anchor is the document's last real ingest, so a manual retry resets
    /// the budget here exactly as it does for the ledger-side re-drive count).</summary>
    public async Task<int> AttemptStartsSinceAsync(string documentId, DateTimeOffset since, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM attempt_starts WHERE DocumentId=$id AND At > $since;";
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Total captured call rows (capture-health stats; never the content).</summary>
    public async Task<int> ModelCallCountAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM model_calls;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The measurement row, written at transcription time with everything known then. NON-PHI — this is what
    /// analytics reads, and it deliberately survives an erasure of the document's text.</summary>
    public async Task PutCorpusAsync(CorpusRow row, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO corpus (DocumentId, At, TranscriptSha256, Model, ModelVersion, PromptHash, ReasoningEffort,
                                SystemFingerprint, Pages, Legibility, Barcodes, TranscriptChars,
                                ModelDomain, ModelContentType, ModelConfidence, DriveItemId, ContentSha256,
                                ReviewState, ReasonCodes)
            VALUES ($id,$at,$sha,$model,$ver,$ph,$re,$fp,$pages,$leg,$bc,$chars,$md,$mct,$mc,$drv,$csha,$rs,$rc)
            ON CONFLICT(DocumentId) DO UPDATE SET
                At=excluded.At, TranscriptSha256=excluded.TranscriptSha256, Model=excluded.Model,
                ModelVersion=excluded.ModelVersion, PromptHash=excluded.PromptHash,
                ReasoningEffort=excluded.ReasoningEffort, SystemFingerprint=excluded.SystemFingerprint,
                Pages=excluded.Pages, Legibility=excluded.Legibility, Barcodes=excluded.Barcodes,
                TranscriptChars=excluded.TranscriptChars, ModelDomain=excluded.ModelDomain,
                ModelContentType=excluded.ModelContentType, ModelConfidence=excluded.ModelConfidence,
                DriveItemId=excluded.DriveItemId, ContentSha256=excluded.ContentSha256,
                ReviewState=excluded.ReviewState, ReasonCodes=excluded.ReasonCodes;
            """;
        object? N(object? v) => v ?? DBNull.Value;
        cmd.Parameters.AddWithValue("$id", row.DocumentId);
        cmd.Parameters.AddWithValue("$at", row.At.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$sha", N(row.TranscriptSha256));
        cmd.Parameters.AddWithValue("$model", N(row.Model));
        cmd.Parameters.AddWithValue("$ver", N(row.ModelVersion));
        cmd.Parameters.AddWithValue("$ph", N(row.PromptHash));
        cmd.Parameters.AddWithValue("$re", N(row.ReasoningEffort));
        cmd.Parameters.AddWithValue("$fp", N(row.SystemFingerprint));
        cmd.Parameters.AddWithValue("$pages", N(row.Pages));
        cmd.Parameters.AddWithValue("$leg", N(row.Legibility));
        cmd.Parameters.AddWithValue("$bc", N(row.Barcodes));
        cmd.Parameters.AddWithValue("$chars", N(row.TranscriptChars));
        cmd.Parameters.AddWithValue("$md", N(row.ModelDomain));
        cmd.Parameters.AddWithValue("$mct", N(row.ModelContentType));
        cmd.Parameters.AddWithValue("$mc", N(row.ModelConfidence));
        cmd.Parameters.AddWithValue("$drv", N(row.DriveItemId));
        cmd.Parameters.AddWithValue("$csha", N(row.ContentSha256));
        cmd.Parameters.AddWithValue("$rs", N(row.ReviewState));
        cmd.Parameters.AddWithValue("$rc", N(row.ReasonCodesJson));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Upsert preserves an existing Outcome/OutcomeAt (the Label-preserving pattern): a re-processed
    /// document refreshes the proposal facts without un-adjudicating.</summary>
    public async Task PutCorpusFieldsAsync(string documentId, IEnumerable<CorpusFieldRow> rows, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await c.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO corpus_fields (DocumentId, FieldName, Proposed, Confidence, Source, At)
                VALUES ($id, $f, $p, $conf, $src, $at)
                ON CONFLICT(DocumentId, FieldName) DO UPDATE SET
                    Proposed=excluded.Proposed, Confidence=excluded.Confidence, Source=excluded.Source, At=excluded.At;
                """;
            cmd.Parameters.AddWithValue("$id", documentId);
            cmd.Parameters.AddWithValue("$f", row.FieldName);
            cmd.Parameters.AddWithValue("$p", row.Proposed ? 1 : 0);
            cmd.Parameters.AddWithValue("$conf", (object?)row.Confidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", (object?)row.Source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Adjudicate a document's field rows. <paramref name="outcomes"/> names specific fields (matched
    /// case-insensitively, because the review diff camelCases column names); a named field with no row — a human-
    /// filled field the model never proposed — is INSERTED with Proposed=0 (the FILL class needs a row too). Then
    /// <paramref name="remainingOutcome"/> (when given) stamps every still-NULL row of the document — "MATCH" for a
    /// review (every un-changed proposal was accepted), "AUTOFILED" for the auto-file lane.</summary>
    public async Task SetFieldOutcomesAsync(string documentId, IReadOnlyList<(string FieldName, string Outcome)> outcomes,
        string? remainingOutcome, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await c.BeginTransactionAsync(ct);
        var atStr = at.UtcDateTime.ToString("O");
        foreach (var (field, outcome) in outcomes)
        {
            int updated;
            await using (var upd = c.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE corpus_fields SET Outcome=$o, OutcomeAt=$at WHERE DocumentId=$id AND FieldName=$f COLLATE NOCASE;";
                upd.Parameters.AddWithValue("$o", outcome);
                upd.Parameters.AddWithValue("$at", atStr);
                upd.Parameters.AddWithValue("$id", documentId);
                upd.Parameters.AddWithValue("$f", field);
                updated = await upd.ExecuteNonQueryAsync(ct);
            }
            if (updated == 0)
            {
                await using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO corpus_fields (DocumentId, FieldName, Proposed, Confidence, Source, Outcome, OutcomeAt, At)
                    VALUES ($id, $f, 0, NULL, NULL, $o, $at, $at);
                    """;
                ins.Parameters.AddWithValue("$id", documentId);
                ins.Parameters.AddWithValue("$f", field);
                ins.Parameters.AddWithValue("$o", outcome);
                ins.Parameters.AddWithValue("$at", atStr);
                await ins.ExecuteNonQueryAsync(ct);
            }
        }
        if (remainingOutcome is not null)
        {
            await using var rest = c.CreateCommand();
            rest.Transaction = tx;
            rest.CommandText = "UPDATE corpus_fields SET Outcome=$o, OutcomeAt=$at WHERE DocumentId=$id AND Outcome IS NULL;";
            rest.Parameters.AddWithValue("$o", remainingOutcome);
            rest.Parameters.AddWithValue("$at", atStr);
            rest.Parameters.AddWithValue("$id", documentId);
            await rest.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CorpusFieldReadback>> GetCorpusFieldsAsync(string documentId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT FieldName, Proposed, Confidence, Source, Outcome, OutcomeAt FROM corpus_fields WHERE DocumentId=$id ORDER BY FieldName;";
        cmd.Parameters.AddWithValue("$id", documentId);
        var outp = new List<CorpusFieldReadback>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            outp.Add(new CorpusFieldReadback(r.GetString(0), r.GetInt32(1) != 0,
                r.IsDBNull(2) ? null : r.GetDouble(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return outp;
    }

    /// <summary>Outcome histogram over all corpus_fields rows — the /api/stats view. AUTOFILED counts separately by
    /// construction; NULL (pending) is reported as "pending".</summary>
    public async Task<IReadOnlyDictionary<string, int>> CorpusFieldOutcomeCountsAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(Outcome, 'pending'), COUNT(*) FROM corpus_fields GROUP BY COALESCE(Outcome, 'pending');";
        var outp = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) outp[r.GetString(0)] = r.GetInt32(1);
        return outp;
    }

    /// <summary>The adjudicated pairs for one review. Upsert; a NULL incoming Provenance never clears a recorded one
    /// (a reconcile recovery replay must not erase what the inline submit observed).</summary>
    public async Task PutAdjudicationsAsync(string documentId, IEnumerable<FieldAdjudicationRow> rows, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await c.BeginTransactionAsync(ct);
        foreach (var row in rows)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO field_adjudications (DocumentId, FieldName, ProposedValue, FinalValue, Signal, Provenance, At)
                VALUES ($id, $f, $pv, $fv, $sig, $prov, $at)
                ON CONFLICT(DocumentId, FieldName) DO UPDATE SET
                    ProposedValue=excluded.ProposedValue, FinalValue=excluded.FinalValue, Signal=excluded.Signal,
                    Provenance=COALESCE(excluded.Provenance, field_adjudications.Provenance), At=excluded.At;
                """;
            cmd.Parameters.AddWithValue("$id", documentId);
            cmd.Parameters.AddWithValue("$f", row.FieldName);
            cmd.Parameters.AddWithValue("$pv", (object?)row.ProposedValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fv", (object?)row.FinalValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sig", row.Signal);
            cmd.Parameters.AddWithValue("$prov", (object?)row.Provenance ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FieldAdjudicationRow>> GetAdjudicationsAsync(string documentId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT FieldName, ProposedValue, FinalValue, Signal, Provenance FROM field_adjudications WHERE DocumentId=$id ORDER BY FieldName;";
        cmd.Parameters.AddWithValue("$id", documentId);
        var outp = new List<FieldAdjudicationRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            outp.Add(new FieldAdjudicationRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return outp;
    }

    /// <summary>Total corpus measurement rows (capture-health stats).</summary>
    public async Task<int> CorpusRowCountAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM corpus;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Corpus rows with a human label (the scoreable population).</summary>
    public async Task<int> LabeledRowCountAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM corpus WHERE Label IS NOT NULL;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Total adjudication rows (capture-health stats; never the values).</summary>
    public async Task<int> AdjudicationCountAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM field_adjudications;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Fill in the human's answer when the reviewer files. This is what turns a row into a SCOREABLE one —
    /// `Label IS NOT NULL` is the whole corpus query. Deliberately an UPDATE of the existing row, not a second table:
    /// the label describes the same document, arrives late, and joining two tables on every analysis buys nothing.
    /// A no-op when the document has no row (retention was off when it was transcribed).</summary>
    public async Task<bool> SetLabelAsync(string documentId, string? label, string? labelDomain, string? driveItemId,
        DateTimeOffset labelAt, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE corpus SET Label=$l, LabelDomain=$d, LabelAt=$at,
                              DriveItemId=COALESCE($drv, DriveItemId)
            WHERE DocumentId=$id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$l", (object?)label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)labelDomain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$drv", (object?)driveItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", labelAt.UtcDateTime.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>THE corpus query: scoreable documents in a window. No joins across Extracted/Filed/Reviewed, no
    /// filesystem archaeology — freeze these ids, pull the PDFs from SharePoint by DriveItemId, run both arms.</summary>
    public async Task<IReadOnlyList<(string DocumentId, string Label, string? DriveItemId)>> ScoreableSinceAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DocumentId, Label, DriveItemId FROM corpus WHERE At > $since AND Label IS NOT NULL ORDER BY At;";
        cmd.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O"));
        var outp = new List<(string, string, string?)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) outp.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return outp;
    }

    /// <summary>Per-class counts of the scoreable corpus — how close it is to supporting a comparison.</summary>
    public async Task<IReadOnlyList<(string Label, int Count)>> LabelCountsAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Label, COUNT(*) n FROM corpus WHERE Label IS NOT NULL GROUP BY Label ORDER BY n DESC;";
        var outp = new List<(string, int)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) outp.Add((r.GetString(0), r.GetInt32(1)));
        return outp;
    }

    /// <summary>Erase ONE document's CONTENT — the erasure-request path. Removes the transcript, its artifacts, and
    /// its captured model calls (request/response bodies are the document's text), plus the adjudicated value pairs.
    ///
    /// The corpus row is deliberately KEPT: it is non-PHI, and destroying it would erase the evidence that the
    /// document was processed and how the model performed — which is not what an erasure request asks for. Use
    /// <see cref="ForgetAsync"/> when the row itself must go too.
    ///
    /// `attempt_starts` is deliberately KEPT too, and this is a SAFETY property, not a retention one: those rows are
    /// the redrive bound's counter (see UploadProcessor's REDRIVE BOUND). They carry no document content — an id and
    /// a timestamp — and deleting them hands a still-looping poison document a fresh unbounded redrive budget, which
    /// is the one unbounded retry loop this pipeline had. An erasure request asks for the document's text to go, not
    /// for its crash bound to reset. <see cref="ForgetAsync"/> does remove them: `full` is document-terminal, so
    /// there is no surviving document to protect.
    ///
    /// The ledger is untouched either way: its transcriptSha256 remains as proof of what was produced.</summary>
    public async Task<bool> DeleteAsync(string documentId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM transcripts WHERE DocumentId=$id;
            DELETE FROM transcript_artifacts WHERE DocumentId=$id;
            DELETE FROM model_calls WHERE DocumentId=$id;
            DELETE FROM field_adjudications WHERE DocumentId=$id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return n > 0;
    }

    /// <summary>Remove the document ENTIRELY, measurement rows AND the attempt marks included. Separate from
    /// <see cref="DeleteAsync"/> so erasing content is not silently also erasing the metrics — or the redrive
    /// bound. Removing the marks is correct HERE and only here: `full` is document-terminal, so no surviving
    /// document inherits a reset crash budget.</summary>
    public async Task<bool> ForgetAsync(string documentId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM transcripts WHERE DocumentId=$id;
            DELETE FROM transcript_artifacts WHERE DocumentId=$id;
            DELETE FROM model_calls WHERE DocumentId=$id;
            DELETE FROM field_adjudications WHERE DocumentId=$id;
            DELETE FROM corpus WHERE DocumentId=$id;
            DELETE FROM corpus_fields WHERE DocumentId=$id;
            DELETE FROM attempt_starts WHERE DocumentId=$id;
            """;
        cmd.Parameters.AddWithValue("$id", documentId);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return n > 0;
    }

    /// <summary>Bulk purge past the window — the CONTENT tables only (transcripts, artifacts, model calls,
    /// adjudicated values). The measurement tables (corpus, corpus_fields) are deliberately EXEMPT: a retention
    /// window is a decision about how long to keep document TEXT, and it must not silently cap the measurement
    /// history at the window length — the corpus is the longitudinal record the model comparisons run on, and
    /// aging it out with the text would delete the evidence of how the system performed (migration plan C2).
    /// Removing a document's measurement rows is <see cref="ForgetAsync"/>, an explicit per-document act.
    /// Returns rows removed from `transcripts`.</summary>
    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await c.BeginTransactionAsync(ct);
        var cut = cutoff.UtcDateTime.ToString("O");
        int removed;
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM transcripts WHERE At < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cut);
            removed = await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM transcript_artifacts WHERE At < $cutoff;
                DELETE FROM model_calls WHERE At < $cutoff;
                DELETE FROM field_adjudications WHERE At < $cutoff;
                DELETE FROM attempt_starts WHERE At < $cutoff;
                """;
            cmd.Parameters.AddWithValue("$cutoff", cut);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return removed;
    }

    /// <summary>See <see cref="ITranscriptStore.LogErasureAsync"/> — the erasure surface's audited record.</summary>
    public async Task LogErasureAsync(string documentId, string scope, string actor, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO erasure_log (DocumentId, Scope, Actor, At) VALUES ($id, $scope, $actor, $at);";
        cmd.Parameters.AddWithValue("$id", documentId);
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$at", at.UtcDateTime.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Epoch = the store FILE's creation instant (the CorpusGuardTests convention).</summary>
    public Task<DateTimeOffset?> EpochAsync(CancellationToken ct = default) =>
        Task.FromResult(File.Exists(_dbPath)
            ? (DateTimeOffset?)new DateTimeOffset(File.GetCreationTimeUtc(_dbPath), TimeSpan.Zero)
            : null);

    /// <summary>How many transcripts are currently retained (ops/tests; never the content).</summary>
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM transcripts;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}
