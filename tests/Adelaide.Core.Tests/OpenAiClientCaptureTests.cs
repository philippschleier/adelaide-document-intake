using System.Net;
using System.Text;
using System.Text.Json;
using Adelaide.Ai;
using Adelaide.Core;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Adelaide.Core.Tests;

// The wrapper IS the model boundary: with a CallContext supplied, every attempt — success, HTTP failure, empty
// content — must hand the recorder a complete record (request with images elided, response verbatim, usage,
// latency, gateway request id), and a recorder failure must never fail the call. All traffic is a fake handler;
// all content synthetic.
public class OpenAiClientCaptureTests
{
    private sealed class FakeCred : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastRequestBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    private sealed class ListRecorder : IModelCallRecorder
    {
        public readonly List<ModelCallRecord> Records = new();
        public Exception? ThrowOnRecord;
        public Task RecordAsync(ModelCallRecord record, CancellationToken ct = default)
        {
            if (ThrowOnRecord is not null) throw ThrowOnRecord;
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private static HttpResponseMessage Ok(string content = "{\"ok\":true}", string? finish = "stop")
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content }, finish_reason = finish } },
            model = "gpt-4o-2024-11-20",
            system_fingerprint = "fp_unit",
            usage = new { prompt_tokens = 111, completion_tokens = 22, completion_tokens_details = new { reasoning_tokens = 3 } },
        });
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        resp.Headers.Add("apim-request-id", "req-unit-1");
        return resp;
    }

    private static OpenAiClient Client(HttpMessageHandler handler, IModelCallRecorder? recorder) =>
        new(new HttpClient(handler), new AiOptions("https://unit.invalid", "gpt-4o", "gpt-4o-mini", ""),
            new FakeCred(), NullLogger<OpenAiClient>.Instance, recorder);

    private static readonly object[] Messages = { new { role = "user", content = "synthetic" } };

    [Fact]
    public async Task A_successful_call_records_the_complete_row()
    {
        var recorder = new ListRecorder();
        var client = Client(new CannedHandler(_ => Ok()), recorder);

        var (content, served, fp) = await client.CompleteWithMetaAsync(Messages, null, 64, "gpt-4o",
            ctx: new CallContext("doc-unit-1", ModelCallSite.Extraction, PromptHash: "hash-1"));

        Assert.Equal("{\"ok\":true}", content);
        Assert.Equal("gpt-4o-2024-11-20", served);
        Assert.Equal("fp_unit", fp);

        var r = Assert.Single(recorder.Records);
        Assert.Equal("doc-unit-1", r.Context.DocumentId);
        Assert.Equal(ModelCallSite.Extraction, r.Context.Site);
        Assert.Equal("hash-1", r.Context.PromptHash);
        Assert.Equal("gpt-4o", r.Deployment);
        Assert.Equal("gpt-4o-2024-11-20", r.ServedModel);
        Assert.Equal("fp_unit", r.SystemFingerprint);
        Assert.Equal(111, r.PromptTokens);
        Assert.Equal(22, r.CompletionTokens);
        Assert.Equal(3, r.ReasoningTokens);
        Assert.Equal("req-unit-1", r.RequestId);
        Assert.Equal(200, r.HttpStatus);
        Assert.Null(r.Error);
        Assert.Contains("\"synthetic\"", r.RequestBody, StringComparison.Ordinal); // the wire body, verbatim
        Assert.Contains("gpt-4o-2024-11-20", r.ResponseBody, StringComparison.Ordinal); // the response, verbatim
    }

    [Fact]
    public async Task Image_payloads_are_elided_in_the_recorded_request_but_sent_in_full()
    {
        var recorder = new ListRecorder();
        var handler = new CannedHandler(_ => Ok());
        var client = Client(handler, recorder);
        var imageBytes = Encoding.UTF8.GetBytes(new string('p', 600));
        var b64 = Convert.ToBase64String(imageBytes);
        var messages = new object[]
        {
            new { role = "user", content = new object[] { new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{b64}", detail = "high" } } } },
        };

        await client.CompleteWithMetaAsync(messages, null, 64, "gpt-4o", ctx: new CallContext("doc-img", ModelCallSite.Transcription));

        Assert.Contains(b64, handler.LastRequestBody, StringComparison.Ordinal); // the wire got the real image
        var r = Assert.Single(recorder.Records);
        Assert.DoesNotContain(b64, r.RequestBody, StringComparison.Ordinal);     // the record did not
        Assert.Contains($"sha256={ModelCallElision.Sha256Hex(imageBytes)}", r.RequestBody, StringComparison.Ordinal);
        Assert.Contains($"bytes={imageBytes.Length}", r.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_call_records_status_and_error_body_then_throws()
    {
        var recorder = new ListRecorder();
        var client = Client(new CannedHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"synthetic-rate-limit\"}", Encoding.UTF8, "application/json"),
        }), recorder);

        await Assert.ThrowsAnyAsync<Exception>(() => client.CompleteWithMetaAsync(Messages, null, 64, "gpt-4o",
            ctx: new CallContext("doc-fail", ModelCallSite.Classify)));

        var r = Assert.Single(recorder.Records);
        Assert.Equal(429, r.HttpStatus);
        Assert.Equal("HTTP 429", r.Error);
        Assert.Contains("synthetic-rate-limit", r.ResponseBody, StringComparison.Ordinal);
        Assert.Null(r.ServedModel); // a failed call has no served-model claim
    }

    [Fact]
    public async Task An_empty_content_response_records_the_row_then_throws_the_budget_exception()
    {
        var recorder = new ListRecorder();
        var client = Client(new CannedHandler(_ => Ok(content: "", finish: "length")), recorder);

        await Assert.ThrowsAsync<EmptyModelResponseException>(() => client.CompleteWithMetaAsync(Messages, null, 64, "gpt-4o",
            ctx: new CallContext("doc-empty", ModelCallSite.Domain)));

        var r = Assert.Single(recorder.Records);
        Assert.Equal(200, r.HttpStatus);
        Assert.StartsWith("empty content", r.Error, StringComparison.Ordinal);
        Assert.Equal(22, r.CompletionTokens); // usage still captured — it is the diagnostic
    }

    [Fact]
    public async Task No_context_means_no_recording()
    {
        var recorder = new ListRecorder();
        var client = Client(new CannedHandler(_ => Ok()), recorder);
        await client.CompleteWithMetaAsync(Messages, null, 64, "gpt-4o");
        Assert.Empty(recorder.Records);
    }

    [Fact]
    public async Task A_recorder_failure_never_fails_the_call()
    {
        var recorder = new ListRecorder { ThrowOnRecord = new InvalidOperationException("store down") };
        var client = Client(new CannedHandler(_ => Ok()), recorder);

        var (content, _, _) = await client.CompleteWithMetaAsync(Messages, null, 64, "gpt-4o",
            ctx: new CallContext("doc-guard", ModelCallSite.Extraction));

        Assert.Equal("{\"ok\":true}", content); // belt-and-braces catch held
    }
}
