using System.Text.Json;
using System.Text.Json.Nodes;
using Adelaide.Audit;
using Adelaide.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Web.Tests;

// WP4: the client's observed last-writer map — server-sanitized before it can touch the hash chain (known field
// names + known enum only; a malicious client can neither mint strings into the ledger nor smuggle values), carried
// as an ADDITIVE names+enum key on the Reviewed diff (the ReviewDiffPhiStripTests pattern), and landed in
// field_adjudications.Provenance. ReviewFieldSignals stays untouched — observation is recorded BESIDE the
// structural inference, never instead of it. Synthetic data only.
public sealed class ReviewProvenanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("adelaide-rp-tests").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private static JsonNode Sidecar() => new JsonObject
    {
        ["contentType"] = "Verordnung",
        ["fields"] = new JsonArray(
            new JsonObject { ["name"] = "DocumentDate", ["value"] = "2026-05-01", ["confidence"] = 0.83, ["source"] = "vision" },
            new JsonObject { ["name"] = "KVNR", ["value"] = "Q000000000", ["confidence"] = 0.95, ["source"] = "vision" }),
        ["match"] = new JsonObject { ["candidate"] = new JsonObject { ["listItemId"] = 7 } },
    };

    private static ReviewSubmission Submission(Dictionary<string, string>? prov) => new(
        "doc-prov", "Verordnung", 7,
        new Dictionary<string, string?> { ["DocumentDate"] = "2026-05-02", ["KVNR"] = "Q000000000" },
        FieldProvenance: prov);

    [Fact]
    public void Sanitize_keeps_known_fields_and_writers_only()
    {
        var prov = ReviewCapture.SanitizeProvenance(Sidecar(), Submission(new()
        {
            ["documentDate"] = "TYPED",              // case-insensitive name + writer, normalized
            ["KVNR"] = "patient",
            ["NotAField"] = "typed",                 // unknown field → dropped (cannot mint ledger strings)
            ["DocumentDate2"] = "prefill",           // unknown field → dropped
        }));

        Assert.NotNull(prov);
        Assert.Equal(2, prov!.Count);
        Assert.Equal("typed", prov["DocumentDate"]); // canonical column casing, lowercased writer
        Assert.Equal("patient", prov["KVNR"]);
    }

    [Fact]
    public void Sanitize_drops_invented_writers_and_returns_null_for_old_clients()
    {
        Assert.Null(ReviewCapture.SanitizeProvenance(Sidecar(), Submission(null)));
        Assert.Null(ReviewCapture.SanitizeProvenance(Sidecar(), Submission(new() { ["DocumentDate"] = "Q123456789" })));
    }

    [Fact]
    public void The_reviewed_diff_carries_names_and_enum_only()
    {
        var diff = ReviewDiff.Compute(Sidecar(), Submission(new() { ["DocumentDate"] = "typed" }));
        using var d = JsonDocument.Parse(JsonSerializer.Serialize(diff));

        var prov = d.RootElement.GetProperty("fieldProvenance");
        var entry = Assert.Single(prov.EnumerateArray());
        Assert.Equal("documentDate", entry.GetProperty("field").GetString()); // camelCased like correctedFields
        Assert.Equal("typed", entry.GetProperty("provenance").GetString());
        Assert.Equal(2, entry.EnumerateObject().Count());                     // field + provenance, NOTHING else — no values
        Assert.DoesNotContain("2026-05", d.RootElement.GetRawText(), StringComparison.Ordinal); // no field VALUE anywhere in the diff
    }

    [Fact]
    public void An_old_client_produces_the_pre_WP4_event_shape()
    {
        var diff = ReviewDiff.Compute(Sidecar(), Submission(null));
        using var d = JsonDocument.Parse(JsonSerializer.Serialize(diff));
        Assert.False(d.RootElement.TryGetProperty("fieldProvenance", out _)); // absent key — byte-compatible history
    }

    [Fact]
    public void The_business_lane_carries_the_same_map()
    {
        var body = new ReviewSubmission("doc-prov", "Verordnung", 0,
            new Dictionary<string, string?> { ["DocumentDate"] = "2026-05-02" },
            IsBusiness: true, Zielbereich: "HR",
            FieldProvenance: new() { ["DocumentDate"] = "typed" });
        using var d = JsonDocument.Parse(JsonSerializer.Serialize(ReviewDiff.Compute(Sidecar(), body)));
        Assert.True(d.RootElement.TryGetProperty("fieldProvenance", out var prov));
        Assert.Single(prov.EnumerateArray());
    }

    [Fact]
    public async Task The_sanitized_map_lands_in_field_adjudications()
    {
        var s = new TranscriptStore(Path.Combine(_dir, "capture.db"));
        var body = Submission(new() { ["documentDate"] = "typed", ["kvnr"] = "patient" });

        await ReviewCapture.RecordReviewOutcomesAsync(s, null, NullLogger.Instance, "doc-prov",
            Sidecar(), body, ReviewCapture.SanitizeProvenance(Sidecar(), body), DateTimeOffset.UtcNow, CancellationToken.None);

        var adj = await s.GetAdjudicationsAsync("doc-prov");
        Assert.Equal("typed", adj.Single(a => a.FieldName == "DocumentDate").Provenance);
        Assert.Equal("patient", adj.Single(a => a.FieldName == "KVNR").Provenance);
        // The structural signal is untouched by the observation: KVNR changed?—no; both unchanged here → match.
        Assert.Equal("correction", adj.Single(a => a.FieldName == "DocumentDate").Signal);
    }
}
