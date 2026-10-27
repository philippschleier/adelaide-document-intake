using System.Text.Json;

namespace Adelaide.Audit;

/// <summary>Per-field correction/fill/match tally over Reviewed-event diffs, handling BOTH diff shapes — the
/// legacy <c>fields[{field,was,now,changed}]</c> events AND the Tier-A <c>correctedFields[] + fieldSignals[]</c>
/// events, which ClassifyEval previously skipped entirely (its tallies silently under-counted every review
/// written after the Tier-A shape shipped; INVESTIGATION.md §3.8). Legacy events with no explicit signal are
/// classified by the same rule the live path uses (identity field + linked patient ⇒ match, was-empty ⇒ fill),
/// so historical and current events stay comparable. PHI-safe: consumes field NAMES and flags only.</summary>
public static class ReviewFieldTally
{
    public sealed class Result
    {
        /// <summary>Per-field denominators — LEGACY-shape events only: Tier-A diffs carry changed fields, not the
        /// full field list, so they cannot contribute to a seen count. Report rates with that caveat.</summary>
        public Dictionary<string, int> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Corrections { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Fills { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Matches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ReviewDiffs { get; internal set; }
        public int NewShape { get; internal set; }
        public int OldShape { get; internal set; }
        public int TypeCorrected { get; internal set; }
        public int PatientCorrected { get; internal set; }
    }

    private static bool IsIdentity(string f) =>
        f.Equals("kvnr", StringComparison.OrdinalIgnoreCase) || f.Equals("lastName", StringComparison.OrdinalIgnoreCase)
        || f.Equals("firstName", StringComparison.OrdinalIgnoreCase) || f.Equals("birthDate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Tally one Reviewed event's Details JSON into <paramref name="r"/>. Unparseable diffs are skipped.</summary>
    public static void Add(Result r, string detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson)) return;
        r.ReviewDiffs++;
        try
        {
            using var d = JsonDocument.Parse(detailsJson);
            var root = d.RootElement;

            if (root.TryGetProperty("correctedFields", out var cf) && cf.ValueKind == JsonValueKind.Array)
            {
                // Tier-A shape: names of the changed fields + their explicit signals.
                r.NewShape++;
                var names = cf.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
                if (names.Contains("contentType", StringComparer.Ordinal)) r.TypeCorrected++;
                if (names.Contains("patient", StringComparer.Ordinal)) r.PatientCorrected++;

                var signals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("fieldSignals", out var fs) && fs.ValueKind == JsonValueKind.Array)
                    foreach (var s in fs.EnumerateArray())
                        if (s.TryGetProperty("field", out var sf) && sf.ValueKind == JsonValueKind.String
                            && s.TryGetProperty("signal", out var sv) && sv.ValueKind == JsonValueKind.String)
                            signals[sf.GetString()!] = sv.GetString()!;

                foreach (var name in names)
                {
                    if (name is "contentType" or "patient") continue; // tracked on their own counters above
                    // A Tier-A diff always writes fieldSignals beside correctedFields; the fallback mirrors the
                    // writer's own rule for robustness against a hand-edited fixture.
                    var signal = signals.TryGetValue(name, out var sig) ? sig : IsIdentity(name) ? "match" : "correction";
                    Bucket(r, signal)[name] = Bucket(r, signal).GetValueOrDefault(name) + 1;
                }
                return;
            }

            if (root.TryGetProperty("fields", out var fa) && fa.ValueKind == JsonValueKind.Array)
            {
                // Legacy shape: the full field list with was/now/changed — classified by the live rule.
                r.OldShape++;
                if (root.TryGetProperty("type", out var t) && t.TryGetProperty("changed", out var tc) && tc.ValueKind == JsonValueKind.True) r.TypeCorrected++;
                var patientLinked = root.TryGetProperty("patient", out var p)
                                    && p.TryGetProperty("now", out var pn) && pn.ValueKind == JsonValueKind.Number && pn.GetInt32() > 0;
                if (root.TryGetProperty("patient", out var p2) && p2.TryGetProperty("changed", out var pc) && pc.ValueKind == JsonValueKind.True) r.PatientCorrected++;

                foreach (var f in fa.EnumerateArray())
                {
                    var name = f.TryGetProperty("field", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    r.Seen[name] = r.Seen.GetValueOrDefault(name) + 1;
                    if (!(f.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.True)) continue;
                    var wasEmpty = !f.TryGetProperty("was", out var w)
                                   || w.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                                   || (w.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(w.GetString()));
                    var signal = patientLinked && IsIdentity(name) ? "match" : wasEmpty ? "fill" : "correction";
                    Bucket(r, signal)[name] = Bucket(r, signal).GetValueOrDefault(name) + 1;
                }
            }
            // Business-lane domain diffs (neither shape) carry no field arrays — counted in ReviewDiffs only.
        }
        catch (JsonException) { /* skip an unparseable diff */ }
    }

    private static Dictionary<string, int> Bucket(Result r, string signal) => signal switch
    {
        "match" => r.Matches,
        "fill" => r.Fills,
        _ => r.Corrections,
    };
}
