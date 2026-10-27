using System.Text.Json;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Web.Tests;

// The N3 rework moved the transcript OUT of the ledger and put its SHA-256 in instead. This asserts the shape of the
// payload the host ACTUALLY emits — TranscribedEventDetails is the same builder Program.cs calls, so this cannot pass
// by two copies of the same idea agreeing with each other.
//
// Byte-identity matters because these events are hash-chained: a reordered or reformatted property would change every
// future event hash while looking equivalent to a reader.
public class TranscribedEventShapeTests
{
    private static Transcript T(string markdown) =>
        new(markdown, 3, 0.95, [new DecodedBarcode("DataMatrix", "payload", true)],
            null, "gpt-4o", "gpt-4o-2024-11-20", "fp_abc123", "acffc7526284");

    // The vision payload as it stood immediately BEFORE the rework: the same seven properties, same order.
    private static string PreReworkVision() => JsonSerializer.Serialize(new
    {
        pages = 3,
        legibility = 0.95,
        barcodes = 1,
        model = "gpt-4o",
        modelVersion = "gpt-4o-2024-11-20",
        systemFingerprint = "fp_abc123",
        promptHash = "acffc7526284",
    });

    private static string PreReworkReuseSplit() => JsonSerializer.Serialize(new
    {
        pages = 3,
        legibility = 0.95,
        barcodes = 1,
        source = "parent",
        parent = "parent-doc",
        separatorVersion = "sep-1",
    });

    // The vision event is the pre-rework payload with exactly TWO properties appended, in this order:
    //   transcriptSha256 — added when the transcript moved off the ledger (the evidence that replaced the text)
    //   reasoningEffort  — added 2026-08-06 with per-model request shaping. A behavioural setting: moving it from
    //                      minimal to medium changes what the model does, so a later shift in results is only
    //                      attributable if the value that produced them was recorded. Null on a non-reasoning model.
    // Still a literal prefix assertion, so the property set, order and formatting stay pinned.
    [Fact]
    public void The_vision_event_is_the_pre_rework_payload_with_exactly_the_hash_and_effort_appended()
    {
        const string body = "some transcript text";
        var now = JsonSerializer.Serialize(TranscribedEventDetails.Vision(T(body)));

        var prefix = PreReworkVision()[..^1];
        Assert.StartsWith(prefix, now, StringComparison.Ordinal);
        Assert.Equal($",\"transcriptSha256\":\"{TranscriptDigest.Of(body)}\",\"reasoningEffort\":null}}", now[prefix.Length..]);
    }

    // And when a reasoning model DID run, the effort it was sent is what lands on the event.
    [Fact]
    public void The_sent_reasoning_effort_lands_on_the_event()
    {
        var t = T("text") with { ReasoningEffort = "minimal" };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(TranscribedEventDetails.Vision(t)));
        Assert.Equal("minimal", doc.RootElement.GetProperty("reasoningEffort").GetString());
    }

    [Fact]
    public void The_split_child_event_is_the_pre_rework_payload_with_exactly_the_hash_appended()
    {
        const string body = "child slice text";
        var now = JsonSerializer.Serialize(TranscribedEventDetails.ReuseSplit(T(body), "parent-doc", "sep-1"));

        var prefix = PreReworkReuseSplit()[..^1];
        Assert.StartsWith(prefix, now, StringComparison.Ordinal);
        Assert.Equal($",\"transcriptSha256\":\"{TranscriptDigest.Of(body)}\"}}", now[prefix.Length..]);
    }

    // The property the rework exists to remove. Checked on both paths, and against the text itself rather than just
    // the property name — a differently-named property carrying the transcript would be the same failure.
    [Fact]
    public void Neither_path_carries_the_transcript_itself()
    {
        const string phi = "Versicherte(r): Mustermann, Max — Diagnose";
        foreach (var payload in new[]
                 {
                     JsonSerializer.Serialize(TranscribedEventDetails.Vision(T(phi))),
                     JsonSerializer.Serialize(TranscribedEventDetails.ReuseSplit(T(phi), "p", "s")),
                 })
        {
            using var doc = JsonDocument.Parse(payload);
            Assert.False(doc.RootElement.TryGetProperty("transcript", out _));
            Assert.DoesNotContain("Mustermann", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("Diagnose", payload, StringComparison.Ordinal);
        }
    }

    // The hash on the event is the hash OF the transcript, so a separately-stored copy can be proven to be the one
    // that was actually generated.
    [Fact]
    public void The_hash_on_the_event_is_the_hash_of_the_transcript()
    {
        const string body = "Verordnung häuslicher Krankenpflege";
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(TranscribedEventDetails.Vision(T(body))));
        Assert.Equal(TranscriptDigest.Of(body), doc.RootElement.GetProperty("transcriptSha256").GetString());
    }

    // Always present, even with nothing transcribed — an absent hash would be ambiguous between "no transcript" and
    // "an event predating this field".
    [Fact]
    public void The_hash_is_present_even_for_an_empty_transcript()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(TranscribedEventDetails.Vision(T(""))));
        Assert.Equal(TranscriptDigest.Of(""), doc.RootElement.GetProperty("transcriptSha256").GetString());
    }
}
