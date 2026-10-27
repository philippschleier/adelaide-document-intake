using System.Text.Json.Nodes;
using Adelaide.Core;

namespace Adelaide.Web;

/// <summary>Turns one review submission into the per-field capture rows (docs/CAPTURE.md): the value pair
/// (proposed vs filed) for EVERY field present on either side — unchanged pairs included, they are the MATCH class
/// calibration needs — plus the structural signal. Built on the SAME change detection as the Reviewed event
/// (<see cref="ReviewDiff.CorrectedFields"/> / <see cref="ReviewFieldSignals"/>), so the stores and the ledger can
/// never disagree about which fields changed. Pure computation + fail-open writers; never consulted by any
/// decision.</summary>
internal static class ReviewCapture
{
    internal sealed record FieldRecord(string FieldName, string? ProposedValue, string? FinalValue, string Signal);

    /// <summary>Every field in (model proposal ∪ human submission), each with its value pair and signal.
    /// ContentType and Domain are handled explicitly (their "human side" lives on dedicated submission properties,
    /// not the Fields map); "patient" is not a field and is excluded (tracked on its own event key).</summary>
    internal static IReadOnlyList<FieldRecord> ComputeFieldRecords(JsonNode model, ReviewSubmission human)
    {
        // RAW (unfiltered) on purpose: these records stamp corpus_fields rows that intake already wrote under the
        // extractor's own names. Using the ledger-filtered map here would leave any pre-gate row unstampable and
        // permanently 'pending'. Confidentiality is enforced at the extractor (FieldVocabulary), not here.
        var modelFields = ReviewDiff.ModelFieldMapRaw(model);
        var humanFields = human.Fields ?? new Dictionary<string, string?>();
        var changed = new HashSet<string>(ReviewDiff.CorrectedFields(model, human), StringComparer.OrdinalIgnoreCase);
        var patientLinked = human.PatientListItemId > 0;

        static string? Norm(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        var outp = new List<FieldRecord>();

        // ContentType: the proposal is the sidecar's top-level classification (the same value CorrectedFields
        // compares); the human side is the dedicated ContentType property.
        var proposedType = Norm(model["contentType"]?.GetValue<string>());
        var finalType = Norm(human.ContentType);
        if (proposedType is not null || finalType is not null)
            outp.Add(new FieldRecord("ContentType", proposedType, finalType, changed.Contains("contentType") ? "correction" : "match"));

        // Domain: the human side is the IsBusiness checkbox, not a Fields entry (the round-tripped Fields["Domain"]
        // is just the prefill echo, so the generic loop below would always call it a match).
        var proposedDomain = Norm(modelFields.TryGetValue("Domain", out var pd) ? pd : null);
        var finalDomain = human.IsBusiness ? DocumentDomain.Business : DocumentDomain.Medical;
        if (proposedDomain is not null)
            outp.Add(new FieldRecord("Domain", proposedDomain, finalDomain,
                string.Equals(proposedDomain, finalDomain, StringComparison.OrdinalIgnoreCase) ? "match" : "correction"));

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // preserve the first-seen casing
        foreach (var k in modelFields.Keys) names.TryAdd(k, k);
        foreach (var k in humanFields.Keys) names.TryAdd(k, k);
        names.Remove("ContentType");
        names.Remove("Domain");

        foreach (var name in names.Values)
        {
            if (name.Length > 128) continue; // NVARCHAR(128) bound on both field tables; a longer name is garbage input
            var proposed = Norm(modelFields.TryGetValue(name, out var mv) ? mv : null);
            var final = Norm(humanFields.TryGetValue(name, out var hv) ? hv : null);
            if (proposed is null && final is null) continue; // nothing proposed, nothing filed — no record
            var signal = changed.Contains(name)
                ? ReviewFieldSignals.Classify(name, proposed is not null, patientLinked).Tag()
                : "match";
            outp.Add(new FieldRecord(name, proposed, final, signal));
        }
        return outp;
    }

    /// <summary>The provenance enum a submission may claim — anything else is dropped, not stored. 'unchanged' is
    /// reserved for the server side (a field present in the proposal the client never wrote at all).</summary>
    private static readonly HashSet<string> AllowedProvenance = new(StringComparer.OrdinalIgnoreCase)
        { "prefill", "patient", "typed", "unchanged" };

    /// <summary>Validate the client's last-writer map before anything records it: keep only fields that exist on the
    /// proposal or the submission, and only the known enum values (normalized lowercase). Null/empty in → null out,
    /// so an old client changes nothing. Keys keep the canonical column casing so the adjudication rows match.
    ///
    /// PRECISELY WHICH LAYER HOLDS WHICH PROPERTY — the docstring used to compress this into one wrong sentence
    /// ("a client cannot mint arbitrary strings into the hash-chained event"), and the compression mattered:
    ///  • Upstream, ReviewGuard.Prune drops and ReviewGuard.Validate refuses any key outside the lane's CLOSED
    ///    VOCABULARY — for `Fields` AND `FieldProvenance`, on BOTH lanes. That is what makes "not arbitrary" true.
    ///    (Until the C6 verification round the business lane was exempt, and provenance keys were filtered only as
    ///    a side effect of a Fields key being dropped, so an ordinary submission carried them through ungated.)
    ///  • This filter cannot supply that property on its own: its `known` set is the model map ∪ the SUBMITTED
    ///    keys, so any key the client sent is "known" by construction. Do not read it as the vocabulary gate.
    ///  • What it DOES supply, and what the layer above cannot: provenance entries are dropped unless the field is
    ///    actually part of THIS review (present on the proposal or the submission), writer values are constrained to
    ///    a closed enum, and the key is canonicalised to the column casing the adjudication rows use.
    ///  • The residual this docstring used to record — names originating in the MODEL's own sidecar being
    ///    vocabulary-checked NOWHERE — is closed at the EXTRACTOR (FieldVocabulary), which is the only point
    ///    upstream of both the ledger and the intake corpus_fields write. ReviewDiff.ModelFieldMap re-applies the
    ///    same set for the ledger paths, so the `known` set built below cannot canonicalise an off-vocabulary key
    ///    into fieldProvenance. Note what is NOT claimed: corpus_fields rows written BEFORE that gate shipped still
    ///    hold the extractor's raw names, and ComputeFieldRecords reads the sidecar UNFILTERED so it can still
    ///    stamp them.</summary>
    internal static Dictionary<string, string>? SanitizeProvenance(JsonNode model, ReviewSubmission human)
    {
        if (human.FieldProvenance is not { Count: > 0 } claimed) return null;
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in ReviewDiff.ModelFieldMap(model, human.IsBusiness).Keys) known.TryAdd(k, k);
        foreach (var k in (human.Fields ?? new Dictionary<string, string?>()).Keys) known.TryAdd(k, k);

        var outp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, writer) in claimed)
            if (known.TryGetValue(field, out var canonical) && AllowedProvenance.Contains(writer))
                outp[canonical] = writer.ToLowerInvariant();
        return outp.Count > 0 ? outp : null;
    }

    /// <summary>Set the review label AND CHECK THAT IT LANDED. SetLabelAsync returning false means the document has
    /// no corpus row — the exact condition that silently no-opped for every filing before this build (the swallow at
    /// the review endpoint was the anti-pattern the fail-open-but-counted rule exists to kill). false → WARN + the
    /// labels-missed counter; exception → WARN + the failure counter. Never throws.</summary>
    internal static async Task SetLabelCheckedAsync(Adelaide.Audit.ITranscriptStore? store, CaptureHealth? health,
        ILogger log, string docId, string? label, string? labelDomain, string? driveItemId, DateTimeOffset now, CancellationToken ct)
    {
        if (store is null) return;
        if (label?.Length > 128)
        {
            // NVARCHAR(128) bound on measure.corpus.Label — a longer value is garbage input, but the row must
            // still become scoreable rather than fail the write on one substrate and land on the other.
            log.LogWarning("Corpus label for {DocId} exceeds 128 chars ({Length}); truncated for capture.", docId, label.Length);
            label = label[..128];
        }
        try
        {
            if (!await store.SetLabelAsync(docId, label, labelDomain, driveItemId, now, ct))
            {
                health?.CountLabelMissed();
                log.LogWarning("Corpus label did NOT land for {DocId}: no corpus row exists (document predates capture, or its capture write failed). The label is lost for this document.", docId);
            }
        }
        catch (Exception ex)
        {
            health?.CountFailure("label");
            log.LogWarning("Corpus label write failed for {DocId} ({ExType}); the filing itself is unaffected.", docId, ex.GetType().Name);
        }
    }

    /// <summary>The reconcile-recovery twin of the inline review capture: a crash-recovered review filing must land
    /// its label AND its field outcomes/adjudications — the recovery block previously wrote neither. Best-basis
    /// reconstruction: when the review sidecar still exists (recovery runs before the purge), the FULL inline path
    /// is replayed from it; when it is gone, outcomes come from the recovery record's stored ReviewDiff
    /// (fieldSignals name the changed fields; everything else the spec filed was an accepted proposal → MATCH) and
    /// adjudications carry FinalValue only (the proposal is unrecoverable without the sidecar — recorded as such,
    /// never guessed).</summary>
    internal static async Task RecordRecoveredReviewAsync(Adelaide.Audit.ITranscriptStore? store, CaptureHealth? health,
        ILogger log, string docId, string driveItemId, UploadProcessor.FilingRecovery rec, string pdfPath,
        DateTimeOffset now, CancellationToken ct)
    {
        if (store is null || rec.Reviewer is null) return;

        var isBusiness = rec.Spec.Target is not null; // only business specs carry a destination target
        await SetLabelCheckedAsync(store, health, log, docId, rec.Spec.ContentType,
            isBusiness ? DocumentDomain.Business : DocumentDomain.Medical, driveItemId, now, ct);

        var sidecarPath = Path.ChangeExtension(pdfPath, ".review.json");
        if (File.Exists(sidecarPath))
        {
            try
            {
                var model = JsonNode.Parse(await File.ReadAllTextAsync(sidecarPath, ct))!;
                var body = new ReviewSubmission(docId, rec.Spec.ContentType, rec.PatientLookupId ?? 0,
                    SpecFieldsAsStrings(rec.Spec.Fields), IsBusiness: isBusiness);
                await RecordReviewOutcomesAsync(store, health, log, docId, model, body, provenance: null, now, ct);
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning("Recovered-review sidecar replay failed for {DocId} ({ExType}); falling back to the stored diff.", docId, ex.GetType().Name);
            }
        }

        // Sidecar gone: the stored diff names the changed fields; the spec's fields are the filed values.
        try
        {
            var specFields = SpecFieldsAsStrings(rec.Spec.Fields);
            // Canonicalize the diff's camelCased names to the spec's column casing BEFORE they become row
            // identities: the SQLite PK is case-sensitive while the SQL twin merges case-insensitively, so a
            // 'documentDate' row written here would diverge across substrates once extraction re-puts
            // 'DocumentDate' (verification finding). A diff name absent from the spec (a field the reviewer
            // cleared) keeps its casing — it carries no filed value and never meets a re-put identity.
            var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in specFields.Keys) canonical.TryAdd(k, k);
            var outcomes = new List<(string, string)>();
            if (rec.ReviewDiffJson is not null)
            {
                using var diff = System.Text.Json.JsonDocument.Parse(rec.ReviewDiffJson);
                if (diff.RootElement.TryGetProperty("fieldSignals", out var fs) && fs.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var s in fs.EnumerateArray())
                        if (s.TryGetProperty("field", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String
                            && s.TryGetProperty("signal", out var sig) && sig.ValueKind == System.Text.Json.JsonValueKind.String)
                            outcomes.Add((canonical.TryGetValue(f.GetString()!, out var canon) ? canon : f.GetString()!,
                                sig.GetString()!.ToUpperInvariant()));
            }
            await store.SetFieldOutcomesAsync(docId, outcomes, remainingOutcome: "MATCH", now, ct);

            var signalOf = outcomes.ToDictionary(o => o.Item1, o => o.Item2.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
            await store.PutAdjudicationsAsync(docId, specFields
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Select(kv => new Adelaide.Audit.FieldAdjudicationRow(
                    kv.Key, ProposedValue: null, FinalValue: kv.Value, // proposal unrecoverable without the sidecar — not guessed
                    signalOf.TryGetValue(kv.Key, out var sg) ? sg : "match", Provenance: null)), now, ct);
        }
        catch (Exception ex)
        {
            health?.CountFailure("recovered-review");
            log.LogWarning("Recovered-review capture failed for {DocId} ({ExType}); the recovered filing itself is unaffected.", docId, ex.GetType().Name);
        }
    }

    /// <summary>The filing spec's native-typed fields back to submission-shaped strings. Graph date-times
    /// ("2026-05-01T00:00:00Z") are collapsed to the date the review form would have submitted, so the shared
    /// change detection cannot mistake the round-trip formatting for a correction.</summary>
    private static Dictionary<string, string?> SpecFieldsAsStrings(IReadOnlyDictionary<string, object?> fields)
    {
        var outp = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in fields)
        {
            if (string.Equals(k, "PatientLookupId", StringComparison.OrdinalIgnoreCase)) continue; // a lookup id, not an extraction field
            var s = v?.ToString();
            if (s is not null
                && DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                s = dt.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            outp[k] = s;
        }
        return outp;
    }

    /// <summary>Write the review's per-field capture: corpus_fields outcomes (non-PHI; FILL fields the model never
    /// proposed are inserted) and the field_adjudications value pairs (PHI). Fail-open but counted per the capture
    /// hard rule — a store failure warns with the document id and increments the counter, never fails the filing
    /// that already succeeded. <paramref name="provenance"/> is the client's observed last-writer map (WP4);
    /// null entries leave the column null.</summary>
    internal static async Task RecordReviewOutcomesAsync(Adelaide.Audit.ITranscriptStore? store, CaptureHealth? health,
        ILogger log, string docId, JsonNode model, ReviewSubmission body,
        IReadOnlyDictionary<string, string>? provenance, DateTimeOffset now, CancellationToken ct)
    {
        if (store is null) return;
        IReadOnlyList<FieldRecord> records;
        try
        {
            records = ComputeFieldRecords(model, body);
        }
        catch (Exception ex)
        {
            health?.CountFailure("field-outcomes");
            log.LogWarning("Per-field capture computation failed for {DocId} ({ExType}); outcomes and adjudications not recorded.", docId, ex.GetType().Name);
            return;
        }

        try
        {
            // The review adjudicates exactly the fields it saw: specific outcomes only, no blanket — a stray row
            // from an older processing attempt stays 'pending' (visible in stats) rather than getting a false MATCH.
            await store.SetFieldOutcomesAsync(docId,
                records.Select(r => (r.FieldName, r.Signal.ToUpperInvariant())).ToList(), remainingOutcome: null, now, ct);
        }
        catch (Exception ex)
        {
            health?.CountFailure("corpus_fields");
            log.LogWarning("corpus_fields outcome write failed for {DocId} ({ExType}); the filing itself is unaffected.", docId, ex.GetType().Name);
        }

        try
        {
            string? ProvOf(string field) =>
                provenance is not null && provenance.TryGetValue(field, out var p) ? p : null;
            await store.PutAdjudicationsAsync(docId,
                records.Select(r => new Adelaide.Audit.FieldAdjudicationRow(
                    r.FieldName, r.ProposedValue, r.FinalValue, r.Signal, ProvOf(r.FieldName))), now, ct);
        }
        catch (Exception ex)
        {
            health?.CountFailure("adjudications");
            log.LogWarning("field_adjudications write failed for {DocId} ({ExType}); the filing itself is unaffected.", docId, ex.GetType().Name);
        }
    }
}
