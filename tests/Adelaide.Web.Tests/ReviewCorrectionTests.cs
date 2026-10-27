using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Adelaide.Web.Tests;

// Tier A: the PHI-free correction record. correctedFields = camelCase field NAMES a human changed (never values);
// contentType = the FINAL/corrected type (slice dimension); contentTypeChange {from,to} = the classification confusion
// matrix, recorded ONLY on a type correction (contentType is non-PHI). Normalization prevents spurious corrections.
public class ReviewCorrectionTests
{
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
    private static ReviewSubmission Sub(string contentType, int patientId, Dictionary<string, string?> fields)
        => new("doc1", contentType, patientId, fields);

    // (a) editing DocumentDate → correctedFields contains "documentDate", NO date value stored.
    [Fact]
    public void Editing_DocumentDate_records_documentDate_by_name_no_value()
    {
        var model = Model("Verordnung", 1, ("DocumentDate", "2026-02-07"), ("KVNR", "Z000000005"));
        var sub = Sub("Verordnung", 1, new() { ["DocumentDate"] = "2026-07-02", ["KVNR"] = "Z000000005" });
        var corrected = ReviewDiff.CorrectedFields(model, sub);
        Assert.Contains("documentDate", corrected);    // camelCase name
        Assert.DoesNotContain("kvnr", corrected);       // KVNR unchanged
        Assert.DoesNotContain("2026-02-07", corrected); // NO values
        Assert.DoesNotContain("2026-07-02", corrected);
    }

    // (b) no-change review → empty correctedFields recorded (the rate denominator = all reviews).
    [Fact]
    public void No_change_review_records_empty_correctedFields()
    {
        var model = Model("Verordnung", 1, ("DocumentDate", "2026-07-02"), ("KVNR", "Z000000005"));
        var sub = Sub("Verordnung", 1, new() { ["DocumentDate"] = "2026-07-02", ["KVNR"] = "Z000000005" });
        Assert.Empty(ReviewDiff.CorrectedFields(model, sub));
    }

    // (c) retyping the same date in a different format (normalized-equal) → NOT a correction.
    [Fact]
    public void Retyping_the_same_date_in_a_different_format_is_not_a_correction()
    {
        var model = Model("Verordnung", 1, ("DocumentDate", "2026-07-02"));
        var sub = Sub("Verordnung", 1, new() { ["DocumentDate"] = "02.07.2026" }); // ISO vs dotted, same day
        Assert.DoesNotContain("documentDate", ReviewDiff.CorrectedFields(model, sub));
    }

    // (d) type corrected Medikamentenplan → Allgemeine Korrespondenz: correctedFields has "contentType"; contentType is
    //     the FINAL type; contentTypeChange carries {from,to}.
    [Fact]
    public void Correcting_the_type_records_contentType_final_type_and_the_confusion_matrix()
    {
        var model = Model("Medikamentenplan", 1, ("DocumentDate", "2026-07-02"));
        var sub = Sub("Allgemeine Korrespondenz", 1, new() { ["DocumentDate"] = "2026-07-02" });

        Assert.Contains("contentType", ReviewDiff.CorrectedFields(model, sub));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ReviewDiff.Compute(model, sub)));
        var root = doc.RootElement;
        Assert.Equal("Allgemeine Korrespondenz", root.GetProperty("contentType").GetString()); // FINAL type, not the model's guess
        var ctc = root.GetProperty("contentTypeChange");
        Assert.Equal("Medikamentenplan", ctc.GetProperty("from").GetString());
        Assert.Equal("Allgemeine Korrespondenz", ctc.GetProperty("to").GetString());
    }

    // contentTypeChange is PRESENT ONLY when the type changed — absent (not null) otherwise.
    [Fact]
    public void No_type_correction_omits_contentTypeChange()
    {
        var model = Model("Verordnung", 1, ("DocumentDate", "2026-07-02"));
        var sub = Sub("Verordnung", 1, new() { ["DocumentDate"] = "2026-02-07" }); // date changed, type not
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ReviewDiff.Compute(model, sub)));
        Assert.False(doc.RootElement.TryGetProperty("contentTypeChange", out _)); // omitted entirely
    }

    // (e) NO PHI value anywhere in the record — even across a full identity + date + type + patient correction.
    [Fact]
    public void The_record_contains_no_PHI_values()
    {
        var model = Model("Medikamentenplan", 1, ("KVNR", "Z000000005"), ("LastName", "Mustermann"), ("BirthDate", "1956-05-29"), ("DocumentDate", "2026-02-07"));
        var sub = Sub("Allgemeine Korrespondenz", 2, new() { ["KVNR"] = "T000000002", ["LastName"] = "Beispiel", ["BirthDate"] = "1960-01-01", ["DocumentDate"] = "2026-07-02" });
        var json = JsonSerializer.Serialize(ReviewDiff.Compute(model, sub));
        foreach (var phi in new[] { "Z000000005", "T000000002", "Mustermann", "Beispiel", "1956-05-29", "1960-01-01", "2026-02-07", "2026-07-02" })
            Assert.DoesNotContain(phi, json);
        // Names ARE present (camelCase); only contentType's category before→after carries values (non-PHI).
        Assert.Contains("documentDate", json);
        Assert.Contains("kvnr", json);
        Assert.Contains("patient", json);
        Assert.Contains("Medikamentenplan", json);        // contentTypeChange.from (category, non-PHI)
        Assert.Contains("Allgemeine Korrespondenz", json); // contentType + contentTypeChange.to
    }
}
