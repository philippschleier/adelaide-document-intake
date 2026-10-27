using System.Globalization;
using Adelaide.Ai;

namespace Adelaide.Core.Tests;

// Pins the prompt-template hashes stamped onto every Transcribed / Extracted event.
//
// WHY PIN: the hash is derived from the prompt literals PLUS the DocumentCatalog/Vocab digest, so a
// one-word prompt tweak — or adding a content type, a field, or an allowed choice value — silently
// re-fingerprints every document processed afterwards. Without a pin that happens quietly and the
// ledger's model-provenance trail forks with nothing pointing at the cause. With it, the change is a
// deliberate act: the build fails, and updating the constant here is the commit that records it.
//
// WHEN THIS FAILS: do NOT just paste in the new value. First confirm the prompt/catalog change was
// intended, then update the constant in the SAME commit as the change, so the two are reviewable together.
// A migration to a different deployed model does NOT move these hashes — they cover the prompts we send,
// not the model that answers. If a model swap moves them, something also edited a prompt.
public class PromptHashPinTests
{
    // Frozen 2026-08-05, immediately before the gpt-4o / gpt-4o-mini migration.
    private const string VisionTranscriberHash = "acffc7526284";

    // MOVED 2026-08-05 (e74a521c5383 -> cce89076ded9): the domain classifier added DomainSystemPrompt and its enum to
    // the extractor's template set — a real change to the model's task.
    //
    // MOVED AGAIN, SAME DAY (cce89076ded9 -> d1da5c19a7af), and this one was a BUG FIX, not a prompt change. The hash
    // input interpolated two doubles with default formatting, which is culture-sensitive: this dev machine is de-DE and
    // rendered "0,8" where the Linux CI runner rendered "0.8". The hash was therefore MACHINE-DEPENDENT — the deployed
    // container would have stamped a different promptHash than the dev box for byte-identical prompts, defeating the
    // attribution the hash exists to provide. cce89076ded9 was the de-DE artefact; d1da5c19a7af is the invariant value
    // and matches what CI computed independently. No prompt text changed. See ThresholdFragment.
    private const string ExtractorHash = "d1da5c19a7af";

    [Fact]
    public void VisionTranscriber_prompt_hash_is_pinned()
        => Assert.Equal(VisionTranscriberHash, VisionTranscriber.TemplateHash);

    [Fact]
    public void Extractor_prompt_hash_is_pinned()
        => Assert.Equal(ExtractorHash, AzureOpenAiExtractor.TemplateHash);

    // Guards the load-bearing declaration order documented in AzureOpenAiExtractor's <remarks>: the hash
    // reads DefiningCriteria and FieldSynonyms, which are static readonly fields. C# runs static field
    // initialisers in textual order, so an eager initialiser placed ABOVE them observes null and throws
    // TypeInitializationException on the first extraction — a crash no other test reaches, because nothing
    // else touches that type's static constructor. Reading the hash at all is the check.
    // REGRESSION GUARD for the culture bug above. The pin alone only catches this when CI happens to run under a
    // different locale than the developer — which is how it surfaced, but is luck, not a test. This asserts the
    // property directly: the numeric fragment of the hash input must render identically whatever the ambient culture,
    // and must use a decimal POINT. A comma here silently re-fingerprints every document processed on that machine.
    [Theory]
    [InlineData("de-DE")]   // decimal comma — the culture that produced the wrong hash
    [InlineData("fr-FR")]   // decimal comma
    [InlineData("en-US")]
    [InlineData("")]        // invariant
    public void The_hash_threshold_fragment_is_culture_invariant(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);
            var fragment = AzureOpenAiExtractor.ThresholdFragment();
            Assert.Equal("escalateBelow=0.8;selfCheckCap=0.75;truncate=12000;maxTokens=200/200/900", fragment);
            Assert.DoesNotContain(",", fragment, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Extractor_static_initialisation_does_not_throw()
    {
        var ex = Record.Exception(() => AzureOpenAiExtractor.TemplateHash);
        Assert.Null(ex);
        Assert.False(string.IsNullOrWhiteSpace(AzureOpenAiExtractor.TemplateHash));
    }
}
