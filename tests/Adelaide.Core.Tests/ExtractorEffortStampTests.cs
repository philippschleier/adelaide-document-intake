using System.Net;
using System.Text;
using System.Text.Json;
using Adelaide.Ai;
using Adelaide.Core;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Core.Tests;

// ExtractedData.ReasoningEffort must describe the EXTRACTION call (strong deployment, Extraction op) — the stamp
// previously reported EffortSentFor(mini, Classification), which resolves to null on gpt-4o-mini and would have
// been a false provenance claim the moment a reasoning deployment landed. The strong deployment name here uses the
// gpt-5 family prefix so ModelCapabilities resolves it as reasoning without any environment override.
public class ExtractorEffortStampTests
{
    private sealed class FakeCred : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    /// <summary>Answers each call by sniffing the response_format schema name in the request body.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var content =
                body.Contains("\"name\":\"domain\"", StringComparison.Ordinal)
                    ? """{"domain":"Medical","confidence":0.95}"""
                : body.Contains("\"name\":\"classification\"", StringComparison.Ordinal)
                    ? """{"contentType":"Medikamentenplan","confidence":0.9}"""
                    : """{"kvnr":null,"lastName":null,"firstName":null,"birthDate":null,"documentDate":"2026-05-01","fields":[],"overallConfidence":0.8}""";
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content }, finish_reason = "stop" } },
                model = "gpt-5-unit-2026-01-01",
                usage = new { prompt_tokens = 10, completion_tokens = 5 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task The_stamp_reflects_the_extraction_call_not_the_mini_classify_call()
    {
        var opt = new AiOptions("https://unit.invalid", "gpt-5-unit", "gpt-4o-mini", ""); // strong = reasoning family
        var client = new OpenAiClient(new HttpClient(new ScriptedHandler()), opt, new FakeCred(), NullLogger<OpenAiClient>.Instance);
        var extractor = new AzureOpenAiExtractor(client, opt, NullLogger<AzureOpenAiExtractor>.Instance);
        var unit = new DocumentUnit("doc-effort", 1, 1,
            new Transcript("Synthetischer Medikamentenplan für einen Testlauf ohne echte Daten.", 1, 0.9, Array.Empty<DecodedBarcode>()));

        var data = await extractor.ExtractAsync(unit);

        // Pre-fix this was EffortSentFor(gpt-4o-mini, Classification) == null; the extraction call actually sends
        // ReasoningEffort.Extraction on the strong reasoning deployment.
        Assert.NotNull(data.ReasoningEffort);
        Assert.Equal(ReasoningEffort.Extraction, data.ReasoningEffort);
    }
}
