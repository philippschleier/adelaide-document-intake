using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Adelaide.Ai;
using Adelaide.Audit;
using Adelaide.Core;
using Adelaide.SharePoint;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Adelaide.Web.Tests;

// THE RUN DEMONSTRATION (docs/CAPTURE.md): one synthetic document per lane through the REAL production worker
// (UploadProcessor.ProcessAsync) with the REAL extractor and OpenAI wrapper — only the HTTP transport and the
// SharePoint filer are faked, so every model_calls row below is produced by the shipped capture path. The test
// output prints the captured rows and the two DocumentDate reconstruction queries (acceptance 1 and 2). The third
// case proves fail-open: with the capture store unusable, the document still files (acceptance 10). All content is
// synthetic; no cloud is touched.
public sealed class CaptureEndToEndDemoTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Directory.CreateTempSubdirectory("adelaide-e2e-demo").FullName;
    public CaptureEndToEndDemoTests(ITestOutputHelper output) => _out = output;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    // ---- fakes: transport + filer + patient directory (everything else is the real pipeline) --------------------

    private sealed class FakeCred : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => new(GetToken(c, ct));
    }

    /// <summary>Answers each chat call from the schema name in the request; classify confidence is scripted per lane.</summary>
    private sealed class ScriptedHandler(double classifyConfidence) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var content =
                body.Contains("\"name\":\"domain\"", StringComparison.Ordinal)
                    ? """{"domain":"Medical","confidence":0.95}"""
                : body.Contains("\"name\":\"classification\"", StringComparison.Ordinal)
                    ? $$"""{"contentType":"Genehmigung","confidence":{{classifyConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"""
                : body.Contains("\"name\":\"selfcheck\"", StringComparison.Ordinal)
                    ? """{"bestaetigt":true,"korrigierterTyp":"Genehmigung","confidence":0.9}"""
                    : """{"kvnr":null,"lastName":"Testfall","firstName":"Synthetik","birthDate":"1950-01-01","documentDate":"2026-05-01","fields":[{"column":"ApprovalNumber","value":"G123456","confidence":0.88},{"column":"ApprovalOutcome","value":"Wie verordnet","confidence":0.85},{"column":"ValidFrom","value":"2026-05-01","confidence":0.8},{"column":"ValidUntil","value":"2026-06-30","confidence":0.8}],"overallConfidence":0.9}""";
            var payload = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content }, finish_reason = "stop" } },
                model = "gpt-4o-2024-11-20",
                system_fingerprint = "fp_demo",
                usage = new { prompt_tokens = 321, completion_tokens = 45, completion_tokens_details = new { reasoning_tokens = 0 } },
            });
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            resp.Headers.Add("apim-request-id", "req-demo-1");
            return resp;
        }
    }

    private sealed class NeverTranscriber : ITranscriber
    {
        public Task<Transcript> TranscribeAsync(IncomingDocument doc, CancellationToken ct = default) =>
            throw new InvalidOperationException("the demo document carries a pre-transcript; the vision call must not run");
    }

    private sealed class SinglePatientDirectory : IPatientDirectory
    {
        public Task<PatientMatch> MatchAsync(ExtractedData data, CancellationToken ct = default) =>
            Task.FromResult(new PatientMatch(MatchOutcome.Single,
                new PatientRef(7, "Q000000000", "Testfall", "Synthetik", new DateOnly(1950, 1, 1), "Active"), "kvnr"));
    }

    private sealed class FakeFiler : IFiler
    {
        // pdfaDeclared FALSE on purpose: the worker's Filed writer must plumb the sink's verdict, not a constant —
        // the hygiene test asserts this exact non-default value (mutation finding).
        public Task<FilingResult> FileAsync(FilingSpec spec, CancellationToken ct = default) => Task.FromResult(new FilingResult("drive-demo-1", PdfADeclared: false, DiRendition: true));
        public Task<bool> ExistsByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> TryDeleteByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default) => Task.FromResult(true);
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    private const string SyntheticTranscript =
        "Genehmigung häuslicher Krankenpflege. Die Krankenkasse genehmigt die verordnete Behandlungspflege wie verordnet. " +
        "Genehmigungsnummer G123456. Gültig ab 01.05.2026 bis 30.06.2026. Ausstellungsdatum 01.05.2026. Synthetisches Testdokument.";

    private (UploadProcessor Proc, SqliteAuditStore Audit, TranscriptStore Capture, CaptureHealth Health) Harness(double classifyConfidence, TranscriptStore? captureOverride = null)
    {
        var audit = new SqliteAuditStore(Path.Combine(_dir, $"audit-{Guid.NewGuid():N}.db"));
        var capture = captureOverride ?? new TranscriptStore(Path.Combine(_dir, $"capture-{Guid.NewGuid():N}.db"));
        var health = new CaptureHealth();
        var opt = new AiOptions("https://unit.invalid", "gpt-4o", "gpt-4o-mini", "");
        var recorder = new StoreCallRecorder(capture, health, NullLogger<StoreCallRecorder>.Instance);
        var client = new OpenAiClient(new HttpClient(new ScriptedHandler(classifyConfidence)), opt, new FakeCred(), NullLogger<OpenAiClient>.Instance, recorder);

        var services = new ServiceCollection();
        services.AddSingleton<ITranscriber>(new NeverTranscriber());
        services.AddSingleton<IExtractor>(new AzureOpenAiExtractor(client, opt, NullLogger<AzureOpenAiExtractor>.Instance));
        services.AddSingleton<IPatientDirectory>(new SinglePatientDirectory());
        services.AddSingleton<IFiler>(new FakeFiler());
        var sp = services.BuildServiceProvider();

        var proc = new UploadProcessor(Channel.CreateUnbounded<UploadJob>(), sp.GetRequiredService<IServiceScopeFactory>(),
            audit, audit, new FilingClaims(), NullLogger<UploadProcessor>.Instance, capture, health);
        return (proc, audit, capture, health);
    }

    /// <summary>Capture a synthetic "PDF" + its carried transcript (the reuse-split path — the pipeline's own
    /// no-vision-call lane, so no native renderer runs inside the test host) and a Received event with the sha.</summary>
    private async Task<string> ArriveAsync(SqliteAuditStore audit, string docId)
    {
        var pdfPath = Path.Combine(_dir, $"{docId}.pdf");
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 synthetic demo document");
        await File.WriteAllBytesAsync(pdfPath, bytes);
        await UploadProcessor.WritePreTranscriptAsync(pdfPath,
            new UploadProcessor.CarriedTranscript(new[] { SyntheticTranscript }, 0.95, "demo-parent", "split-manual-v1"), CancellationToken.None);
        await audit.AppendAsync(new AuditEntry(docId, "Received", "demo", Details: JsonSerializer.Serialize(new
        {
            channel = "Upload", original = "synthetic-demo.pdf",
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
        })));
        return pdfPath;
    }

    private static readonly Regex TwelveHex = new("^[0-9a-f]{12}$");

    // ---- Lane A: auto-file --------------------------------------------------------------------------------------

    [Fact]
    public async Task Demo_lane_A_autofile_every_call_captured_and_fields_stamped_AUTOFILED()
    {
        var (proc, audit, capture, _) = Harness(classifyConfidence: 0.9);
        var pdf = await ArriveAsync(audit, "demoautofile1");

        await proc.ProcessAsync(new UploadJob("demoautofile1", pdf, "synthetic-demo.pdf"), CancellationToken.None);

        Assert.Equal("Filed", (await audit.GetDocumentAsync("demoautofile1"))!.State);

        // Acceptance 2: a model_calls row for EVERY call made — incl. the previously-invisible escalation site.
        var calls = await capture.GetModelCallsAsync("demoautofile1");
        Assert.Equal(new[] { "domain", "classify", "classify-escalation", "self-check", "extraction" },
            calls.Select(c => c.CallSite).ToArray());
        _out.WriteLine("model_calls (lane A):");
        foreach (var c in calls)
        {
            Assert.False(string.IsNullOrEmpty(c.Deployment));
            Assert.Equal("gpt-4o-2024-11-20", c.ServedModel);
            Assert.Matches(TwelveHex, c.PromptHash!);
            Assert.Equal(321, c.PromptTokens);
            Assert.Equal(45, c.CompletionTokens);
            Assert.Equal("req-demo-1", c.RequestId);
            Assert.Equal(200, c.HttpStatus);
            Assert.NotNull(c.RequestBody);
            Assert.NotNull(c.ResponseBody);
            _out.WriteLine($"  {c.CallSite,-20} attempt={c.Attempt} deployment={c.Deployment,-12} served={c.ServedModel} promptHash={c.PromptHash} tokens={c.PromptTokens}+{c.CompletionTokens} latencyMs={c.LatencyMs} requestId={c.RequestId} status={c.HttpStatus}");
        }

        // Acceptance 1, auto-file lane — the DocumentDate question from the stores alone:
        var dd = (await capture.GetCorpusFieldsAsync("demoautofile1")).Single(f => f.FieldName == "DocumentDate");
        _out.WriteLine("");
        _out.WriteLine("reconstruction (lane A): SELECT Proposed, Confidence, Source, Outcome FROM corpus_fields WHERE DocumentId='demoautofile1' AND FieldName='DocumentDate'");
        _out.WriteLine($"  -> proposed={dd.Proposed} confidence={dd.Confidence} source={dd.Source} outcome={dd.Outcome}");
        Assert.True(dd.Proposed);
        Assert.Equal(0.9, dd.Confidence);
        Assert.Equal("vision", dd.Source);
        Assert.Equal("AUTOFILED", dd.Outcome);                       // unlabeled BY DESIGN — never NULL-pending
        Assert.Empty(await capture.GetAdjudicationsAsync("demoautofile1")); // no human touched it — no value pairs
    }

    // ---- C6: the worker-lane hygiene sweep (verification finding: only the drain core's payloads were pinned;
    // these two lanes exercise the worker's Filing/Filed/NeedsReview/Matched writers — the PHI-heaviest sites,
    // since the canonical filename embeds patient name + KVNR). ---------------------------------------------------

    [Fact]
    public async Task Both_worker_lanes_keep_every_event_payload_structural()
    {
        // Lane A: auto-file. The canonical name reaches the projection via the side-channel — never the chain.
        var (procA, auditA, _, _) = Harness(classifyConfidence: 0.9);
        var pdfA = await ArriveAsync(auditA, "hygienelane0a");
        await procA.ProcessAsync(new UploadJob("hygienelane0a", pdfA, "synthetic-demo.pdf"), CancellationToken.None);
        var docA = await auditA.GetDocumentAsync("hygienelane0a");
        Assert.Equal("Filed", docA!.State);
        Assert.False(string.IsNullOrEmpty(docA.FileName)); // the projection carries the patient-bearing canonical name…
        AssertStructural(await auditA.GetEventsAsync("hygienelane0a"), docA.FileName!);
        var filedA = (await auditA.GetEventsAsync("hygienelane0a")).Single(e => e.Type == "Filed");
        Assert.Contains("\"pdfaDeclared\":false", filedA.Details); // C10 rides every Filed event — the FAKE reports false, so a hardcoded true fails here
        Assert.Contains("\"diRendition\":true", filedA.Details);

        // Lane B: review. NeedsReview carries reasonCodes; the German prose is projection-only (erasable).
        var (procB, auditB, _, _) = Harness(classifyConfidence: 0.55);
        var pdfB = await ArriveAsync(auditB, "hygienelane0b");
        await procB.ProcessAsync(new UploadJob("hygienelane0b", pdfB, "synthetic-demo.pdf"), CancellationToken.None);
        var docB = await auditB.GetDocumentAsync("hygienelane0b");
        Assert.Equal("NeedsReview", docB!.State);
        AssertStructural(await auditB.GetEventsAsync("hygienelane0b"), "no-canonical-name-on-this-lane");
        var review = (await auditB.GetEventsAsync("hygienelane0b")).Single(e => e.Type == "NeedsReview");
        Assert.Contains("reasonCodes", review.Details);
    }

    /// <summary>No event payload may carry the canonical filename, the original name, or a prose/error/name key —
    /// a regression re-adding any of these to a worker writer permanently embeds PHI in the un-erasable chain.
    /// The seeded Received is exempt: ArriveAsync authors it in the pre-C6 shape as history stand-in.</summary>
    private static void AssertStructural(IReadOnlyList<AuditEvent> events, string canonicalFileName)
    {
        foreach (var e in events.Where(e => e.Type != "Received"))
        {
            Assert.DoesNotContain(canonicalFileName, e.Details);
            Assert.DoesNotContain("synthetic-demo.pdf", e.Details);
            Assert.DoesNotContain("\"fileName\"", e.Details);
            Assert.DoesNotContain("\"original\"", e.Details);
            Assert.DoesNotContain("\"reasons\"", e.Details);
            Assert.DoesNotContain("\"error\"", e.Details); // "errorClass" is allowed — this matches only the exact key
        }
    }

    // ---- Lane B: review ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Demo_lane_B_review_proposal_confidence_and_human_answer_reconstructable_without_the_sidecar()
    {
        var (proc, audit, capture, health) = Harness(classifyConfidence: 0.55); // low → escalates, stays low → review
        var pdf = await ArriveAsync(audit, "demoreview0001");

        await proc.ProcessAsync(new UploadJob("demoreview0001", pdf, "synthetic-demo.pdf"), CancellationToken.None);
        Assert.Equal("NeedsReview", (await audit.GetDocumentAsync("demoreview0001"))!.State);

        // The human corrects DocumentDate and files — the endpoint's capture block, verbatim inputs.
        var model = JsonNode.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(pdf, ".review.json")))!;
        var body = new ReviewSubmission("demoreview0001", "Genehmigung", 7, new Dictionary<string, string?>
        {
            ["DocumentDate"] = "2026-05-02", // the correction
            ["ApprovalNumber"] = "G123456",
            ["ApprovalOutcome"] = "Wie verordnet",
            ["ValidFrom"] = "2026-05-01",
            ["ValidUntil"] = "2026-06-30",
            ["LastName"] = "Testfall", ["FirstName"] = "Synthetik", ["BirthDate"] = "1950-01-01",
        }, FieldProvenance: new Dictionary<string, string> { ["DocumentDate"] = "typed", ["ApprovalNumber"] = "prefill" });
        var now = DateTimeOffset.UtcNow;
        await ReviewCapture.SetLabelCheckedAsync(capture, health, NullLogger.Instance, "demoreview0001", "Genehmigung", DocumentDomain.Medical, "drive-demo-1", now, CancellationToken.None);
        await ReviewCapture.RecordReviewOutcomesAsync(capture, health, NullLogger.Instance, "demoreview0001", model, body,
            ReviewCapture.SanitizeProvenance(model, body), now, CancellationToken.None);

        // Acceptance 1, review lane — no sidecar consulted from here on:
        var dd = (await capture.GetCorpusFieldsAsync("demoreview0001")).Single(f => f.FieldName == "DocumentDate");
        var pair = (await capture.GetAdjudicationsAsync("demoreview0001")).Single(a => a.FieldName == "DocumentDate");
        _out.WriteLine("reconstruction (lane B):");
        _out.WriteLine("  SELECT Proposed, Confidence, Source, Outcome FROM corpus_fields WHERE DocumentId='demoreview0001' AND FieldName='DocumentDate'");
        _out.WriteLine($"    -> proposed={dd.Proposed} confidence={dd.Confidence} source={dd.Source} outcome={dd.Outcome}");
        _out.WriteLine("  SELECT ProposedValue, FinalValue, Signal, Provenance FROM field_adjudications WHERE DocumentId='demoreview0001' AND FieldName='DocumentDate'");
        _out.WriteLine($"    -> proposed={pair.ProposedValue} final={pair.FinalValue} signal={pair.Signal} provenance={pair.Provenance}");

        Assert.True(dd.Proposed);
        Assert.Equal(0.9, dd.Confidence);
        Assert.Equal("CORRECTION", dd.Outcome);
        Assert.Equal("2026-05-01", pair.ProposedValue);
        Assert.Equal("2026-05-02", pair.FinalValue);
        Assert.Equal("correction", pair.Signal);
        Assert.Equal("typed", pair.Provenance);                       // the observed writer, beside the inference
        Assert.Equal(1, await capture.LabeledRowCountAsync());        // the label landed
        Assert.Equal(0, health.LabelsMissedSinceStart);

        // The escalation really ran as its own site on this lane too.
        Assert.Contains("classify-escalation", (await capture.GetModelCallsAsync("demoreview0001")).Select(c => c.CallSite));
    }

    // ---- Lane C: capture store unusable — the document still processes (acceptance 10) ---------------------------

    [Fact]
    public async Task Demo_lane_C_a_dead_capture_store_never_stops_a_document()
    {
        // Kill the store by replacing its DB file with a same-named directory (SQLITE_CANTOPEN on every OS — an
        // exclusive FileStream lock is advisory-only on Unix and would leave the store healthy on Linux CI).
        var lockedPath = Path.Combine(_dir, "locked-capture.db");
        var locked = new TranscriptStore(lockedPath);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(lockedPath);
        Directory.CreateDirectory(lockedPath);

        var (proc, audit, _, health) = Harness(classifyConfidence: 0.9, captureOverride: locked);
        var pdf = await ArriveAsync(audit, "demodeadstore1");

        await proc.ProcessAsync(new UploadJob("demodeadstore1", pdf, "synthetic-demo.pdf"), CancellationToken.None);

        Assert.Equal("Filed", (await audit.GetDocumentAsync("demodeadstore1"))!.State); // processing survived capture
        Assert.True(health.FailuresTotal >= 1);                                        // and the failures were counted
        _out.WriteLine($"lane C: document Filed with capture store locked; counted failures by area: " +
            string.Join(", ", health.FailuresByArea().Select(kv => $"{kv.Key}={kv.Value}")));
    }
}
