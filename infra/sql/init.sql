-- Adelaide audit substrate — run ONCE by a SQL admin (the Entra admin on the logical server) against the target
-- database, BEFORE the app starts. Idempotent. Creates the append-only LEDGER events table, the projection, and the
-- App Service managed-identity database user with data read/write only (no DDL — the ledger enforces append-only on
-- events even against the app). See infra/README.md for how it's run.
--
-- Replace <APP_NAME> with the App Service name (var.web_app_name) — its system-assigned managed identity surfaces in
-- Entra/SQL under that display name.

SET XACT_ABORT ON;
SET NOCOUNT ON;

-- 1) events — append-only LEDGER table (DB-layer tamper-evidence + digest anchoring against even a DBA). Storage
--    representations MATCH SqliteAuditStore byte-for-byte: At is the DateTimeOffset 'o' round-trip string, hashed
--    verbatim, so the app hash chain verifies identically and the SQLite history migrates as a straight copy.
IF OBJECT_ID('dbo.events','U') IS NULL
    CREATE TABLE dbo.events (
        Seq           BIGINT        NOT NULL PRIMARY KEY,
        DocumentId    NVARCHAR(64)  NOT NULL,
        UnitId        NVARCHAR(64)  NULL,
        Type          NVARCHAR(64)  NOT NULL,
        At            NVARCHAR(40)  NOT NULL,   -- DateTimeOffset 'o' string (hashed verbatim)
        Actor         NVARCHAR(256) NOT NULL,
        CorrelationId NVARCHAR(128) NULL,
        Details       NVARCHAR(MAX) NOT NULL,   -- STRUCTURAL METADATA ONLY. Append-only ledger = UN-ERASABLE: whatever
                                                -- lands here is permanent. Since C6 the app enforces this by construction:
                                                -- payloads carry opaque ids, content hashes, and reason codes — never
                                                -- filenames (the canonical name embeds patient name/KVNR) and never prose.
                                                -- Display text lives in the UPDATABLE documents projection; use the
                                                -- events_readable view below for name-enriched reads in one query.
        PrevHash      NVARCHAR(64)  NOT NULL,
        Hash          NVARCHAR(64)  NOT NULL
    )
    WITH (LEDGER = ON (APPEND_ONLY = ON));

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_events_doc' AND object_id = OBJECT_ID('dbo.events'))
    CREATE INDEX ix_events_doc ON dbo.events(DocumentId);

-- Discard-recovery: the undiscard event's correlation id joins the immutable event to the erasable projection
-- Reasons prose. A GUID collision must fail the append loudly, never silently attach one reason to two events.
-- Filtered (nulls exempt): every event before 2026-08-15 carries NULL here, so the index is purely additive.
-- Deliberate trade-off (same note as the SQLite twin ux_events_correlation): CorrelationId becomes a unique
-- per-event tag; a future flow sharing one id across a request's events must retire this index first.
-- Exercised in CI by the audit contract tests, which apply this schema to a fresh database per test.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ux_events_correlation' AND object_id = OBJECT_ID('dbo.events'))
    CREATE UNIQUE NONCLUSTERED INDEX ux_events_correlation ON dbo.events(CorrelationId) WHERE CorrelationId IS NOT NULL;

-- 2) documents — derived, rebuildable one-row-per-document projection (CQRS read model). A PLAIN updatable table
--    (NOT a ledger table): it's regenerated from events and is updated in place by the MERGE in the app.
IF OBJECT_ID('dbo.documents','U') IS NULL
    CREATE TABLE dbo.documents (
        DocumentId        NVARCHAR(64)  NOT NULL PRIMARY KEY,
        State             NVARCHAR(64)  NOT NULL,
        OriginalName      NVARCHAR(512) NULL,
        FileName          NVARCHAR(512) NULL,
        ContentType       NVARCHAR(128) NULL,
        PatientListItemId INT           NULL,
        Disposition       NVARCHAR(64)  NULL,
        Reasons           NVARCHAR(MAX) NULL,
        Sha256            NVARCHAR(64)  NULL,
        DuplicateOf       NVARCHAR(64)  NULL,
        SourceRecordId    NVARCHAR(256) NULL,
        ReasonCodes       NVARCHAR(MAX) NULL,
        Confidence        FLOAT         NULL,
        DriveItemId       NVARCHAR(256) NULL,
        CorrectedFields   NVARCHAR(MAX) NULL,
        ContentTypeChange NVARCHAR(MAX) NULL,
        FirstSeen         NVARCHAR(40)  NOT NULL,
        LastUpdated       NVARCHAR(40)  NOT NULL,
        EventCount        INT           NOT NULL
    );

-- Additive projection columns (idempotent — re-running adds them to an EXISTING documents table). The app has no DDL, so
-- an admin must run this against the prod database BEFORE deploying code that SELECTs these columns (else the read fails).
IF COL_LENGTH('dbo.documents','ReasonCodes')     IS NULL ALTER TABLE dbo.documents ADD ReasonCodes     NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.documents','Confidence')      IS NULL ALTER TABLE dbo.documents ADD Confidence      FLOAT         NULL;
IF COL_LENGTH('dbo.documents','DriveItemId')     IS NULL ALTER TABLE dbo.documents ADD DriveItemId     NVARCHAR(256) NULL;
IF COL_LENGTH('dbo.documents','CorrectedFields')   IS NULL ALTER TABLE dbo.documents ADD CorrectedFields   NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.documents','ContentTypeChange') IS NULL ALTER TABLE dbo.documents ADD ContentTypeChange NVARCHAR(MAX) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_documents_sha' AND object_id = OBJECT_ID('dbo.documents'))
    CREATE INDEX ix_documents_sha ON dbo.documents(Sha256);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_documents_state' AND object_id = OBJECT_ID('dbo.documents'))
    CREATE INDEX ix_documents_state ON dbo.documents(State);

-- 2b) events_readable — the one-query readable ledger (C6). Post-C6 event payloads are structural only; this view
--     re-joins the ERASABLE display columns (names, reasons prose) from the projection, so an operator reading the
--     chain never has to hand-join. The projection is the mapping: deleting/clearing a projection row's name columns
--     erases the name from every view row while the hash-chained events stay bit-identical. DROP+CREATE so a re-run
--     of this script can never leave a stale shape.
IF OBJECT_ID('dbo.events_readable','V') IS NOT NULL DROP VIEW dbo.events_readable;
EXEC('CREATE VIEW dbo.events_readable AS
    SELECT e.Seq, e.DocumentId, e.UnitId, e.Type, e.At, e.Actor, e.CorrelationId, e.Details,
           d.State, d.OriginalName, d.FileName, d.SourceRecordId, d.Reasons, d.DriveItemId
    FROM dbo.events e LEFT JOIN dbo.documents d ON d.DocumentId = e.DocumentId');

-- 3) App managed-identity user: data read/write only. db_datawriter lets the app INSERT events + MERGE the projection;
--    the LEDGER append-only constraint blocks UPDATE/DELETE on events regardless of this grant. No db_ddladmin — the app
--    must never ALTER/DROP. (Run the CREATE USER on the target database, not master.)
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '<APP_NAME>')
    CREATE USER [<APP_NAME>] FROM EXTERNAL PROVIDER;
-- ALTER ROLE ... ADD MEMBER is idempotent in SQL Server (a no-op if the member is already in the role, NOT an error),
-- so these are safe to re-run unguarded — re-running the whole script does not fail here.
ALTER ROLE db_datareader ADD MEMBER [<APP_NAME>];
ALTER ROLE db_datawriter ADD MEMBER [<APP_NAME>];

-- Follow-up (the external anchor, optional but recommended): enable automatic digest storage to an IMMUTABLE blob
-- container so the ledger digests are anchored outside the DB (tamper-PROOF, not just tamper-evident). That storage
-- account CANNOT be LRS (ledger requirement) — use ZRS/GRS. Configure via ALTER DATABASE ... SET LEDGER_DIGEST_STORAGE.
