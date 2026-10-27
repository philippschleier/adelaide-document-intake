using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Adelaide.Core;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Adelaide.Ai;

/// <summary>
/// Minimal keyless Azure OpenAI chat client against the v1 (OpenAI-compatible) endpoint, authenticated with an
/// Entra token (cognitiveservices scope). HttpClient is supplied by IHttpClientFactory (with resilience).
///
/// THE model boundary (capture): every chat completion in the system flows through <see cref="CompleteWithMetaAsync"/>,
/// and when the caller supplies a <see cref="CallContext"/> the full record of the call — request as sent (images
/// elided to hash+size), response verbatim, token usage, latency, gateway request id — is handed to the
/// <see cref="IModelCallRecorder"/> (the PHI-side model_calls table). Capture observes and never decides: a recording
/// failure is logged and counted by the recorder, and this client additionally guards so it can never fail the call.
/// The old content-only overload is gone — it silently discarded the served model and fingerprint for three of the
/// five call sites (INVESTIGATION.md §7.2).
/// </summary>
public sealed class OpenAiClient(HttpClient http, AiOptions opt, TokenCredential cred, ILogger<OpenAiClient> log, IModelCallRecorder? recorder = null)
{
    private static readonly string[] Scope = { "https://cognitiveservices.azure.com/.default" };

    /// <summary>Simple text prompt → reply text (diagnostics only — no capture context).</summary>
    public async Task<string> AskAsync(string prompt, int maxTokens = 64, CancellationToken ct = default) =>
        (await CompleteWithMetaAsync(new object[] { new { role = "user", content = prompt } }, responseFormat: null, maxTokens, ct: ct)).Content;

    /// <summary>Full chat completion returning the reply text, the SERVED model id, and the backend's system_fingerprint
    /// (either may be null if the model/API version doesn't surface it). A fixed seed reproduces output only WITHIN a
    /// given fingerprint, so record the fingerprint to detect when Azure changed the backend under you.
    ///
    /// <c>Model</c> is the model id the API says it actually ran — e.g. <c>gpt-4o-2024-11-20</c>, i.e. name AND version —
    /// which is NOT the same as the deployment name we asked for. Deployments can be repointed at a new model version in
    /// the portal without any change here, so the request-side deployment string cannot answer "which model version
    /// produced this row". Only the response can.
    ///
    /// <paramref name="ctx"/> keys the captured call record to a document + call site; null (tools, probes) = no capture.</summary>
    public async Task<(string Content, string? Model, string? SystemFingerprint)> CompleteWithMetaAsync(
        IReadOnlyList<object> messages, object? responseFormat, int answerTokens, string? model = null, CancellationToken ct = default,
        int? seed = null, string? reasoningEffort = null, CallContext? ctx = null)
    {
        var deployment = model ?? opt.Deployment;
        var caps = ModelCapabilities.For(deployment);
        // The caller's budget is what the ANSWER needs. A reasoning model spends reasoning tokens out of the same
        // allowance, so the wire budget adds the effort's measured allowance on top — otherwise reasoning eats the
        // whole budget and the response comes back with finish_reason=length and EMPTY content.
        var effort = caps.SupportsReasoningEffort ? (reasoningEffort ?? ReasoningEffort.Classification) : null;
        var wireBudget = caps.UsesMaxCompletionTokens ? answerTokens + ReasoningEffort.Allowance(effort) : answerTokens;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = deployment,
            ["messages"] = messages,
        };
        if (caps.UsesMaxCompletionTokens) payload["max_completion_tokens"] = wireBudget;
        else payload["max_tokens"] = wireBudget;
        // temperature=0 is how the classic surface is made deterministic. A reasoning model REFUSES any value but its
        // default, so sending it at all is a 400 — determinism there has to come from the schema, not the sampler.
        if (caps.SupportsTemperature) payload["temperature"] = 0;
        if (effort is not null) payload["reasoning_effort"] = effort;
        if (responseFormat is not null) payload["response_format"] = responseFormat;
        if (seed is not null) payload["seed"] = seed.Value; // reproducible output for a given system_fingerprint

        var wireBody = JsonSerializer.Serialize(payload); // the EXACT bytes sent — also the captured request (images elided)

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{opt.OpenAiEndpoint}/openai/v1/chat/completions");
        var token = await cred.GetTokenAsync(new TokenRequestContext(Scope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        req.Content = new StringContent(wireBody, Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var requestId = HeaderOrNull(resp, "apim-request-id") ?? HeaderOrNull(resp, "x-request-id");
        if (!resp.IsSuccessStatusCode)
        {
            // Status + timing only — the response body can echo prompt content (PHI). The body rides the thrown
            // exception and is sanitised (Redact) at the catch points; it is never logged here.
            log.LogWarning("OpenAI {Model} failed: HTTP {Status} in {ElapsedMs}ms budget={Budget} effort={Effort} seed={Seed} deployment={Deployment}",
                deployment, (int)resp.StatusCode, sw.ElapsedMilliseconds, wireBudget, effort ?? "(n/a)", seed, opt.Deployment);
            // A failed attempt is still a call that happened: capture it (status + error body) before throwing.
            await RecordAsync(ctx, deployment, served: null, fingerprint: null, effort, wireBody, body,
                promptTokens: null, completionTokens: null, reasoningTokens: null,
                sw.ElapsedMilliseconds, requestId, (int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}", ct);
            throw Redact.HttpFail((int)resp.StatusCode, body, "OpenAI");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
        var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        var fp = root.TryGetProperty("system_fingerprint", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        var served = root.TryGetProperty("model", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetString() : null;

        // Reasoning-token accounting: reasoning comes out of the completion budget, so this is what says whether the
        // budget is right. Numbers only — never the content.
        int completionTokens = 0, reasoningTokens = 0, promptTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            // Prompt tokens too: TPM is billed on prompt+completion, and on these prompts the PROMPT dominates. Without
            // it the throughput ceiling can only be guessed, which is how an under-provisioned deployment gets found
            // the hard way — as 429s in production rather than as arithmetic beforehand.
            if (usage.TryGetProperty("prompt_tokens", out var ptk) && ptk.ValueKind == JsonValueKind.Number) promptTokens = ptk.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ctk) && ctk.ValueKind == JsonValueKind.Number) completionTokens = ctk.GetInt32();
            if (usage.TryGetProperty("completion_tokens_details", out var det)
                && det.TryGetProperty("reasoning_tokens", out var rt) && rt.ValueKind == JsonValueKind.Number) reasoningTokens = rt.GetInt32();
        }

        // THE GUARD. A reasoning model that exhausts its budget returns HTTP 200 with finish_reason=length and an
        // EMPTY string — the caller then hands "" to ParseJson and gets a JsonReaderException on every document.
        // Measured: at the pre-fix 200-token classify budget, gpt-5-mini spent all 200 on reasoning and answered with
        // nothing. Detect it here, where the usage numbers are, and throw something that says WHY.
        if (content.Length == 0)
        {
            log.LogError(
                "OpenAI {Model} returned EMPTY content: finish={Finish} budget={Budget} effort={Effort} completion={Completion} reasoning={Reasoning}. " +
                "The completion budget was consumed before an answer was produced — raise the answer budget or lower the reasoning effort.",
                deployment, finish ?? "(none)", wireBudget, effort ?? "(n/a)", completionTokens, reasoningTokens);
            await RecordAsync(ctx, deployment, served, fp, effort, wireBody, body,
                promptTokens, completionTokens, reasoningTokens,
                sw.ElapsedMilliseconds, requestId, (int)resp.StatusCode, $"empty content (finish={finish ?? "none"})", ct);
            throw new EmptyModelResponseException(deployment, finish, wireBudget, effort, completionTokens, reasoningTokens);
        }

        // PHI-safe: model / timing / status / fingerprint / token counts only — never the messages or the completion.
        log.LogInformation("OpenAI {Model} ok: HTTP {Status} in {ElapsedMs}ms budget={Budget} effort={Effort} prompt={Prompt} completion={Completion} reasoning={Reasoning} total={Total} served={ServedModel} fingerprint={Fingerprint}",
            deployment, (int)resp.StatusCode, sw.ElapsedMilliseconds, wireBudget, effort ?? "(n/a)", promptTokens, completionTokens, reasoningTokens, promptTokens + completionTokens, served ?? "(none)", fp ?? "(none)");
        await RecordAsync(ctx, deployment, served, fp, effort, wireBody, body,
            promptTokens, completionTokens, reasoningTokens,
            sw.ElapsedMilliseconds, requestId, (int)resp.StatusCode, error: null, ct);
        return (content, served, fp);
    }

    private static string? HeaderOrNull(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    /// <summary>Hand the finished call record to the recorder. Belt-and-braces fail-open: the recorder is itself
    /// required to log+count rather than throw, and this catch guarantees a recorder bug still cannot fail the model
    /// call it observed. Never silent — the warning names the document and site.</summary>
    private async Task RecordAsync(CallContext? ctx, string deployment, string? served, string? fingerprint, string? effort,
        string wireBody, string responseBody, int? promptTokens, int? completionTokens, int? reasoningTokens,
        long latencyMs, string? requestId, int? httpStatus, string? error, CancellationToken ct)
    {
        if (ctx is null || recorder is null) return;
        try
        {
            await recorder.RecordAsync(new ModelCallRecord(
                ctx, DateTimeOffset.UtcNow, deployment, served, fingerprint, effort,
                ModelCallElision.ElideDataUrls(wireBody), responseBody,
                PromptTokens: promptTokens, CompletionTokens: completionTokens, ReasoningTokens: reasoningTokens,
                LatencyMs: latencyMs, RequestId: requestId, HttpStatus: httpStatus, Error: error), ct);
        }
        catch (Exception ex)
        {
            log.LogWarning("Model-call capture failed for {DocId} at {Site} ({ExType}); the call itself is unaffected.",
                ctx.DocumentId, ctx.Site.Wire(), ex.GetType().Name);
        }
    }
}

/// <summary>The model returned HTTP 200 with no content because the completion budget ran out — on a reasoning model,
/// typically because reasoning tokens consumed it before an answer was produced.
///
/// Its own type so callers can tell it apart from a transport failure or a malformed response: those are retried or
/// dead-lettered, whereas this one means the BUDGET is wrong and retrying identically will fail identically.
/// Carries no prompt or completion text.</summary>
public sealed class EmptyModelResponseException(string deployment, string? finishReason, int budget, string? effort, int completionTokens, int reasoningTokens)
    : InvalidOperationException(
        $"Model '{deployment}' returned empty content (finish={finishReason ?? "none"}, budget={budget}, " +
        $"effort={effort ?? "n/a"}, completion={completionTokens}, reasoning={reasoningTokens}).")
{
    public string Deployment { get; } = deployment;
    public string? FinishReason { get; } = finishReason;
    public int Budget { get; } = budget;
    public string? Effort { get; } = effort;
    public int CompletionTokens { get; } = completionTokens;
    public int ReasoningTokens { get; } = reasoningTokens;
}
