using Adelaide.Ai;
using Xunit;

namespace Adelaide.Core.Tests;

// Per-deployment request shaping. Both families must run side by side during the comparison — this is not a swap, so
// a global switch would make the comparison impossible to run at all.
public class ModelCapabilitiesTests
{
    // The incumbent must be UNCHANGED by the refactor: temperature=0, max_tokens, no reasoning_effort. If any of
    // these flip, the 4o arm of the comparison stops being the thing it was measured as.
    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4o-mini")]
    public void The_4o_family_keeps_the_classic_surface(string deployment)
    {
        var c = ModelCapabilities.For(deployment);
        Assert.False(c.UsesMaxCompletionTokens);
        Assert.True(c.SupportsTemperature);
        Assert.False(c.SupportsReasoningEffort);
        Assert.True(c.ReportsSystemFingerprint);
    }

    [Theory]
    [InlineData("gpt-5-mini")]
    [InlineData("gpt-5")]
    [InlineData("o3-mini")]
    public void Reasoning_families_get_the_reasoning_surface(string deployment)
    {
        var c = ModelCapabilities.For(deployment);
        Assert.True(c.UsesMaxCompletionTokens);
        Assert.False(c.SupportsTemperature);      // 400s on any value but the default
        Assert.True(c.SupportsReasoningEffort);
        Assert.False(c.ReportsSystemFingerprint); // absent from the response entirely
    }

    // An unknown deployment must behave as it always did rather than being guessed into a shape it may not accept.
    [Theory]
    [InlineData("some-future-model")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_deployment_defaults_to_the_classic_surface(string? deployment)
        => Assert.False(ModelCapabilities.For(deployment).UsesMaxCompletionTokens);

    // The stamp is computed the same way the wire value is, so the recorded effort cannot drift from what was sent.
    [Fact]
    public void The_stamped_effort_is_null_on_a_model_that_has_no_such_parameter()
    {
        Assert.Null(ModelCapabilities.EffortSentFor("gpt-4o-mini", ReasoningEffort.Minimal));
        Assert.Equal(ReasoningEffort.Minimal, ModelCapabilities.EffortSentFor("gpt-5-mini", ReasoningEffort.Minimal));
    }

    // ---- budget allowances ----

    // Every allowance must clear the MEASURED worst case with real headroom. A budget that fits the median and fails
    // the tail returns empty content, which is the failure this exists to prevent.
    [Theory]
    [InlineData(ReasoningEffort.Minimal, 0)]    // measured worst case: 0 reasoning tokens
    [InlineData(ReasoningEffort.Low, 128)]      // measured worst case: 128
    [InlineData(ReasoningEffort.Medium, 320)]   // measured worst case: 320
    [InlineData(ReasoningEffort.High, 576)]     // measured worst case: 576
    public void Allowances_clear_the_measured_worst_case_with_headroom(string effort, int measuredWorstCase)
    {
        var allowance = ReasoningEffort.Allowance(effort);
        Assert.True(allowance > measuredWorstCase,
            $"allowance {allowance} for '{effort}' does not exceed the measured worst case {measuredWorstCase}");
        Assert.True(allowance >= measuredWorstCase * 2 || allowance >= 512,
            $"allowance {allowance} for '{effort}' leaves too little headroom over {measuredWorstCase}");
    }

    // An unset/unknown effort must budget for the DEFAULT's real behaviour. The unset default was measured to behave
    // like medium, not low — budgeting for the optimistic case is how the empty-content failure happens.
    [Fact]
    public void An_unknown_effort_budgets_for_the_measured_default_not_the_optimistic_one()
        => Assert.Equal(ReasoningEffort.Allowance(ReasoningEffort.Medium), ReasoningEffort.Allowance("something-else"));

    [Fact]
    public void Classification_defaults_to_minimal()
        => Assert.Equal(ReasoningEffort.Minimal, ReasoningEffort.Classification);
}
