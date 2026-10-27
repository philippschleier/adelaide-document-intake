using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Adelaide.Web.Tests;

// Field NAMES are ledger inputs (correctedFields / fieldSignals[].field / the canonical key behind
// fieldProvenance[].field), and every downstream reader treats a "name" as inherently non-identifying.
// The extractor drops non-schema columns at the source for a CATALOG type, but the bare FALLBACK type has no
// schema to check against, so a model-authored column name reached the append-only Reviewed event unchecked —
// the review endpoint's vocabulary prune was never the backstop for it, because that prune only gates keys the
// CLIENT submitted and a model-invented name never passes through a submission.
//
// These tests drive ReviewDiff directly with a sidecar shaped the way the extractor emits one for a fallback-typed
// document, i.e. carrying a column name the catalog does not define.
public class ReviewModelFieldVocabularyTests
{
    // The task's own example: free prose carrying a patient name, as a model-authored "column".
    private const string ProseName = "Versichertennummer Anna Müller";
    // Identifier-shaped and therefore PAST the charset floor — only the closed vocabulary can refuse this one.
    // This is the case that proves the fix is the vocabulary, not merely ValidFieldName.
    private const string IdentifierShapedName = "VersichertennummerAnnaMueller";

    private static JsonNode Model(string contentType, int patientId, params (string name, string? value)[] fields)
    {
        var arr = new JsonArray();
        foreach (var (n, v) in fields) arr.Add(new JsonObject { ["name"] = n, ["value"] = v });
        return new JsonObject
        {
            ["contentType"] = contentType,
            ["fields"] = arr,
            ["match"] = new JsonObject { ["candidate"] = new JsonObject { ["listItemId"] = patientId } },
        };
    }

    private static ReviewSubmission Sub(string contentType, int patientId, Dictionary<string, string?> fields,
        Dictionary<string, string>? provenance = null)
        => new("doc1", contentType, patientId, fields, FieldProvenance: provenance);

    [Theory]
    [InlineData(ProseName)]
    [InlineData(IdentifierShapedName)]
    public void Model_authored_off_vocabulary_column_never_reaches_correctedFields(string offVocabulary)
    {
        // The model proposed a value the human never submitted — the exact shape that used to look "cleared" and
        // therefore be recorded as a human correction under the model's own invented name.
        var model = Model("Allgemeine Korrespondenz", 1, ("DocumentDate", "2026-07-02"), (offVocabulary, "A123456789"));
        var sub = Sub("Allgemeine Korrespondenz", 1, new() { ["DocumentDate"] = "2026-07-02" });

        var corrected = ReviewDiff.CorrectedFields(model, sub);

        Assert.DoesNotContain(corrected, c => c.Contains("Müller", StringComparison.OrdinalIgnoreCase)
                                           || c.Contains("Mueller", StringComparison.OrdinalIgnoreCase)
                                           || c.Contains("Anna", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(corrected); // and it is not counted as a correction at all: the reviewer changed nothing
    }

    [Theory]
    [InlineData(ProseName)]
    [InlineData(IdentifierShapedName)]
    public void Off_vocabulary_model_column_appears_nowhere_in_the_recorded_diff(string offVocabulary)
    {
        // Covers every key of the Reviewed payload at once — correctedFields, fieldSignals, fieldProvenance,
        // identityAgreement — by asserting on the serialized record the ledger would actually store.
        var model = Model("Allgemeine Korrespondenz", 1, ("DocumentDate", "2026-07-02"), (offVocabulary, "A123456789"));
        var sub = Sub("Allgemeine Korrespondenz", 1,
            new() { ["DocumentDate"] = "2026-07-05" },                      // a real correction, so the diff is non-empty
            provenance: new() { [offVocabulary] = "typed" });               // and the client claims provenance for it

        var json = JsonSerializer.Serialize(ReviewDiff.Compute(model, sub));

        Assert.DoesNotContain(offVocabulary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mueller", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Anna", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("documentDate", json, StringComparison.Ordinal); // the legitimate correction still records
    }

    // ---- Stored-sidecar replay -------------------------------------------------------------------------------------
    // Reconcile replays a .filing.json diff as a NEW hash-chained "Reviewed" event. A sidecar written BEFORE the
    // extractor gate shipped still carries whatever the model authored, so the replay is its own door into the ledger.

    [Theory]
    [InlineData(ProseName)]
    [InlineData(IdentifierShapedName)]
    public void Stored_diff_replay_drops_off_vocabulary_names_from_every_shape_that_carries_one(string offVocabulary)
    {
        var diff = (JsonObject)JsonNode.Parse($$"""
        {
          "correctedFields": ["documentDate", {{JsonSerializer.Serialize(offVocabulary)}}],
          "fieldSignals":    [{"field":"documentDate","signal":"correction"},
                              {"field":{{JsonSerializer.Serialize(offVocabulary)}},"signal":"fill"}],
          "fieldProvenance": [{"field":{{JsonSerializer.Serialize(offVocabulary)}},"provenance":"typed"}],
          "domain": "Medical"
        }
        """)!;

        var dropped = ReviewDiff.SanitizeStoredDiff(diff);

        Assert.Equal(3, dropped);
        var json = diff.ToJsonString();
        Assert.DoesNotContain(offVocabulary, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("documentDate", json, StringComparison.Ordinal);   // the legitimate correction survives
    }

    [Fact]
    public void Stored_diff_replay_keeps_the_diffs_own_tokens_contentType_and_patient()
    {
        // REGRESSION GUARD, and the trap in this fix: "contentType" and "patient" are ReviewDiff's own tokens for a
        // type correction and a patient reassignment — NOT catalog columns. "patient" is in no vocabulary, so a naive
        // filter would silently discard a real correction signal, which is the failure mode this whole slice is about.
        var diff = (JsonObject)JsonNode.Parse("""
        {"correctedFields":["contentType","patient","kvnr"],"domain":"Medical"}
        """)!;

        var dropped = ReviewDiff.SanitizeStoredDiff(diff);

        Assert.Equal(0, dropped);
        var kept = diff["correctedFields"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "contentType", "patient", "kvnr" }, kept);
    }

    [Fact]
    public void Stored_diff_replay_reads_the_lane_from_the_diff_and_defaults_to_the_stricter_one()
    {
        // A business-lane sidecar may legitimately name a business filing column; a diff with no `domain` (pre
        // domain-step) must fall back to MEDICAL, the subset — fail-safe, never the wider vocabulary by default.
        var business = (JsonObject)JsonNode.Parse("""{"correctedFields":["zielbereich"],"domain":"Business"}""")!;
        var noDomain = (JsonObject)JsonNode.Parse("""{"correctedFields":["zielbereich"]}""")!;

        var businessDropped = ReviewDiff.SanitizeStoredDiff(business);
        var medicalDropped = ReviewDiff.SanitizeStoredDiff(noDomain);

        Assert.Equal(0, businessDropped);                       // business column on the business lane: kept
        Assert.Equal(1, medicalDropped);                        // same name with no lane stated: refused
    }

    [Fact]
    public void In_vocabulary_model_columns_are_unaffected()
    {
        // Regression guard: the filter must not narrow a normal review. KVNR is an identity field, DocumentDate a
        // server-added provenance field — both in the closed vocabulary.
        var model = Model("Verordnung", 1, ("DocumentDate", "2026-02-07"), ("KVNR", "Z000000005"));
        var sub = Sub("Verordnung", 1, new() { ["DocumentDate"] = "2026-07-02", ["KVNR"] = "Z000000009" });

        var corrected = ReviewDiff.CorrectedFields(model, sub);

        Assert.Contains("documentDate", corrected);
        Assert.Contains("kvnr", corrected);
    }

    [Theory]
    [InlineData(ProseName)]
    [InlineData(IdentifierShapedName)]
    public void Off_vocabulary_model_names_are_counted_so_the_exclusion_is_observable(string offVocabulary)
    {
        // Counted, never named — a hallucinated "column" can be arbitrary text, so the endpoint logs only this number.
        var model = Model("Allgemeine Korrespondenz", 1, ("DocumentDate", "2026-07-02"), (offVocabulary, "A123456789"));

        Assert.Equal(1, ReviewDiff.OffVocabularyModelFields(model, isBusiness: false));
        Assert.Equal(0, ReviewDiff.OffVocabularyModelFields(
            Model("Verordnung", 1, ("DocumentDate", "2026-07-02"), ("KVNR", "Z000000005")), isBusiness: false));
    }
}
