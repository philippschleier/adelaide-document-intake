using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Adelaide.Web.Tests;

// A business reassignment records the domain flip (domainChange) as first-class labeled data (design §11): the model
// said medical-type X → the human said Business. PHI-free; the seed for the future domain classifier. The medical
// correctedFields/contentTypeChange shape is deliberately NOT emitted for a business review (those columns don't apply).
public class BusinessReviewDiffTests
{
    private static JsonNode Model(string contentType) => new JsonObject
    {
        ["contentType"] = contentType,
        ["fields"] = new JsonArray { new JsonObject { ["name"] = "KVNR", ["value"] = "Z000000005" } },
        ["match"] = new JsonObject { ["candidate"] = new JsonObject { ["listItemId"] = 1 } },
    };
    private static ReviewSubmission Business(string contentType, string zielbereich = "Finance")
        => new("doc1", contentType, 0, new Dictionary<string, string?>(), IsBusiness: true, Zielbereich: zielbereich);

    [Fact]
    public void Business_review_records_the_domain_change_from_the_models_medical_guess()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ReviewDiff.Compute(Model("Kassenkorrespondenz"), Business("Kassenkorrespondenz"))));
        var root = doc.RootElement;
        Assert.Equal("Business", root.GetProperty("domain").GetString());
        var dc = root.GetProperty("domainChange");
        Assert.Equal("Kassenkorrespondenz", dc.GetProperty("from").GetString()); // the model's medical guess (the confusion signal)
        Assert.Equal("Business", dc.GetProperty("to").GetString());
    }

    [Fact]
    public void Business_review_record_contains_no_PHI()
    {
        var json = JsonSerializer.Serialize(ReviewDiff.Compute(Model("Kassenkorrespondenz"), Business("Kassenkorrespondenz")));
        Assert.DoesNotContain("Z000000005", json); // the model's KVNR never leaks into the labeled record
        Assert.Contains("domainChange", json);
    }

    [Fact]
    public void Business_review_omits_the_medical_correction_fields()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ReviewDiff.Compute(Model("Document"), Business("Document"))));
        Assert.False(doc.RootElement.TryGetProperty("correctedFields", out _));
        Assert.False(doc.RootElement.TryGetProperty("contentTypeChange", out _));
    }

    // The reviewer's functional-area label rides the SAME record as domainChange → the immutable Reviewed event. This is
    // the label's system of record (independent of the disposable SharePoint column) and the Phase-2 training signal.
    [Fact]
    public void Business_review_records_the_zielbereich_label_alongside_the_domain_change()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ReviewDiff.Compute(Model("Kassenkorrespondenz"), Business("Kassenkorrespondenz", "Finance"))));
        var root = doc.RootElement;
        Assert.Equal("Finance", root.GetProperty("zielbereich").GetString());
        Assert.Equal("Business", root.GetProperty("domain").GetString());   // still beside domain/domainChange
        Assert.True(root.TryGetProperty("domainChange", out _));
    }
}
