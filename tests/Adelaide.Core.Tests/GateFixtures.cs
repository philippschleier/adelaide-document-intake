namespace Adelaide.Core.Tests;

/// <summary>Shared fixture text for tests that exercise <see cref="IntakePipeline.Decide"/>.
///
/// These tests used the placeholder "md". Once the empty-transcript gate landed (N4) that became a FAILING input —
/// correctly: two characters for a one-page document is exactly the condition the gate exists to catch
/// (see <see cref="TranscriptQuality"/>). The placeholder was masking nothing, but it also could not be kept, because
/// every gate test would then trip a reason it wasn't testing.
///
/// So: a body long enough to be a plausible one-page document. Synthetic, PHI-free — no real KVNR, no real name
/// (the project's test-fixture rule: synthetic identifiers only, never values copied from sample PDFs).</summary>
internal static class GateFixtures
{
    public const string Transcript = """
        # Verordnung häuslicher Krankenpflege (Muster 12)
        Praxis Beispiel, Musterstraße 1, 12345 Musterstadt
        Versicherte(r): Mustermann, Max — geb. 01.01.1950
        Verordnungszeitraum von 01.05.2026 bis 31.05.2026
        Leistungen: Medikamentengabe, Blutdruckmessung
        """;
}
