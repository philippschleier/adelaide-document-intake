using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// FINDING 7: the redaction helper must strip control characters and Unicode bidi overrides/isolates from anything it
// renders toward a log line or a user-facing message, so a crafted filename/error cannot reorder text or forge a line.
// Bidi code points are written as (char)0x…​ casts so this source file itself stays pure ASCII (no invisible chars).
public class RedactScrubTests
{
    private const char RLO = (char)0x202E; // Right-to-Left Override
    private const char PDF = (char)0x202C; // Pop Directional Formatting
    private const char LRI = (char)0x2066; // Left-to-Right Isolate
    private const char PDI = (char)0x2069; // Pop Directional Isolate

    [Fact]
    public void Strips_bidi_overrides_and_control_chars()
    {
        var dirty = "A" + RLO + "evil" + PDF + "\tB\nC";
        Assert.Equal("AevilBC", Redact.Scrub(dirty));
    }

    [Fact]
    public void Strips_directional_isolates() =>
        Assert.Equal("xy", Redact.Scrub("x" + LRI + PDI + "y"));

    [Fact]
    public void Keeps_normal_unicode_text() =>
        Assert.Equal("Müller-Datei.pdf", Redact.Scrub("Müller-Datei.pdf"));

    [Fact]
    public void Error_scrubs_bidi_and_caps_length()
    {
        var scrubbed = Redact.Error("Zugriff " + RLO + "verweigert" + PDF + " auf Datei");
        Assert.DoesNotContain(RLO, scrubbed);
        Assert.DoesNotContain(PDF, scrubbed);
        Assert.True(scrubbed.Length <= 120);
    }
}
