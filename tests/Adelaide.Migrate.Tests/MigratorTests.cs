using Adelaide.Audit;
using Adelaide.Core;
using Adelaide.Migrate;
using Microsoft.Data.Sqlite;

namespace Adelaide.Migrate.Tests;

// Exercises the real Migrator copy path SQLite -> SQLite (driver-agnostic, so the same code runs SQLite -> Azure SQL in
// production). The crux assertion: the target chain re-verifies with the IDENTICAL head hash — i.e. the copy is byte-exact
// and the tamper-evidence survives the migration.
public class MigratorTests : IDisposable
{
    private readonly string _src = Path.Combine(Path.GetTempPath(), $"adelaide-mig-src-{Guid.NewGuid():N}.db");
    private readonly string _tgt = Path.Combine(Path.GetTempPath(), $"adelaide-mig-tgt-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var b in new[] { _src, _tgt })
            foreach (var f in new[] { b, b + "-wal", b + "-shm" })
                try { File.Delete(f); } catch { /* best effort */ }
    }

    private static SqliteConnection Raw(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        return c;
    }

    [Fact]
    public async Task Copies_events_and_projection_and_chain_reverifies_identically()
    {
        // A realistic source: several events incl. a Filed event whose Details populate the projection.
        ChainVerification srcChain;
        DocumentAudit? srcDoc;
        using (var src = new SqliteAuditStore(_src))
        {
            await src.AppendAsync(new AuditEntry("doc-1", "Received", "test"));
            await src.AppendAsync(new AuditEntry("doc-1", "Filed", "test",
                Details: "{\"fileName\":\"2026-05-28_Verordnung_Z000000005_3f9a1c2e.pdf\",\"contentType\":\"Verordnung\",\"patientListItemId\":102}"));
            await src.AppendAsync(new AuditEntry("doc-2", "Received", "test"));
            srcChain = await src.VerifyChainAsync();
            srcDoc = await src.GetDocumentAsync("doc-1");
        }
        Assert.True(srcChain.Intact);
        Assert.Equal(3, srcChain.EventCount);

        // Fresh, empty target schema.
        using (var _ = new SqliteAuditStore(_tgt)) { }

        // Migrate via the real copy path.
        Migrator.Result result;
        using (var s = Raw(_src))
        using (var t = Raw(_tgt))
            result = await Migrator.MigrateAsync(s, t);

        Assert.Equal(3, result.EventsCopied);
        Assert.Equal(2, result.DocumentsCopied);

        // Target chain re-verifies IDENTICALLY — same count + same head hash (the byte-identical contract).
        ChainVerification tgtChain;
        DocumentAudit? tgtDoc;
        using (var tgt = new SqliteAuditStore(_tgt))
        {
            tgtChain = await tgt.VerifyChainAsync();
            tgtDoc = await tgt.GetDocumentAsync("doc-1");
        }
        Assert.True(tgtChain.Intact);
        Assert.Equal(srcChain.EventCount, tgtChain.EventCount);
        Assert.Equal(srcChain.HeadSeq, tgtChain.HeadSeq);
        Assert.Equal(srcChain.HeadHash, tgtChain.HeadHash);   // <- the crux: identical head hash after migration

        // Projection (what the Review UI reads) came across intact.
        Assert.NotNull(tgtDoc);
        Assert.Equal("Filed", tgtDoc!.State);
        Assert.Equal("Verordnung", tgtDoc.ContentType);
        Assert.Equal(102, tgtDoc.PatientListItemId);
        Assert.Equal(srcDoc!.FileName, tgtDoc.FileName);
    }

    [Fact]
    public async Task Seed_unions_sources_into_the_projection_only_and_never_clobbers()
    {
        // Source 1 (the MAIN ledger): doc-1 Filed with a driveItemId + srid, doc-2 in review.
        using (var src = new SqliteAuditStore(_src))
        {
            await src.AppendAsync(new AuditEntry("doc-1", "Received", "test",
                Details: """{"channel":"Drain","sha256":"aa11"}""",
                Projection: new ProjectionOverrides(OriginalName: "scan-a.pdf", SourceRecordId: "share/scan-a.pdf")));
            await src.AppendAsync(new AuditEntry("doc-1", "Filed", "test",
                Details: """{"contentType":"Verordnung","disposition":"AutoFile","driveItemId":"drv-1"}""",
                Projection: new ProjectionOverrides(FileName: "2026-05-28_Verordnung_X.pdf")));
            await src.AppendAsync(new AuditEntry("doc-2", "NeedsReview", "gate", Details: """{"reasonCodes":["low_confidence"],"confidence":0.55}"""));
        }
        // Source 2 (a shadow chain from the R6 census): doc-2 under a DIFFERENT state (must LOSE — first source
        // wins) + doc-3 only here.
        var shadow = Path.Combine(Path.GetTempPath(), $"adelaide-mig-shadow-{Guid.NewGuid():N}.db");
        try
        {
            using (var s2 = new SqliteAuditStore(shadow))
            {
                await s2.AppendAsync(new AuditEntry("doc-2", "Filed", "smoke", Details: """{"contentType":"Arztbrief"}"""));
                await s2.AppendAsync(new AuditEntry("doc-3", "Received", "smoke", Details: """{"channel":"Upload","sha256":"cc33"}"""));
            }
            // Target: fresh schema + one LIVE row (doc-1 already exists in the cloud) that must never be clobbered.
            using (var tgtStore = new SqliteAuditStore(_tgt))
                await tgtStore.AppendAsync(new AuditEntry("doc-1", "Received", "cloud", Details: """{"channel":"Upload","sha256":"live"}"""));

            Migrator.SeedResult seeded;
            using (var s1 = Raw(_src))
            using (var s2 = Raw(shadow))
            using (var t = Raw(_tgt))
                seeded = await Migrator.SeedProjectionAsync(new[] { (System.Data.Common.DbConnection)s1, s2 }, t);

            Assert.Equal(3, seeded.UnionCount);      // doc-1, doc-2 (from src1), doc-3 (from shadow)
            Assert.Equal(2, seeded.Inserted);        // doc-2 + doc-3
            Assert.Equal(1, seeded.SkippedExisting); // the live doc-1 row was left alone

            using var tgt = new SqliteAuditStore(_tgt);
            // PROJECTION ONLY: the target chain still holds exactly the one live event.
            Assert.Equal(1, (await tgt.VerifyChainAsync()).EventCount);
            // The live row was not clobbered by the seed's Filed-state doc-1.
            Assert.Equal("Received", (await tgt.GetDocumentAsync("doc-1"))!.State);
            Assert.Equal("live", (await tgt.GetDocumentAsync("doc-1"))!.Sha256);
            // First source wins for doc-2 — and its NON-TERMINAL state transmutes to SeededHistory (verification
            // finding: seeded ghosts must never surface as actionable review/dead-letter work), while the
            // measurement columns ride along.
            var doc2 = await tgt.GetDocumentAsync("doc-2");
            Assert.Equal("SeededHistory", doc2!.State);
            Assert.Contains("low_confidence", doc2.ReasonCodes!);
            Assert.Equal(0.55, doc2.Confidence);
            // doc-3 came from the shadow chain, transmuted too, and the dedup lookups still see the seeded rows.
            Assert.Equal("SeededHistory", (await tgt.GetDocumentAsync("doc-3"))!.State);
            Assert.Equal("doc-3", (await tgt.FindBySha256Async("cc33", ""))!.DocumentId);
            // …but the ACTIVE dashboard query does not — SeededHistory is terminal for every list predicate.
            Assert.DoesNotContain((await tgt.ListActiveAsync()), d => d.DocumentId is "doc-2" or "doc-3");
        }
        finally
        {
            foreach (var f in new[] { shadow, shadow + "-wal", shadow + "-shm" })
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Seed_carries_the_full_projection_row_including_the_drive_item_locator()
    {
        using (var src = new SqliteAuditStore(_src))
        {
            await src.AppendAsync(new AuditEntry("doc-9", "Received", "test",
                Details: """{"channel":"Drain","sha256":"99"}""",
                Projection: new ProjectionOverrides(OriginalName: "orig.pdf", SourceRecordId: "share/orig.pdf")));
            await src.AppendAsync(new AuditEntry("doc-9", "NeedsReview", "gate",
                Details: """{"reasonCodes":["low_confidence"],"confidence":0.42}"""));
            await src.AppendAsync(new AuditEntry("doc-9", "Reviewed", "user:rev",
                Details: """{"correctedFields":["documentDate"],"contentTypeChange":{"from":"Arztbrief","to":"Genehmigung"}}"""));
            await src.AppendAsync(new AuditEntry("doc-9", "Filed", "test",
                Details: """{"contentType":"Genehmigung","disposition":"ReviewedThenFiled","driveItemId":"drv-locator-9"}""",
                Projection: new ProjectionOverrides(FileName: "canonical-9.pdf")));
        }
        using (var _ = new SqliteAuditStore(_tgt)) { }

        using (var s = Raw(_src))
        using (var t = Raw(_tgt))
            await Migrator.SeedProjectionAsync(new[] { (System.Data.Common.DbConnection)s }, t);

        using var tgt = new SqliteAuditStore(_tgt);
        var d = await tgt.GetDocumentAsync("doc-9");
        Assert.Equal("Filed", d!.State);                   // terminal source state PRESERVED (only non-terminal transmutes)
        Assert.Equal("drv-locator-9", d.DriveItemId);      // the previously-dropped column — the viewer/compare/reclassify locator
        Assert.Equal("share/orig.pdf", d.SourceRecordId);  // drain idempotency
        Assert.Equal("canonical-9.pdf", d.FileName);       // business-key guard
        Assert.Equal("99", d.Sha256);                      // exact-duplicate dedup
        // Every column this commit added to the copy — a regression nulling any one fails here (mutation finding).
        Assert.Contains("low_confidence", d.ReasonCodes!);
        Assert.Equal(0.42, d.Confidence);
        Assert.Contains("documentDate", d.CorrectedFields!);
        Assert.Contains("Arztbrief", d.ContentTypeChange!);
    }

    [Fact]
    public async Task Seed_reads_a_pre_column_era_source_opened_read_only()
    {
        // A frozen shadow DB from before the newer projection columns (14-column era), opened STRICTLY READ-ONLY —
        // the seed's schema-tolerant read must supply NULLs for the missing columns instead of relying on anything
        // having ALTERed the evidence file first (verification finding).
        var old = Path.Combine(Path.GetTempPath(), $"adelaide-mig-old-{Guid.NewGuid():N}.db");
        try
        {
            using (var c = Raw(old))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE documents (
                        DocumentId TEXT PRIMARY KEY, State TEXT NOT NULL, OriginalName TEXT, FileName TEXT, ContentType TEXT,
                        PatientListItemId INTEGER, Disposition TEXT, Reasons TEXT, Sha256 TEXT, DuplicateOf TEXT, SourceRecordId TEXT,
                        FirstSeen TEXT NOT NULL, LastUpdated TEXT NOT NULL, EventCount INTEGER NOT NULL);
                    INSERT INTO documents VALUES ('old-doc-1','Received','legacy.pdf',NULL,NULL,NULL,NULL,NULL,'dd44',NULL,'share/legacy.pdf',
                        '2026-06-01T00:00:00.0000000+00:00','2026-06-01T00:00:00.0000000+00:00',1);
                    """;
                cmd.ExecuteNonQuery();
            }
            using (var _ = new SqliteAuditStore(_tgt)) { }

            using (var s = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = old, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            using (var t = Raw(_tgt))
            {
                s.Open();
                await Migrator.SeedProjectionAsync(new[] { (System.Data.Common.DbConnection)s }, t);
            }

            using var tgt = new SqliteAuditStore(_tgt);
            var d = await tgt.GetDocumentAsync("old-doc-1");
            Assert.Equal("SeededHistory", d!.State);      // non-terminal legacy state transmutes
            Assert.Equal("dd44", d.Sha256);
            Assert.Null(d.DriveItemId);                   // missing-in-source columns land as NULL, not an error
            Assert.Equal("old-doc-1", (await tgt.FindBySourceRecordIdAsync("share/legacy.pdf", ""))!.DocumentId);
        }
        finally
        {
            foreach (var f in new[] { old, old + "-wal", old + "-shm" })
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Preflight_refuses_a_timestamp_that_would_sort_wrongly()
    {
        // FirstSeen/LastUpdated go into NVARCHAR(40) and are compared as STRINGS — every dashboard list orders by
        // LastUpdated, and the stalled-document gauge asks LastUpdated < cutoff. That is sound only because every
        // value shares one fixed-width skeleton, so only digits differ. Both stores guarantee it by always writing
        // "o"; this seed is the one path that copies a timestamp in from an arbitrary source, and the length bound
        // alone cannot catch a badly-shaped one: "…Z" is 28 characters and "+02:00" is 33, both under 40. Neither
        // errors. They just sort wrongly, forever.
        var bad = Path.Combine(Path.GetTempPath(), $"adelaide-mig-bad-{Guid.NewGuid():N}.db");
        try
        {
            using (var c = Raw(bad))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE documents (
                        DocumentId TEXT PRIMARY KEY, State TEXT NOT NULL, OriginalName TEXT, FileName TEXT, ContentType TEXT,
                        PatientListItemId INTEGER, Disposition TEXT, Reasons TEXT, Sha256 TEXT, DuplicateOf TEXT, SourceRecordId TEXT,
                        FirstSeen TEXT NOT NULL, LastUpdated TEXT NOT NULL, EventCount INTEGER NOT NULL);
                    -- 'Z' form: 28 chars, well inside NVARCHAR(40), and it sorts BEFORE every '+00:00' value.
                    INSERT INTO documents VALUES ('bad-z','Received',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
                        '2026-06-01T00:00:00.0000000Z','2026-06-01T00:00:00.0000000Z',1);
                    -- Non-UTC offset: correct length, correct shape, WRONG instant once compared lexicographically.
                    INSERT INTO documents VALUES ('bad-offset','Received',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
                        '2026-06-01T00:00:00.0000000+02:00','2026-06-01T00:00:00.0000000+02:00',1);
                    -- The good shape, to prove the check is not simply rejecting everything.
                    INSERT INTO documents VALUES ('good','Received',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
                        '2026-06-01T00:00:00.0000000+00:00','2026-06-01T00:00:00.0000000+00:00',1);
                    """;
                cmd.ExecuteNonQuery();
            }

            using var s = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bad, Mode = SqliteOpenMode.ReadOnly }.ToString());
            s.Open();
            var pre = await Migrator.SeedPreflightAsync(new[] { (System.Data.Common.DbConnection)s });

            Assert.Equal(3, pre.UnionCount);
            Assert.Equal(4, pre.Violations.Count);                    // two columns × two bad rows
            Assert.All(pre.Violations, v => Assert.Contains("round-trip UTC", v, StringComparison.Ordinal));
            Assert.DoesNotContain(pre.Violations, v => v.StartsWith("good:", StringComparison.Ordinal));
            // Shape only — the offending VALUE never appears, so this output stays safe to paste into a ticket.
            Assert.DoesNotContain(pre.Violations, v => v.Contains("2026-06-01", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var f in new[] { bad, bad + "-wal", bad + "-shm" })
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Refuses_to_migrate_into_a_populated_ledger()
    {
        using (var src = new SqliteAuditStore(_src))
            await src.AppendAsync(new AuditEntry("doc", "Received", "test"));
        using (var _ = new SqliteAuditStore(_tgt)) { }

        // First migration succeeds.
        using (var s = Raw(_src))
        using (var t = Raw(_tgt))
            await Migrator.MigrateAsync(s, t);

        // Second migration into the now-populated target MUST throw — the anti-double-migration / chain-corruption guard.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var s = Raw(_src);
            using var t = Raw(_tgt);
            await Migrator.MigrateAsync(s, t);
        });
    }
}
