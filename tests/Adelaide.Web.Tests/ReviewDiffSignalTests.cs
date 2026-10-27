using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Adelaide.Web.Tests;

// End-to-end over the real ReviewDiff: the diff must now say WHY each field changed, and must carry the
// model-vs-patient-record comparison. Synthetic identifiers only (project fixture rule).
public class ReviewDiffSignalTests
{
    private const string ModelKvnr = "Z00000000S";   // what the model "read"
    private const string RecordKvnr = "Z000000005";  // what the patient record holds

    private static JsonNode Model(string? modelKvnr = ModelKvnr, int candidateId = 7, bool withCandidate = true) =>
        new JsonObject
        {
            ["contentType"] = "Verordnung",
            ["fields"] = new JsonArray
            {
                new JsonObject { ["name"] = "KVNR", ["value"] = modelKvnr },
                new JsonObject { ["name"] = "LastName", ["value"] = "Mustermann" },
                new JsonObject { ["name"] = "DocumentDate", ["value"] = "2026-05-01" },
                new JsonObject { ["name"] = "ApprovalNumber", ["value"] = null },
            },
            ["match"] = withCandidate
                ? new JsonObject
                {
                    ["candidate"] = new JsonObject
                    {
                        ["listItemId"] = candidateId,
                        ["kvnr"] = RecordKvnr,
                        ["lastName"] = "Mustermann",
                        ["firstName"] = "Max",
                        ["birthDate"] = "1950-01-01",
                    },
                }
                : new JsonObject(),
        };

    private static ReviewSubmission Sub(Dictionary<string, string?> fields, int patientId = 7) =>
        new("doc1", "Verordnung", patientId, fields);

    private static JsonElement Diff(JsonNode model, ReviewSubmission human) =>
        JsonDocument.Parse(JsonSerializer.Serialize(ReviewDiff.Compute(model, human))).RootElement.Clone();

    private static Dictionary<string, string> Signals(JsonElement d) =>
        d.GetProperty("fieldSignals").EnumerateArray()
         .ToDictionary(x => x.GetProperty("field").GetString()!, x => x.GetProperty("signal").GetString()!);

    // The headline behaviour: the reviewer picked a patient, KVNR was overwritten from the record. That is a MATCH.
    [Fact]
    public void An_overwritten_identity_field_is_reported_as_a_match()
    {
        var d = Diff(Model(), Sub(new() { ["KVNR"] = RecordKvnr, ["DocumentDate"] = "2026-05-01" }));
        Assert.Equal("match", Signals(d)["kvnr"]);
    }

    [Fact]
    public void A_changed_extraction_field_is_reported_as_a_correction()
    {
        var d = Diff(Model(), Sub(new() { ["KVNR"] = RecordKvnr, ["DocumentDate"] = "2026-06-15" }));
        Assert.Equal("correction", Signals(d)["documentDate"]);
    }

    [Fact]
    public void A_field_the_model_left_empty_is_reported_as_a_fill()
    {
        var d = Diff(Model(), Sub(new() { ["KVNR"] = RecordKvnr, ["DocumentDate"] = "2026-05-01", ["ApprovalNumber"] = "G12345" }));
        Assert.Equal("fill", Signals(d)["approvalNumber"]);
    }

    // correctedFields is unchanged — existing readers and the PHI forward-strip depend on it. The new signal sits
    // ALONGSIDE it rather than replacing it.
    [Fact]
    public void The_existing_correctedFields_shape_is_preserved()
    {
        var d = Diff(Model(), Sub(new() { ["KVNR"] = RecordKvnr, ["DocumentDate"] = "2026-06-15" }));
        var corrected = d.GetProperty("correctedFields").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("kvnr", corrected);
        Assert.Contains("documentDate", corrected);
    }

    // ---- the oracle ----

    [Fact]
    public void The_oracle_records_the_model_vs_record_disagreement_structurally()
    {
        var d = Diff(Model(), Sub(new() { ["KVNR"] = RecordKvnr }));
        var kvnr = d.GetProperty("identityAgreement").EnumerateArray().Single(x => x.GetProperty("field").GetString() == "KVNR");
        Assert.False(kvnr.GetProperty("agreed").GetBoolean());
        Assert.Equal("a->d@9", kvnr.GetProperty("shape").GetString());   // 'S'(letter) vs '5'(digit) at index 9
    }

    [Fact]
    public void The_oracle_records_agreement_where_the_model_read_correctly()
    {
        var d = Diff(Model(), Sub(new() { ["KVNR"] = RecordKvnr }));
        var last = d.GetProperty("identityAgreement").EnumerateArray().Single(x => x.GetProperty("field").GetString() == "LastName");
        Assert.True(last.GetProperty("agreed").GetBoolean());
    }

    // A field the model never read is not an oracle sample — that is a recall gap, counted as FILL, not a wrong read.
    [Fact]
    public void A_field_the_model_did_not_read_is_not_an_oracle_sample()
    {
        var d = Diff(Model(), Sub(new() { ["KVNR"] = RecordKvnr }));
        var fields = d.GetProperty("identityAgreement").EnumerateArray().Select(x => x.GetProperty("field").GetString()).ToList();
        Assert.DoesNotContain("FirstName", fields);   // the model read no FirstName; the record has one
    }

    // If the reviewer picked a DIFFERENT patient we do not hold that patient's values, so no oracle is emitted. A
    // wrong oracle would be worse than a missing one.
    [Fact]
    public void No_oracle_is_emitted_when_the_reviewer_chose_a_different_patient()
    {
        var d = Diff(Model(candidateId: 7), Sub(new() { ["KVNR"] = RecordKvnr }, patientId: 99));
        Assert.Empty(d.GetProperty("identityAgreement").EnumerateArray());
    }

    [Fact]
    public void No_oracle_is_emitted_when_there_was_no_matched_candidate()
    {
        var d = Diff(Model(withCandidate: false), Sub(new() { ["KVNR"] = RecordKvnr }));
        Assert.Empty(d.GetProperty("identityAgreement").EnumerateArray());
    }

    // The whole diff must stay PHI-free: names and structural descriptors only, never a value.
    [Fact]
    public void The_diff_contains_no_patient_values()
    {
        var json = JsonSerializer.Serialize(ReviewDiff.Compute(Model(), Sub(new() { ["KVNR"] = RecordKvnr, ["LastName"] = "Mustermann" })));
        Assert.DoesNotContain(RecordKvnr, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ModelKvnr, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mustermann", json, StringComparison.OrdinalIgnoreCase);
    }
}
