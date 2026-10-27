namespace Adelaide.Ai;

/// <summary>What a deployment's API surface actually accepts. Resolved PER DEPLOYMENT, because both families have to
/// run side by side during a model comparison — this is not a swap, and a global switch would make the comparison
/// impossible to run.
///
/// Measured against gpt-5-mini 2025-08-07 on 2026-08-06:
///   max_tokens        -> 400 "Unsupported parameter: 'max_tokens' is not supported with this model.
///                             Use 'max_completion_tokens' instead."
///   temperature=0     -> 400 "Unsupported value: 'temperature' does not support 0 with this model.
///                             Only the default (1) value is supported."
///   system_fingerprint-> absent from the response entirely
///   reasoning_effort  -> accepted; the UNSET default behaves like `medium`, not `low`
///
/// Resolution is deliberately explicit-first: an operator can name a deployment in
/// ADELAIDE_REASONING_DEPLOYMENTS and be certain, rather than depending on a name heuristic that a future model
/// naming scheme could silently break. The family table is only a convenience so the current deployments work with
/// no configuration at all.</summary>
public sealed record ModelCapabilities(
    bool UsesMaxCompletionTokens,
    bool SupportsTemperature,
    bool SupportsReasoningEffort,
    bool ReportsSystemFingerprint)
{
    /// <summary>The classic chat-completions surface: gpt-4o and friends.</summary>
    public static readonly ModelCapabilities ChatCompletion = new(
        UsesMaxCompletionTokens: false, SupportsTemperature: true,
        SupportsReasoningEffort: false, ReportsSystemFingerprint: true);

    /// <summary>The reasoning surface: gpt-5 family and the o-series. Spends reasoning tokens out of the completion
    /// budget, refuses a non-default temperature, and reports no system_fingerprint.</summary>
    public static readonly ModelCapabilities Reasoning = new(
        UsesMaxCompletionTokens: true, SupportsTemperature: false,
        SupportsReasoningEffort: true, ReportsSystemFingerprint: false);

    /// <summary>Deployment-name prefixes known to be reasoning models. Only a fallback — the env override wins.</summary>
    private static readonly string[] ReasoningFamilies = ["gpt-5", "o1", "o3", "o4"];

    private static readonly Lazy<HashSet<string>> Configured = new(() =>
        new HashSet<string>(
            (Environment.GetEnvironmentVariable("ADELAIDE_REASONING_DEPLOYMENTS") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase));

    /// <summary>Capabilities for a deployment NAME (what we ask for), not the served model id. Explicit configuration
    /// first, then the family table, then the classic surface — so an unknown deployment behaves as it always did
    /// rather than being guessed into a shape it may not accept.</summary>
    public static ModelCapabilities For(string? deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment)) return ChatCompletion;
        if (Configured.Value.Contains(deployment)) return Reasoning;
        foreach (var f in ReasoningFamilies)
            if (deployment.StartsWith(f, StringComparison.OrdinalIgnoreCase)) return Reasoning;
        return ChatCompletion;
    }

    /// <summary>The reasoning effort that will ACTUALLY be sent for this deployment — the requested value on a
    /// reasoning model, null on a model that has no such parameter. Callers stamp this on their events, so the
    /// recorded value is computed the same way the wire value is and the two cannot drift apart.</summary>
    public static string? EffortSentFor(string? deployment, string requested) =>
        For(deployment).SupportsReasoningEffort ? requested : null;
}

/// <summary>How hard a reasoning model should think. Reasoning tokens come out of the completion budget and dominate
/// latency, so this is a cost/latency dial rather than a correctness setting.
///
/// Measured on a ~996-token classification prompt against gpt-5-mini, 3 runs each (2026-08-06):
///   minimal  reasoning 0/0/0        completion 27/27/27     ~700ms    same answer
///   low      reasoning 64/64/128    completion 91/91/155    ~1400ms   same answer
///   medium   reasoning 256/320/256  completion 283/347/283  ~2900ms   same answer
///   high     reasoning 576/384/576  completion 603/411/603  4.5-21s   same answer
///
/// All four reached the same classification. `minimal` costs roughly what gpt-4o-mini did (15 tokens / 554ms), which
/// is why it is the default for the constrained-enum tasks: the model is choosing from a fixed catalog, not deriving
/// anything. Raise it per call site if a comparison shows it hurting.</summary>
public static class ReasoningEffort
{
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    /// <summary>Effort for the constrained-enum decisions: domain, classification, self-check.</summary>
    public static string Classification =>
        Environment.GetEnvironmentVariable("ADELAIDE_REASONING_EFFORT_CLASSIFY") is { Length: > 0 } v ? v : Minimal;

    /// <summary>Effort for field extraction — a harder task than picking from an enum, so it gets its own dial.</summary>
    public static string Extraction =>
        Environment.GetEnvironmentVariable("ADELAIDE_REASONING_EFFORT_EXTRACT") is { Length: > 0 } v ? v : Minimal;

    /// <summary>Reasoning tokens to ALLOW on top of the answer budget, per effort level. Derived from the measured
    /// worst case above with deliberate headroom, because a budget that fits the median and fails the tail returns
    /// EMPTY content — the failure this whole exercise exists to prevent. See OpenAiClient.WireBudget.</summary>
    public static int Allowance(string? effort) => effort switch
    {
        Minimal => 512,    // measured worst case 0; 512 covers a model that starts reasoning anyway
        Low => 768,        // measured worst case 128
        Medium => 1536,    // measured worst case 320
        High => 2560,      // measured worst case 576
        _ => 1536,         // unset behaves like medium — budget for that, never for the optimistic case
    };
}
