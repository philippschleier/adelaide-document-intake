-- Adelaide capture substrate (migration plan C3, decision D1) — run ONCE by a SQL admin (the Entra admin on the
-- logical server) against the SAME database as init.sql (adelaide-audit), BEFORE deploying a build that selects the
-- Azure SQL capture stores. Idempotent. The app's managed-identity user (created by init.sql) has db_datareader /
-- db_datawriter — database-wide roles, so these tables are covered with NO new grants and the app still has no DDL.
--
-- TWO SCHEMAS, split by data class (mirrors the SQLite store's PHI/non-PHI table split):
--
--   capture.*  — document CONTENT and per-attempt operational rows: transcripts, per-page artifacts, verbatim
--                model-call bodies, adjudicated field values, attempt marks. Erasable per document
--                (DeleteAsync/ForgetAsync) and aged by the retention window.
--   measure.*  — the NON-PHI measurement layer: corpus rows, per-field calibration rows, and the erasure log.
--                Deliberately NOT covered by DeleteAsync or the window purge (plan C2): erasing content must not
--                erase the evidence of how the model performed.
--
-- Co-location with dbo.events is safe BY THE ENGINE: events is an append-only ledger table; no DELETE against
-- these schemas can touch it regardless of permissions.
--
-- Timestamps are native datetimeoffset(7) here (unlike dbo.events, whose At stays a hashed 'o'-string for chain
-- byte-compatibility) — there is no hash chain over capture rows, and Phase 6 analytics queries want real types.
--
-- Collation is PINNED where identity or grouping semantics depend on it (verification finding — behavior must
-- never be a property of the server default): DocumentId, Label and Outcome are Latin1_General_100_BIN2 to mirror
-- the SQLite twin's binary comparisons; FieldName deliberately stays at the database default because the store
-- applies an explicit case-insensitive COLLATE on exactly the comparisons that want it.

SET XACT_ABORT ON;
SET NOCOUNT ON;

IF SCHEMA_ID('capture') IS NULL EXEC('CREATE SCHEMA capture');
IF SCHEMA_ID('measure') IS NULL EXEC('CREATE SCHEMA measure');

-- ── capture: content, erasable ──────────────────────────────────────────────────────────────────────────────────

IF OBJECT_ID('capture.transcripts','U') IS NULL
    CREATE TABLE capture.transcripts (
        DocumentId       NVARCHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        Transcript       NVARCHAR(MAX)     NOT NULL,
        TranscriptSha256 NVARCHAR(64)      NOT NULL,
        At               DATETIMEOFFSET(7) NOT NULL
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_transcripts_at' AND object_id=OBJECT_ID('capture.transcripts'))
    CREATE INDEX ix_transcripts_at ON capture.transcripts(At);

IF OBJECT_ID('capture.transcript_artifacts','U') IS NULL
    CREATE TABLE capture.transcript_artifacts (
        DocumentId       NVARCHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        PageMarkdown     NVARCHAR(MAX)     NULL,
        BarcodePayloads  NVARCHAR(MAX)     NULL,
        CheckboxEvidence NVARCHAR(MAX)     NULL,
        At               DATETIMEOFFSET(7) NOT NULL
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_artifacts_at' AND object_id=OBJECT_ID('capture.transcript_artifacts'))
    CREATE INDEX ix_artifacts_at ON capture.transcript_artifacts(At);

-- One row per model-call attempt, request/response verbatim (image payloads elided upstream). Append-only by design.
IF OBJECT_ID('capture.model_calls','U') IS NULL
    CREATE TABLE capture.model_calls (
        Id               BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        DocumentId       NVARCHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,
        CallSite         NVARCHAR(40)      NOT NULL,
        Attempt          INT               NOT NULL,
        At               DATETIMEOFFSET(7) NOT NULL,
        Deployment       NVARCHAR(128)     NULL,
        ServedModel      NVARCHAR(128)     NULL,
        SystemFingerprint NVARCHAR(128)    NULL,
        PromptHash       NVARCHAR(64)      NULL,
        ReasoningEffort  NVARCHAR(32)      NULL,
        RequestBody      NVARCHAR(MAX)     NULL,
        RenderParams     NVARCHAR(MAX)     NULL,
        SourcePdfSha256  NVARCHAR(64)      NULL,
        ResponseBody     NVARCHAR(MAX)     NULL,
        OutputSha256     NVARCHAR(64)      NULL,
        OutputBytes      BIGINT            NULL,
        OutputRef        NVARCHAR(64)      NULL,
        PromptTokens     INT               NULL,
        CompletionTokens INT               NULL,
        ReasoningTokens  INT               NULL,
        LatencyMs        BIGINT            NOT NULL,
        RequestId        NVARCHAR(128)     NULL,
        HttpStatus       INT               NULL,
        Error            NVARCHAR(MAX)     NULL
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_model_calls_doc' AND object_id=OBJECT_ID('capture.model_calls'))
    CREATE INDEX ix_model_calls_doc ON capture.model_calls(DocumentId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_model_calls_at' AND object_id=OBJECT_ID('capture.model_calls'))
    CREATE INDEX ix_model_calls_at ON capture.model_calls(At);

IF OBJECT_ID('capture.field_adjudications','U') IS NULL
    CREATE TABLE capture.field_adjudications (
        DocumentId    NVARCHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,
        FieldName     NVARCHAR(128)     NOT NULL,
        ProposedValue NVARCHAR(MAX)     NULL,
        FinalValue    NVARCHAR(MAX)     NULL,
        Signal        NVARCHAR(16)      NOT NULL,
        Provenance    NVARCHAR(16)      NULL,
        At            DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT pk_field_adjudications PRIMARY KEY (DocumentId, FieldName)
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_field_adjudications_at' AND object_id=OBJECT_ID('capture.field_adjudications'))
    CREATE INDEX ix_field_adjudications_at ON capture.field_adjudications(At);

-- One row per worker pickup — the redrive bound's attempt evidence. Aged by the window purge and removed by
-- ForgetAsync (document-terminal), but NOT by DeleteAsync: a content erasure must not reset the crash budget of a
-- document that is still processable.
IF OBJECT_ID('capture.attempt_starts','U') IS NULL
    CREATE TABLE capture.attempt_starts (
        DocumentId NVARCHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,
        At         DATETIMEOFFSET(7) NOT NULL
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_attempt_starts_doc' AND object_id=OBJECT_ID('capture.attempt_starts'))
    CREATE INDEX ix_attempt_starts_doc ON capture.attempt_starts(DocumentId, At);

-- ── measure: non-PHI measurement, survives erasure of content ───────────────────────────────────────────────────

IF OBJECT_ID('measure.corpus','U') IS NULL
    CREATE TABLE measure.corpus (
        DocumentId       NVARCHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        At               DATETIMEOFFSET(7) NOT NULL,
        TranscriptSha256 NVARCHAR(64)      NULL,
        Model            NVARCHAR(128)     NULL,
        ModelVersion     NVARCHAR(128)     NULL,
        PromptHash       NVARCHAR(64)      NULL,
        ReasoningEffort  NVARCHAR(32)      NULL,
        SystemFingerprint NVARCHAR(128)    NULL,
        Pages            INT               NULL,
        Legibility       FLOAT             NULL,
        Barcodes         INT               NULL,
        TranscriptChars  INT               NULL,
        ModelDomain      NVARCHAR(32)      NULL,
        ModelContentType NVARCHAR(128)     NULL,
        ModelConfidence  FLOAT             NULL,
        Label            NVARCHAR(128)     COLLATE Latin1_General_100_BIN2 NULL,
        LabelDomain      NVARCHAR(32)      NULL,
        LabelAt          DATETIMEOFFSET(7) NULL,
        DriveItemId      NVARCHAR(256)     NULL,
        ContentSha256    NVARCHAR(64)      NULL,
        ReviewState      NVARCHAR(64)      NULL,
        ReasonCodes      NVARCHAR(MAX)     NULL
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_corpus_at' AND object_id=OBJECT_ID('measure.corpus'))
    CREATE INDEX ix_corpus_at ON measure.corpus(At);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_corpus_label' AND object_id=OBJECT_ID('measure.corpus'))
    CREATE INDEX ix_corpus_label ON measure.corpus(Label);

IF OBJECT_ID('measure.corpus_fields','U') IS NULL
    CREATE TABLE measure.corpus_fields (
        DocumentId NVARCHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,
        FieldName  NVARCHAR(128)     NOT NULL,
        Proposed   BIT               NOT NULL,
        Confidence FLOAT             NULL,
        Source     NVARCHAR(32)      NULL,
        Outcome    NVARCHAR(16)      COLLATE Latin1_General_100_BIN2 NULL,
        OutcomeAt  DATETIMEOFFSET(7) NULL,
        At         DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT pk_corpus_fields PRIMARY KEY (DocumentId, FieldName)
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_corpus_fields_at' AND object_id=OBJECT_ID('measure.corpus_fields'))
    CREATE INDEX ix_corpus_fields_at ON measure.corpus_fields(At);

-- The audited record of per-document erasures. Written by the erasure surface AFTER the store operation; never
-- deleted by DeleteAsync/ForgetAsync/window purge — it is the durable record OF those acts. Non-PHI (id + scope).
IF OBJECT_ID('measure.erasure_log','U') IS NULL
    CREATE TABLE measure.erasure_log (
        Id         BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        DocumentId NVARCHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,
        Scope      NVARCHAR(16)      NOT NULL,  -- 'content' (DeleteAsync) | 'full' (ForgetAsync)
        Actor      NVARCHAR(256)     NOT NULL,
        At         DATETIMEOFFSET(7) NOT NULL
    );
