using Adelaide.Core;

namespace Adelaide.Ai;

/// <summary>Azure OpenAI + Document Intelligence connection config (keyless; endpoints are non-secret).</summary>
public sealed record AiOptions(string OpenAiEndpoint, string Deployment, string MiniDeployment, string DiEndpoint)
{
    public static AiOptions Load()
    {
        var env = DotEnv.Read();
        string? Val(string k) => Environment.GetEnvironmentVariable(k) ?? (env.TryGetValue(k, out var v) ? v : null);
        var endpoint = Val("ADELAIDE_OPENAI_ENDPOINT")
            ?? throw new InvalidOperationException("Missing ADELAIDE_OPENAI_ENDPOINT (set it in .env).");
        var deployment = Val("ADELAIDE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
        var mini = Val("ADELAIDE_OPENAI_MINI_DEPLOYMENT") ?? "gpt-4o-mini";
        var di = Val("ADELAIDE_DI_ENDPOINT") ?? "";
        return new AiOptions(endpoint.TrimEnd('/'), deployment, mini, di.TrimEnd('/'));
    }

    /// <summary>Inert options for demo mode (the AI seams are rebound to fixture replay; nothing dials these).</summary>
    public static AiOptions DemoPlaceholder() => new("https://demo.invalid", "demo-fixture", "demo-fixture", "");
}
