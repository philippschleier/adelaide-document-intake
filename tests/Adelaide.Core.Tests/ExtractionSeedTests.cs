using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

public class ExtractionSeedTests
{
    // Two real SHA-256 hashes (of "test" and "foo") — only their leading 8 hex chars matter here.
    private const string ShaTest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string ShaFoo = "2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae";

    [Fact]
    public void Same_content_hash_is_deterministic()  // re-uploading identical bytes reproduces the same extraction
        => Assert.Equal(ExtractionSeed.FromContentSha(ShaTest), ExtractionSeed.FromContentSha(ShaTest));

    [Fact]
    public void Different_content_hashes_give_different_seeds()
        => Assert.NotEqual(ExtractionSeed.FromContentSha(ShaTest), ExtractionSeed.FromContentSha(ShaFoo));

    [Fact]
    public void Seed_is_never_negative()  // the sign bit is cleared so 0xffffffff maps to int.MaxValue, not -1
        => Assert.True(ExtractionSeed.FromContentSha("ffffffff" + new string('a', 56)) >= 0);

    [Theory]
    [InlineData(null)]       // no hash available
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]      // too short to take 32 bits from
    [InlineData("zzzzzzzz")] // not hex — tolerated, never throws mid-extraction
    public void Missing_or_unusable_hash_is_unseeded(string? sha)
        => Assert.Null(ExtractionSeed.FromContentSha(sha));

    [Fact]
    public void ExtractedData_carries_seed_and_fingerprint()  // the provenance the worker stamps onto the Extracted event
    {
        var data = new ExtractedData([], ModelSeed: 12345, ModelFingerprint: "fp_49e2bef596");
        Assert.Equal(12345, data.ModelSeed);
        Assert.Equal("fp_49e2bef596", data.ModelFingerprint);
    }
}
