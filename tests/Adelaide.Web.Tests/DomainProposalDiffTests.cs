using System.Text.Json;
using System.Text.Json.Nodes;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Web.Tests;

// N2: the reviewer's business checkbox stops being the SOLE source of the domain answer and becomes a confirmation or
// correction of the model's proposal. For that to be measurable the diff has to record both halves — what the model
// said (modelDomain) and whether the human kept it (domainConfirmed) — on BOTH lanes.
//
// Agreements matter as much as disagreements: without the confirmations there is no denominator, and domain accuracy
// cannot be computed at all. The pre-existing domainChange field only ever fired on business submissions, so a
// document the model wrongly called Business and the human kept medical left no trace whatsoever.
public class DomainProposalDiffTests
{
    private static JsonNode Model(string contentType, string? domain) =>
        new JsonObject
        {
            ["contentType"] = contentType,
            ["domain"] = domain,
            ["fields"] = new JsonArray { new JsonObject { ["name"] = "KVNR", ["value"] = "Z000000005" } },
            ["match"] = new JsonObject { ["candidate"] = new JsonObject { ["listItemId"] = 1 } },
        };

    private static ReviewSubmission Sub(bool isBusiness, string contentType = "Kassenkorrespondenz") =>
        new("doc1", contentType, isBusiness ? 0 : 1, new Dictionary<string, string?>(),
            IsBusiness: isBusiness, Zielbereich: isBusiness ? "Finance" : null);

    private static JsonElement Diff(JsonNode model, ReviewSubmission human) =>
        JsonDocument.Parse(JsonSerializer.Serialize(ReviewDiff.Compute(model, human))).RootElement.Clone();

    [Fact]
    public void Business_proposal_confirmed_by_the_human_is_recorded_as_confirmed()
    {
        var d = Diff(Model("Kassenkorrespondenz", DocumentDomain.Business), Sub(isBusiness: true));
        Assert.Equal(DocumentDomain.Business, d.GetProperty("modelDomain").GetString());
        Assert.True(d.GetProperty("domainConfirmed").GetBoolean());
    }

    [Fact]
    public void Medical_proposal_overridden_to_business_is_recorded_as_not_confirmed()
    {
        var d = Diff(Model("Kassenkorrespondenz", DocumentDomain.Medical), Sub(isBusiness: true));
        Assert.Equal(DocumentDomain.Medical, d.GetProperty("modelDomain").GetString());
        Assert.False(d.GetProperty("domainConfirmed").GetBoolean());
    }

    // The case that previously left NO record at all: model said Business, human kept it medical.
    [Fact]
    public void Business_proposal_overridden_to_medical_is_recorded_on_the_medical_lane()
    {
        var d = Diff(Model("Verordnung", DocumentDomain.Business), Sub(isBusiness: false, "Verordnung"));
        Assert.Equal(DocumentDomain.Medical, d.GetProperty("domain").GetString());
        Assert.Equal(DocumentDomain.Business, d.GetProperty("modelDomain").GetString());
        Assert.False(d.GetProperty("domainConfirmed").GetBoolean());
    }

    [Fact]
    public void Medical_proposal_confirmed_is_recorded_on_the_medical_lane()
    {
        var d = Diff(Model("Verordnung", DocumentDomain.Medical), Sub(isBusiness: false, "Verordnung"));
        Assert.True(d.GetProperty("domainConfirmed").GetBoolean());
    }

    // A document extracted before the domain step has no proposal. It must be EXCLUDED from the metric, not scored as
    // a correct confirmation — otherwise every historical review inflates domain accuracy toward 100%.
    [Fact]
    public void A_document_with_no_proposal_records_a_null_confirmation_not_a_true_one()
    {
        var business = Diff(Model("Kassenkorrespondenz", null), Sub(isBusiness: true));
        Assert.Equal(JsonValueKind.Null, business.GetProperty("modelDomain").ValueKind);
        Assert.Equal(JsonValueKind.Null, business.GetProperty("domainConfirmed").ValueKind);

        var medical = Diff(Model("Verordnung", null), Sub(isBusiness: false, "Verordnung"));
        Assert.Equal(JsonValueKind.Null, medical.GetProperty("domainConfirmed").ValueKind);
    }

    // The existing business shape must survive unchanged — recovery and the §11 labeled seed both read it.
    [Fact]
    public void The_existing_domainChange_shape_is_preserved()
    {
        var d = Diff(Model("Kassenkorrespondenz", DocumentDomain.Business), Sub(isBusiness: true));
        Assert.Equal("Business", d.GetProperty("domain").GetString());
        Assert.Equal("Kassenkorrespondenz", d.GetProperty("domainChange").GetProperty("from").GetString());
        Assert.Equal("Finance", d.GetProperty("zielbereich").GetString());
    }
}
