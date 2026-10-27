using Adelaide.Audit;
using Xunit;

namespace Adelaide.Audit.Tests;

// The shared diff tally must count BOTH Reviewed-event shapes — the acceptance fixture for the ClassifyEval
// Tier-A undercount (Tier-A diffs were previously skipped entirely). Synthetic diffs only.
public class ReviewFieldTallyTests
{
    private const string LegacyDiff = """
        {
          "type": { "was": "Verordnung", "now": "Genehmigung", "changed": true },
          "patient": { "was": null, "now": 7, "changed": true },
          "fields": [
            { "field": "documentDate", "was": "2026-05-01", "now": "2026-05-02", "changed": true },
            { "field": "legalBasis",   "was": null,         "now": "§ 37.3",     "changed": true },
            { "field": "kvnr",         "was": "Q000000001", "now": "Q000000000", "changed": true },
            { "field": "senderType",   "was": "Kasse",      "now": "Kasse",      "changed": false }
          ]
        }
        """;

    private const string TierADiff = """
        {
          "correctedFields": ["contentType", "documentDate", "serviceYear"],
          "fieldSignals": [
            { "field": "documentDate", "signal": "correction" },
            { "field": "serviceYear",  "signal": "fill" }
          ],
          "contentType": "Genehmigung",
          "domain": "Medical"
        }
        """;

    [Fact]
    public void Both_shapes_land_in_the_same_buckets()
    {
        var r = new ReviewFieldTally.Result();
        ReviewFieldTally.Add(r, LegacyDiff);
        ReviewFieldTally.Add(r, TierADiff);

        Assert.Equal(2, r.ReviewDiffs);
        Assert.Equal(1, r.OldShape);
        Assert.Equal(1, r.NewShape);
        Assert.Equal(2, r.TypeCorrected);                                  // one per shape
        Assert.Equal(1, r.PatientCorrected);

        Assert.Equal(2, r.Corrections["documentDate"]);                    // counted from BOTH shapes — the fix
        Assert.Equal(1, r.Fills["legalBasis"]);                            // legacy was-empty ⇒ fill
        Assert.Equal(1, r.Fills["serviceYear"]);                           // Tier-A explicit fill
        Assert.Equal(1, r.Matches["kvnr"]);                                // identity + linked patient ⇒ match
        Assert.False(r.Corrections.ContainsKey("senderType"));             // unchanged legacy field: seen only
        Assert.Equal(1, r.Seen["senderType"]);
        Assert.False(r.Seen.ContainsKey("serviceYear"));                   // Tier-A carries no field list → no seen
    }

    [Fact]
    public void A_business_domain_diff_counts_as_a_review_with_no_field_buckets()
    {
        var r = new ReviewFieldTally.Result();
        ReviewFieldTally.Add(r, """{ "domain": "Business", "domainChange": { "from": "Verordnung", "to": "Business" } }""");
        Assert.Equal(1, r.ReviewDiffs);
        Assert.Equal(0, r.OldShape + r.NewShape);
        Assert.Empty(r.Corrections);
    }

    [Fact]
    public void An_unparseable_diff_is_skipped_not_fatal()
    {
        var r = new ReviewFieldTally.Result();
        ReviewFieldTally.Add(r, "{not json");
        Assert.Equal(1, r.ReviewDiffs);
        Assert.Empty(r.Corrections);
    }
}
