using Microsoft.Data.Sqlite;

// ─────────────────────────────────────────────────────────────────────────────
// Adelaide classifier eval (baseline / Mode A) — measure classification accuracy
// from the audit ledger, without re-running the model.
//
// Ground truth: documents a human REVIEWED then filed carry the human-confirmed
// content type on their 'Filed' event (disposition='ReviewedThenFiled'); the
// model's own answer is on the 'Extracted' event. Joining the two per document
// gives a confusion matrix over exactly the docs a human adjudicated — the real
// accuracy signal, computed offline. Auto-filed docs have no human label, so
// they're reported but excluded from the accuracy denominator (unverified).
//
// PHI-safe: emits only content-type names and counts — never patient data.
// Read-only DB access; never touches the live worker's writes.
// ─────────────────────────────────────────────────────────────────────────────

// --ids <file> restricts scoring to a frozen document set (one id per line; '#' comments and blanks
// ignored). Without it the whole ledger is scored, as before. The frozen list is how a baseline stays
// comparable across a model migration: the same documents must be re-scored, not merely the same query.
// It is ALSO how triage/business documents stay out — they are filed as 'Triagedokument', a type the
// classifier cannot emit, so including them turns every one into a guaranteed miss (34.2% vs 87.8% on
// the 2026-08-05 ledger). See tools/baseline/freeze-classification-set.mjs.
var idsIdx = Array.IndexOf(args, "--ids");
string? idsPath = idsIdx >= 0 && idsIdx + 1 < args.Length ? args[idsIdx + 1] : null;
if (idsIdx >= 0 && idsPath is null) { Console.Error.WriteLine("FATAL: --ids requires a file path."); return 2; }

// Walk the arguments and consume each flag's value(s), so only true positionals remain. Doing this structurally
// rather than by index arithmetic: an earlier version computed idsIdx+1 with idsIdx == -1, which silently swallowed
// the db path argument and fell through to ADELAIDE_AUDIT_DB — i.e. quietly scored a DIFFERENT ledger than the one
// named on the command line. With three flags now, one of them variadic, index maths is not worth the risk.
var positionalList = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (!a.StartsWith("--", StringComparison.Ordinal)) { positionalList.Add(a); continue; }
    if (a == "--backfill-oracle") { while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) i++; continue; }
    if (a is "--ids" or "--oracle-db") { if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) i++; continue; }
}
var positional = positionalList.ToArray();
var dbPath = positional.FirstOrDefault()
             ?? Environment.GetEnvironmentVariable("ADELAIDE_AUDIT_DB")
             ?? "adelaide-audit.db";
if (!File.Exists(dbPath)) { Console.Error.WriteLine($"FATAL: audit DB not found at '{dbPath}'."); return 2; }

// --backfill-oracle <sidecarDir> [<sidecarDir>...] derives the identity oracle from review sidecars and upserts it
// into the oracle store, then reports. The sidecars are deleted at filing, so this measurement can only be taken
// while they still exist — freezing it is the point. Idempotent: the store's key is (documentId, field).
var bfIdx = Array.IndexOf(args, "--backfill-oracle");
var backfillDirs = bfIdx < 0 ? [] : args.Skip(bfIdx + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (bfIdx >= 0 && backfillDirs.Length == 0) { Console.Error.WriteLine("FATAL: --backfill-oracle requires at least one sidecar directory."); return 2; }

// --oracle-db <path> overrides where the oracle store lives (default: beside the audit DB).
var oracleIdx = Array.IndexOf(args, "--oracle-db");
string? oracleDbArg = oracleIdx >= 0 && oracleIdx + 1 < args.Length ? args[oracleIdx + 1] : null;

HashSet<string>? frozen = null;
if (idsPath is not null)
{
    if (!File.Exists(idsPath)) { Console.Error.WriteLine($"FATAL: id list not found at '{idsPath}'."); return 2; }
    frozen = File.ReadAllLines(idsPath)
                 .Select(l => l.Trim())
                 .Where(l => l.Length > 0 && !l.StartsWith('#'))
                 .ToHashSet(StringComparer.Ordinal);
    if (frozen.Count == 0) { Console.Error.WriteLine($"FATAL: id list '{idsPath}' contained no ids."); return 2; }
}

var oracleDbPath = oracleDbArg ?? Adelaide.Audit.IdentityOracleStore.DefaultPathFor(dbPath);

// ── Backfill: derive the identity oracle from review sidecars ────────────────────────────────────────────────────
// The comparison and the structural descriptor come from Adelaide.Core.IdentityAgreement — the SAME code the live
// review path uses — so a backfilled row and a live row can never mean different things.
if (backfillDirs.Length > 0)
{
    var oracle = new Adelaide.Audit.IdentityOracleStore(oracleDbPath);
    int sidecars = 0, withCandidate = 0, samples = 0, skippedNoPair = 0;
    var at = DateTimeOffset.UtcNow;

    foreach (var dir in backfillDirs)
    {
        if (!Directory.Exists(dir)) { Console.Error.WriteLine($"  (skipping missing directory {dir})"); continue; }
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            System.Text.Json.JsonDocument doc;
            try { doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                sidecars++;
                if (root.TryGetProperty("documentId", out var idEl) is false || idEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var docId = idEl.GetString()!;
                if (!root.TryGetProperty("match", out var m) || !m.TryGetProperty("candidate", out var cand)
                    || cand.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                withCandidate++;

                var modelValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("fields", out var fa) && fa.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var f in fa.EnumerateArray())
                        if (f.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                            modelValues[n.GetString()!] = f.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

                string? Cand(string k) => cand.TryGetProperty(k, out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null;
                foreach (var (column, candKey) in new[] { ("KVNR", "kvnr"), ("LastName", "lastName"), ("FirstName", "firstName"), ("BirthDate", "birthDate") })
                {
                    modelValues.TryGetValue(column, out var mv);
                    var r = Adelaide.Core.IdentityAgreement.Compare(column, mv, Cand(candKey));
                    if (r is null) { skippedNoPair++; continue; }   // one side empty => a recall gap, not a wrong read
                    await oracle.PutAsync(docId, r.Field, r.Agreed, r.Shape, Adelaide.Audit.IdentityOracleStore.SourceBackfill, at);
                    samples++;
                }
            }
        }
    }
    Console.WriteLine("=== Identity oracle backfill (from review sidecars) ===");
    Console.WriteLine($"  sidecars read              : {sidecars}");
    Console.WriteLine($"  with a matched candidate   : {withCandidate}");
    Console.WriteLine($"  comparable field pairs     : {samples}");
    Console.WriteLine($"  skipped (one side empty)   : {skippedNoPair}   <- model read nothing: a RECALL gap, not a wrong read");
    Console.WriteLine($"  store                      : {oracleDbPath}  ({await oracle.CountAsync()} rows total)");
    Console.WriteLine();
}

var connStr = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
using var db = new SqliteConnection(connStr);
db.Open();

// Per document: the latest 'Extracted' content type (the model) joined to the latest 'Filed' content type +
// disposition (the outcome). For disposition='ReviewedThenFiled', the filed type is the human's confirmed answer.
const string sql = """
    WITH ai AS (
      SELECT e.DocumentId, json_extract(e.Details,'$.contentType') AS aitype
      FROM events e
      JOIN (SELECT DocumentId, MAX(Seq) s FROM events WHERE Type='Extracted' GROUP BY DocumentId) m
        ON e.DocumentId=m.DocumentId AND e.Seq=m.s
    ),
    filed AS (
      SELECT e.DocumentId,
             json_extract(e.Details,'$.contentType') AS finaltype,
             json_extract(e.Details,'$.disposition') AS disp
      FROM events e
      JOIN (SELECT DocumentId, MAX(Seq) s FROM events WHERE Type='Filed' GROUP BY DocumentId) m
        ON e.DocumentId=m.DocumentId AND e.Seq=m.s
    )
    SELECT ai.DocumentId, ai.aitype, filed.finaltype, filed.disp
    FROM ai JOIN filed USING(DocumentId);
    """;

// Selected per-document (not pre-grouped) so a --ids frozen set can be applied before aggregating.
var perDoc = new List<(string Id, string Ai, string Human, string Disp)>();
using (var cmd = db.CreateCommand())
{
    cmd.CommandText = sql;
    using var r = cmd.ExecuteReader();
    while (r.Read())
        perDoc.Add((
            r.GetString(0),
            r.IsDBNull(1) ? "(null)" : r.GetString(1),
            r.IsDBNull(2) ? "(null)" : r.GetString(2),
            r.IsDBNull(3) ? "(none)" : r.GetString(3)));
}

var scoredIds = perDoc.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
if (frozen is not null)
{
    var missing = frozen.Except(scoredIds).Count();
    perDoc = perDoc.Where(x => frozen.Contains(x.Id)).ToList();
    Console.WriteLine($"Frozen set: {idsPath}");
    Console.WriteLine($"  {frozen.Count} ids requested, {perDoc.Count} scored, {missing} not present in this ledger.");
    if (missing > 0)
        Console.WriteLine("  WARNING: ids missing from the ledger are NOT scored — the comparison is no longer like-for-like.");
    Console.WriteLine();
}

// TYPE accuracy is the SECOND decision and is scored only where the FIRST one landed on the medical lane — i.e. the
// human called it medical AND the model did not route it to business. A document either side of that is a domain
// result, already counted above; scoring it again as a type miss is the double-count that produced the old 34.2%.
var businessIds = new HashSet<string>(StringComparer.Ordinal);
using (var cmd = db.CreateCommand())
{
    cmd.CommandText = """
        SELECT e.DocumentId FROM events e
        JOIN (SELECT DocumentId, MAX(Seq) s FROM events WHERE Type='Reviewed' GROUP BY DocumentId) m
          ON e.DocumentId=m.DocumentId AND e.Seq=m.s
        WHERE json_extract(e.Details,'$.domain')='Business' OR json_extract(e.Details,'$.modelDomain')='Business';
        """;
    using var dr = cmd.ExecuteReader();
    while (dr.Read()) businessIds.Add(dr.GetString(0));
}
// Legacy safety net: business documents filed BEFORE the domain step carry no modelDomain, but they were filed as
// 'Triagedokument' — a type outside DocumentCatalog that the classifier cannot produce. Excluding them keeps a
// historical ledger scoreable on the same definition as a post-domain-step one.
var excludedBusiness = perDoc.Count(x => businessIds.Contains(x.Id) || x.Human == "Triagedokument");
perDoc = perDoc.Where(x => !businessIds.Contains(x.Id) && x.Human != "Triagedokument").ToList();
if (excludedBusiness > 0)
    Console.WriteLine($"Type accuracy excludes {excludedBusiness} business-lane document(s) — scored under Domain above, not as type misses.\n");

var rows = perDoc
    .GroupBy(x => (x.Ai, x.Human, x.Disp))
    .Select(g => (Ai: g.Key.Ai, Human: g.Key.Human, Disp: g.Key.Disp, N: g.Count()))
    .ToList();

// ── Domain accuracy (medical vs business) — the FIRST of the two decisions, scored on its own ──
// Ground truth is the reviewer's checkbox, recorded on the Reviewed event as domain=Business (and, for documents
// extracted after the domain step shipped, alongside the model's own proposal as modelDomain/domainConfirmed).
// Scoring this separately is the entire point: a business document filed as 'Triagedokument' is not a TYPE error —
// the classifier cannot emit that type — so folding it into type accuracy reported 34.2% where the medical figure
// was 87.8%. Two decisions, two metrics.
Console.WriteLine("=== Domain (medical vs business) ===");
int domScored = 0, domCorrect = 0, domNoProposal = 0;
var domConfusion = new Dictionary<string, int>(StringComparer.Ordinal);
using (var cmd = db.CreateCommand())
{
    cmd.CommandText = """
        SELECT e.DocumentId,
               json_extract(e.Details,'$.domain')          AS humanDomain,
               json_extract(e.Details,'$.modelDomain')     AS modelDomain
        FROM events e
        JOIN (SELECT DocumentId, MAX(Seq) s FROM events WHERE Type='Reviewed' GROUP BY DocumentId) m
          ON e.DocumentId=m.DocumentId AND e.Seq=m.s;
        """;
    using var dr = cmd.ExecuteReader();
    while (dr.Read())
    {
        var id = dr.GetString(0);
        if (frozen is not null && !frozen.Contains(id)) continue;
        // A Reviewed event with no domain recorded is a MEDICAL review (the business lane always writes it).
        var human = dr.IsDBNull(1) ? "Medical" : dr.GetString(1);
        if (dr.IsDBNull(2)) { domNoProposal++; continue; }   // extracted before the domain step — no proposal to score
        var model = dr.GetString(2);
        domScored++;
        if (string.Equals(model, human, StringComparison.OrdinalIgnoreCase)) domCorrect++;
        else domConfusion[$"{model} -> {human}"] = domConfusion.GetValueOrDefault($"{model} -> {human}") + 1;
    }
}
if (domScored == 0)
    Console.WriteLine($"  No scoreable domain proposals yet ({domNoProposal} reviewed doc(s) predate the domain classifier).");
else
{
    Console.WriteLine($"  Domain accuracy: {domCorrect}/{domScored} = {100.0 * domCorrect / domScored:F1}%   ({domNoProposal} predate the domain step, excluded)");
    foreach (var (k, v) in domConfusion.OrderByDescending(x => x.Value)) Console.WriteLine($"    {v,4}   {k}");
}
Console.WriteLine();

var totalFiled = rows.Sum(x => x.N);
var autoFiled = rows.Where(x => x.Disp == "AutoFile").Sum(x => x.N);
var reviewed = rows.Where(x => x.Disp == "ReviewedThenFiled").ToList();
var reviewedTotal = reviewed.Sum(x => x.N);
var reviewedCorrect = reviewed.Where(x => x.Ai == x.Human).Sum(x => x.N);

Console.WriteLine($"Audit DB : {dbPath}");
Console.WriteLine($"Filed docs with a classification: {totalFiled}  (auto-filed {autoFiled}, human-reviewed {reviewedTotal})");
Console.WriteLine();

if (reviewedTotal == 0)
{
    Console.WriteLine("No 'ReviewedThenFiled' docs yet — no human-adjudicated ground truth to score against.");
    return 0;
}

Console.WriteLine($"Classification accuracy on the human-reviewed set: {reviewedCorrect}/{reviewedTotal} = {100.0 * reviewedCorrect / reviewedTotal:F1}%");
Console.WriteLine("(scored only where a human confirmed/corrected the type; auto-filed docs are unverified, excluded)");
Console.WriteLine();

Console.WriteLine("Confusions — model classified  ->  human corrected to  (reviewed set):");
var confusions = reviewed.Where(x => x.Ai != x.Human).OrderByDescending(x => x.N).ToList();
if (confusions.Count == 0) Console.WriteLine("  (none — every reviewed doc kept the model's type)");
foreach (var x in confusions)
    Console.WriteLine($"  {x.N,4}   {x.Ai,-28} -> {x.Human}");

Console.WriteLine();
Console.WriteLine("Per model-type review-correction rate:");
foreach (var g in reviewed.GroupBy(x => x.Ai).OrderByDescending(g => g.Sum(x => x.N)))
{
    var tot = g.Sum(x => x.N);
    var wrong = g.Where(x => x.Ai != x.Human).Sum(x => x.N);
    Console.WriteLine($"  {g.Key,-28} {tot,4} reviewed, {wrong,4} retyped ({(tot > 0 ? 100.0 * wrong / tot : 0):F0}%)");
}

// ── Extraction accuracy: which fields the human most often corrected (from the review diffs) ──
// The 'Reviewed' event Details is the ReviewDiff: type/patient/fields, each {was, now, changed}. We tally ONLY field
// NAMES and the `changed` flag — never the was/now VALUES (a field value can be PHI: KVNR, name, DOB, a date). This
// surfaces the extraction prompts most worth sharpening, the same way the confusion matrix did for classification.
// A changed field is one of THREE things, and lumping them together pointed this ranking at the wrong fields:
//   MATCH      — the reviewer selected a patient; the identity fields were populated from the patient record. The
//                review form overwrites KVNR/LastName/FirstName/BirthDate on load AND on selection, so a human
//                cannot express a correction to them at all. On the 2026-08-05 ledger these held the top FOUR
//                places (KVNR 72%, BirthDate 34%, LastName 24%, FirstName 19%) purely as this artefact.
//   CORRECTION — model proposed a value, human changed it. A PRECISION failure: the model was wrong.
//   FILL       — model proposed nothing, human supplied it. A RECALL failure: a different fix entirely.
// Diffs written before fieldSignals existed are classified from the same rule (identity field + a linked patient
// ⇒ match, else was-empty ⇒ fill), so historical events stay comparable.
Console.WriteLine();
Console.WriteLine("=== Extraction: what the human actually changed, by signal ===");
// Both diff shapes tally via the SHARED Adelaide.Audit.ReviewFieldTally — the legacy fields[] events AND the
// Tier-A correctedFields/fieldSignals events, which this loop previously skipped (Tier-A reviews silently
// disappeared from every count below). The identityAgreement oracle pass stays inline (shape-independent).
var fieldTally = new Adelaide.Audit.ReviewFieldTally.Result();
int agreeSamples = 0, agreed = 0;
var agreeByField = new Dictionary<string, (int Agreed, int Total)>(StringComparer.OrdinalIgnoreCase);
var shapes = new Dictionary<string, int>(StringComparer.Ordinal);

using (var cmd = db.CreateCommand())
{
    cmd.CommandText = "SELECT DocumentId, Details FROM events WHERE Type='Reviewed'";
    using var rr = cmd.ExecuteReader();
    while (rr.Read())
    {
        if (rr.IsDBNull(1)) continue;
        if (frozen is not null && !frozen.Contains(rr.GetString(0))) continue; // same frozen set as the confusion matrix
        var details = rr.GetString(1);
        Adelaide.Audit.ReviewFieldTally.Add(fieldTally, details);
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(details);
            // The oracle: model-extracted identity value vs the matched patient record's value.
            if (d.RootElement.TryGetProperty("identityAgreement", out var ia) && ia.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var a in ia.EnumerateArray())
                {
                    var fld = a.TryGetProperty("field", out var af) ? af.GetString() ?? "?" : "?";
                    var ok = a.TryGetProperty("agreed", out var ag) && ag.ValueKind == System.Text.Json.JsonValueKind.True;
                    agreeSamples++; if (ok) agreed++;
                    var cur = agreeByField.GetValueOrDefault(fld);
                    agreeByField[fld] = (cur.Agreed + (ok ? 1 : 0), cur.Total + 1);
                    if (!ok && a.TryGetProperty("shape", out var sh) && sh.ValueKind == System.Text.Json.JsonValueKind.String)
                        shapes[sh.GetString()!] = shapes.GetValueOrDefault(sh.GetString()!) + 1;
                }
        }
        catch { /* skip an unparseable diff */ }
    }
}
var seen = fieldTally.Seen; var corrections = fieldTally.Corrections; var fills = fieldTally.Fills; var matches = fieldTally.Matches;
Console.WriteLine($"Review diffs: {fieldTally.ReviewDiffs}  (legacy shape {fieldTally.OldShape}, Tier-A shape {fieldTally.NewShape}; type corrected {fieldTally.TypeCorrected}, patient changed {fieldTally.PatientCorrected})");
if (corrections.Count > 0 || fills.Count > 0 || matches.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("PRECISION + RECALL — the prompts worth sharpening (MATCH excluded: not a correction)");
    Console.WriteLine("NOTE: /seen denominators cover LEGACY-shape diffs only — Tier-A diffs name changed fields without a field list.");
    Console.WriteLine("field                        correction/seen        fill/seen");
    foreach (var k in corrections.Keys.Concat(fills.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderByDescending(k => corrections.GetValueOrDefault(k))
                 .ThenByDescending(k => fills.GetValueOrDefault(k)))
    {
        var c = corrections.GetValueOrDefault(k); var fl = fills.GetValueOrDefault(k); var n = seen.GetValueOrDefault(k);
        Console.WriteLine($"  {k,-26} {c,4}/{n,-5} {(n > 0 ? 100.0 * c / n : 0),3:F0}%   {fl,4}/{n,-5} {(n > 0 ? 100.0 * fl / n : 0),3:F0}%");
    }
    if (matches.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("MATCH — populated from the patient record when a patient was linked. NOT extraction errors:");
        foreach (var k in matches.Keys.OrderByDescending(k => matches[k]))
            Console.WriteLine($"  {k,-26} {matches[k],4}/{seen.GetValueOrDefault(k),-5}");
    }
}

if (agreeSamples > 0)
{
    Console.WriteLine();
    Console.WriteLine("=== Identity oracle: model-extracted vs the matched patient record ===");
    Console.WriteLine($"Overall agreement: {agreed}/{agreeSamples} = {100.0 * agreed / agreeSamples:F1}%   (deterministic ground truth; no human judgement)");
    foreach (var (k, v) in agreeByField.OrderBy(x => x.Value.Total == 0 ? 1.0 : (double)x.Value.Agreed / x.Value.Total))
        Console.WriteLine($"  {k,-26} {v.Agreed,4}/{v.Total,-5} {(v.Total > 0 ? 100.0 * v.Agreed / v.Total : 0),3:F0}%");
    if (shapes.Count > 0)
    {
        Console.WriteLine("  how they disagreed (structural; d=digit a=letter x=other, no values):");
        foreach (var (k, v) in shapes.OrderByDescending(x => x.Value).Take(10)) Console.WriteLine($"    {v,4}  {k}");
    }
}
else
{
    Console.WriteLine();
    Console.WriteLine("=== Identity oracle (live review diffs): no samples yet — no diff carries identityAgreement. ===");
}

// ── The persisted oracle: backfilled and/or live, reported by SOURCE ─────────────────────────────────────────────
// Kept separate from the live-event tally above because the two have different coverage: the backfill can only see
// documents whose review sidecar still exists, so it is NOT a random sample of the corpus and must not be silently
// pooled with live measurements.
if (File.Exists(oracleDbPath))
{
    var oracle = new Adelaide.Audit.IdentityOracleStore(oracleDbPath);
    var tally = await oracle.TallyAsync();
    if (tally.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Identity oracle (persisted): model-extracted vs the matched patient record ===");
        Console.WriteLine($"store: {oracleDbPath}");
        Console.WriteLine("Deterministic ground truth — two values already held, no human judgement, no consent question.");
        foreach (var bySource in tally.GroupBy(t => t.Source))
        {
            var srcAgreed = bySource.Sum(t => t.Agreed);
            var total = bySource.Sum(t => t.Total);
            Console.WriteLine();
            Console.WriteLine($"  source={bySource.Key}   overall {srcAgreed}/{total} = {(total > 0 ? 100.0 * srcAgreed / total : 0):F1}%");
            Console.WriteLine("  field                      agreed/pairs   agreement");
            foreach (var t in bySource.OrderBy(t => t.Total == 0 ? 1.0 : (double)t.Agreed / t.Total))
                Console.WriteLine($"    {t.Field,-24} {t.Agreed,5}/{t.Total,-6} {(t.Total > 0 ? 100.0 * t.Agreed / t.Total : 0),5:F1}%");
        }

        var storedShapes = await oracle.ShapesAsync();
        if (storedShapes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  HOW the model misreads (structural; d=digit a=letter x=other — no values):");
            foreach (var (field, shape, n) in storedShapes.Take(12))
                Console.WriteLine($"    {n,4}  {field,-12} {shape}");
        }
    }
}
return 0;
