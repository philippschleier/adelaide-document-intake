using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Adelaide.Web.Tests;

// Forward-strip (WORM boundary): a NEW Reviewed event's diff must carry NO patient was/now VALUES (KVNR/surname/DOB/
// dates) — only field NAMES + the contentType before→after (non-PHI). Sealed historical events keep their valued diffs
// and are not touched (that's the chain/tamper tests' concern in Audit.Tests).
public class ReviewDiffPhiStripTests
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

    [Fact]
    public void New_Reviewed_diff_omits_all_PHI_values_but_keeps_the_contentType_change()
    {
        // A review that corrects KVNR + surname + DOB + DocumentDate AND reclassifies the type + reassigns the patient.
        var model = Model("Document", 1,
            ("KVNR", "Z000000005"), ("LastName", "Mustermann"), ("BirthDate", "1956-05-29"), ("DocumentDate", "2026-02-07"));
        var human = new ReviewSubmission("doc1", "Verordnung", 2, new()
        {
            ["KVNR"] = "T000000002", ["LastName"] = "Beispiel", ["BirthDate"] = "1960-01-01", ["DocumentDate"] = "2026-07-02",
        });

        var json = JsonSerializer.Serialize(ReviewDiff.Compute(model, human));

        // (a) NO patient VALUES anywhere — neither the model's originals nor the human's submitted values.
        foreach (var phi in new[] { "Z000000005", "T000000002", "Mustermann", "Beispiel", "1956-05-29", "1960-01-01", "2026-02-07", "2026-07-02" })
            Assert.DoesNotContain(phi, json);

        // Field NAMES are retained (structural, PHI-free, camelCase), so which fields changed is still visible.
        Assert.Contains("kvnr", json);
        Assert.Contains("documentDate", json);

        // contentType's before→after IS kept (a document category, not patient data).
        Assert.Contains("contentTypeChange", json);
        Assert.Contains("Document", json);   // from
        Assert.Contains("Verordnung", json); // to
    }
}
