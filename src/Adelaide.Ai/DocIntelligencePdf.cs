using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Adelaide.Core;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Adelaide.Ai;

/// <summary>
/// Produces a searchable PDF (the original scan plus an invisible OCR text layer) via Azure AI Document
/// Intelligence — prebuilt-read with <c>output=pdf</c>. Used at filing time so archived PDFs are full-text
/// searchable in SharePoint and on the desktop. This is an OUTPUT concern only: it never touches how the
/// document is understood (vision → classify → extract stays as-is).
///
/// Best-effort by contract: returns <c>null</c> if DI is not configured or anything fails, so the caller
/// files the original instead — a missing text layer must never block or lose a document.
///
/// Capture (model boundary): when a <see cref="CallContext"/> is supplied, each analyze operation records a
/// model-call row — request with the PDF elided to sha256+size, the final poll body verbatim (D1's binary output
/// by hash+size instead), latency, gateway request id, and failures with their error. The single analyze URL below
/// stays the only DI write point in the system. Capture never changes behaviour: a recording failure is logged
/// and the operation's result is what it always was.
/// </summary>
public sealed class DocIntelligencePdf(HttpClient http, AiOptions opt, TokenCredential cred, ILogger<DocIntelligencePdf> log, IModelCallRecorder? recorder = null) : IPdfTextLayer
{
    private const string ApiVersion = "2024-11-30";   // GA; supports output=pdf (searchable PDF) + selection marks (layout)
    private const string ReadModel = "prebuilt-read";     // cheapest OCR model — text layer for the searchable PDF
    private const string LayoutModel = "prebuilt-layout"; // adds selection marks (checkbox state + confidence + geometry)
    private static readonly string[] Scope = { "https://cognitiveservices.azure.com/.default" };

    public Task<byte[]?> MakeSearchableAsync(byte[] pdf, CallContext? ctx = null, CancellationToken ct = default) =>
        MakeSearchableImplAsync(pdf, preserveRotation: true, ctx, ct);

    /// <summary>The Volltext fallback: the OCR-recognized text of <paramref name="pdf"/> (via <see cref="ReadAsync"/>'s
    /// <c>prebuilt-read</c>), which is exactly the text <see cref="MakeSearchableAsync"/> burns into the searchable
    /// layer. Null on failure / not configured, so it never blocks filing.</summary>
    public async Task<string?> ExtractTextAsync(byte[] pdf, CallContext? ctx = null, CancellationToken ct = default) =>
        (await ReadAsync(pdf, ctx, ct))?.Content;

    /// <summary>Diagnostic only: the DI searchable PDF WITHOUT re-applying the input /Rotate — exposes DI's raw output
    /// orientation (whether DI preserves or strips the page /Rotate attribute).</summary>
    public Task<byte[]?> MakeSearchableRawAsync(byte[] pdf, CallContext? ctx = null, CancellationToken ct = default) =>
        MakeSearchableImplAsync(pdf, preserveRotation: false, ctx, ct);

    private async Task<byte[]?> MakeSearchableImplAsync(byte[] pdf, bool preserveRotation, CallContext? ctx, CancellationToken ct)
    {
        ctx = ctx is null ? null : ctx with { Site = ModelCallSite.DiReadPdf }; // the site is this method's identity, not the caller's guess
        if (string.IsNullOrWhiteSpace(opt.DiEndpoint))
        {
            log.LogDebug("Document Intelligence endpoint not configured; filing original (non-searchable) PDF.");
            return null;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? requestId = null;
        try
        {
            var (resultUrl, reqId) = await StartAsync(pdf, ReadModel, "&output=pdf", ct); // POST :analyze?output=pdf → Operation-Location
            requestId = reqId;
            if (await PollAsync(resultUrl, ct) is { } pollBody)                           // poll the long-running operation
            {
                var outPdf = await DownloadPdfAsync(resultUrl, ct);                       // GET analyzeResults/{id}/pdf
                // DI rasterizes honoring /Rotate for OCR but DROPS the page /Rotate attribute from its output PDF, so the
                // searchable copy would display in the RAW (scanned) orientation — losing any scanner or reviewer rotation.
                // Re-apply the input's /Rotate onto the output so the filed artifact displays in the intended orientation
                // (its re-OCR'd text layer reads upright regardless).
                if (preserveRotation) outPdf = ReapplyRotation(pdf, outPdf); // preserveRotation:false exposes DI's raw output for diagnostics
                log.LogInformation("DI searchable-PDF ok in {ElapsedMs}ms ({Bytes} bytes)", sw.ElapsedMilliseconds, outPdf.Length);
                // The binary output by hash+size (never bytes): the bytes themselves persist as the filed SharePoint
                // artifact, reachable from the doc's Filed event (driveItemId). ResponseBody = the poll body verbatim.
                await RecordAsync(ctx, pdf, ReadModel, "&output=pdf", pollBody,
                    ModelCallElision.Sha256Hex(outPdf), outPdf.Length, sw.ElapsedMilliseconds, requestId, 200, error: null, ct);
                return outPdf;
            }
            await RecordAsync(ctx, pdf, ReadModel, "&output=pdf", responseBody: null, null, null, sw.ElapsedMilliseconds, requestId, null, "poll timeout", ct);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning("DI searchable-PDF failed in {ElapsedMs}ms; filing original PDF instead. {ExType} [{ExChain}] httpStatus={HttpStatus} apiVersion={ApiVersion} diModel={DiModel}",
                sw.ElapsedMilliseconds, ex.GetType().Name, Redact.Chain(ex), ex.Data["httpStatus"], ApiVersion, ReadModel);
            await RecordFailureAsync(ctx, pdf, ReadModel, "&output=pdf", sw.ElapsedMilliseconds, requestId, ex, ct);
            return null;
        }
    }

    /// <summary>Diagnostic/probe: <c>prebuilt-read</c> WITHOUT <c>output=pdf</c> → the recognized text
    /// (<c>content</c>) and each page's DI-detected rotation <c>angle</c>. This is the SAME OCR that
    /// <see cref="MakeSearchableAsync"/> burns into the searchable layer, so <c>content</c> here is exactly what
    /// SharePoint would index. Best-effort: null on failure / not configured.</summary>
    public async Task<ReadResult?> ReadAsync(byte[] pdf, CallContext? ctx = null, CancellationToken ct = default)
    {
        ctx = ctx is null ? null : ctx with { Site = ModelCallSite.DiOcr };
        if (string.IsNullOrWhiteSpace(opt.DiEndpoint)) return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? requestId = null;
        try
        {
            var (resultUrl, reqId) = await StartAsync(pdf, ReadModel, "", ct); // JSON result only (no output=pdf)
            requestId = reqId;
            if (await PollAsync(resultUrl, ct) is not { } body)
            {
                await RecordAsync(ctx, pdf, ReadModel, "", responseBody: null, null, null, sw.ElapsedMilliseconds, requestId, null, "poll timeout", ct);
                return null;
            }
            await RecordAsync(ctx, pdf, ReadModel, "", body, null, null, sw.ElapsedMilliseconds, requestId, 200, error: null, ct);
            using var d = JsonDocument.Parse(body);
            if (!d.RootElement.TryGetProperty("analyzeResult", out var ar)) return null;
            var content = ar.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var angles = new List<double>();
            if (ar.TryGetProperty("pages", out var pages))
                foreach (var p in pages.EnumerateArray())
                    angles.Add(p.TryGetProperty("angle", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDouble() : 0);
            return new ReadResult(content, angles);
        }
        catch (Exception ex)
        {
            log.LogWarning("DI read probe failed. {ExType}", ex.GetType().Name);
            await RecordFailureAsync(ctx, pdf, ReadModel, "", sw.ElapsedMilliseconds, requestId, ex, ct);
            return null;
        }
    }

    /// <summary>Best-effort layout analysis (prebuilt-layout): returns selection marks (checkbox state +
    /// confidence + geometry) and text lines, so checkbox-bearing fields can be read from a real detector
    /// instead of inferred from prose. <paramref name="pages"/> (e.g. "1-2") limits billing to those pages.
    /// Returns null on any failure — extraction then falls back to the vision reading.</summary>
    public async Task<LayoutResult?> AnalyzeLayoutAsync(byte[] pdf, string? pages = null, CallContext? ctx = null, CancellationToken ct = default)
    {
        ctx = ctx is null ? null : ctx with { Site = ModelCallSite.DiLayout };
        if (string.IsNullOrWhiteSpace(opt.DiEndpoint)) { log.LogDebug("DI endpoint not configured; no selection marks."); return null; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var queryExtra = string.IsNullOrWhiteSpace(pages) ? "" : $"&pages={pages}";
        string? requestId = null;
        try
        {
            var (resultUrl, reqId) = await StartAsync(pdf, LayoutModel, queryExtra, ct);
            requestId = reqId;
            var body = await PollAsync(resultUrl, ct);
            var result = body is null ? null : LayoutResult.Parse(body);
            log.LogInformation("DI layout analysis ok in {ElapsedMs}ms ({Marks} marks)", sw.ElapsedMilliseconds, result?.Marks.Count ?? 0);
            await RecordAsync(ctx, pdf, LayoutModel, queryExtra, body, null, null, sw.ElapsedMilliseconds, requestId,
                body is null ? null : 200, body is null ? "poll timeout" : null, ct);
            return result;
        }
        catch (Exception ex)
        {
            log.LogWarning("DI layout analysis failed in {ElapsedMs}ms; no selection marks. {ExType} [{ExChain}] httpStatus={HttpStatus} apiVersion={ApiVersion} diModel={DiModel}",
                sw.ElapsedMilliseconds, ex.GetType().Name, Redact.Chain(ex), ex.Data["httpStatus"], ApiVersion, LayoutModel);
            await RecordFailureAsync(ctx, pdf, LayoutModel, queryExtra, sw.ElapsedMilliseconds, requestId, ex, ct);
            return null;
        }
    }

    private async Task<(string ResultUrl, string? RequestId)> StartAsync(byte[] pdf, string model, string queryExtra, CancellationToken ct)
    {
        var url = $"{opt.DiEndpoint}/documentintelligence/documentModels/{model}:analyze?api-version={ApiVersion}{queryExtra}";
        var payload = new Dictionary<string, object?> { ["base64Source"] = Convert.ToBase64String(pdf) };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        await AuthAsync(req, ct);
        using var resp = await http.SendAsync(req, ct);
        var requestId = resp.Headers.TryGetValues("apim-request-id", out var v) ? v.FirstOrDefault()
            : resp.Headers.TryGetValues("x-ms-request-id", out var v2) ? v2.FirstOrDefault() : null;
        if (!resp.IsSuccessStatusCode)
            throw Redact.HttpFail((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), "DI analyze");
        return resp.Headers.TryGetValues("Operation-Location", out var loc) && loc.FirstOrDefault() is { } l
            ? (l, requestId)
            : throw new InvalidOperationException("DI analyze: missing Operation-Location header.");
    }

    /// <summary>Polls the long-running operation; returns the succeeded result body, or null on timeout.</summary>
    private async Task<string?> PollAsync(string resultUrl, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++) // ceiling ~2 min; a typical analyze completes in seconds
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, resultUrl);
            await AuthAsync(req, ct);
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw Redact.HttpFail((int)resp.StatusCode, body, "DI poll");
            var status = JsonDocument.Parse(body).RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            switch (status)
            {
                case "succeeded": return body;
                case "failed": throw new InvalidOperationException("DI analysis returned status=failed"); // body may carry OCR'd content (PHI) — omit
                default: await Task.Delay(TimeSpan.FromSeconds(2), ct); break; // notStarted | running
            }
        }
        log.LogWarning("DI analysis did not complete within the polling window.");
        return null;
    }

    private async Task<byte[]> DownloadPdfAsync(string resultUrl, CancellationToken ct)
    {
        // resultUrl = {endpoint}/.../analyzeResults/{id}?api-version=… → insert "/pdf" before the query string
        var q = resultUrl.IndexOf('?');
        var pdfUrl = q >= 0 ? $"{resultUrl[..q]}/pdf{resultUrl[q..]}" : $"{resultUrl}/pdf";
        using var req = new HttpRequestMessage(HttpMethod.Get, pdfUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));
        await AuthAsync(req, ct);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw Redact.HttpFail((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), "DI get-pdf");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task AuthAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var token = await cred.GetTokenAsync(new TokenRequestContext(Scope), ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    // ---- capture ----
    /// <summary>Record one DI operation. The request is reconstructed as {url, body} with the PDF elided to
    /// sha256+size (the exact wire body's only other content is that base64). Belt-and-braces fail-open like the
    /// OpenAI wrapper: the recorder logs+counts its own failures, and this catch guarantees a recorder bug can
    /// never change the operation's outcome.</summary>
    private async Task RecordAsync(CallContext? ctx, byte[] pdf, string model, string queryExtra, string? responseBody,
        string? outputSha256, long? outputBytes, long latencyMs, string? requestId, int? httpStatus, string? error, CancellationToken ct)
    {
        if (ctx is null || recorder is null) return;
        try
        {
            var request = JsonSerializer.Serialize(new
            {
                url = $"documentModels/{model}:analyze?api-version={ApiVersion}{queryExtra}",
                body = new { base64Source = ModelCallElision.Marker("application/pdf", pdf) },
            });
            await recorder.RecordAsync(new ModelCallRecord(
                ctx, DateTimeOffset.UtcNow,
                Deployment: model, ServedModel: $"{model}@{ApiVersion}", SystemFingerprint: null, ReasoningEffort: null,
                RequestBody: request, ResponseBody: responseBody,
                OutputSha256: outputSha256, OutputBytes: outputBytes, OutputRef: outputSha256 is null ? null : "filed-artifact",
                LatencyMs: latencyMs, RequestId: requestId, HttpStatus: httpStatus, Error: error), ct);
        }
        catch (Exception ex)
        {
            log.LogWarning("DI call capture failed for {DocId} at {Site} ({ExType}); the operation itself is unaffected.",
                ctx.DocumentId, ctx.Site.Wire(), ex.GetType().Name);
        }
    }

    private Task RecordFailureAsync(CallContext? ctx, byte[] pdf, string model, string queryExtra, long latencyMs,
        string? requestId, Exception ex, CancellationToken ct) =>
        RecordAsync(ctx, pdf, model, queryExtra, responseBody: null, null, null, latencyMs, requestId,
            ex.Data["httpStatus"] as int?, $"{ex.GetType().Name}", ct);

    // Restore the input PDF's page /Rotate onto the DI output (which strips it). Applies ONLY the pages where the output
    // differs from the input, so it never double-rotates if DI ever preserves rotation. Best-effort: on any PDF parse
    // issue it returns the DI output unchanged (a searchable-but-mis-rotated PDF beats a corrupt or unfiled one).
    private byte[] ReapplyRotation(byte[] inputPdf, byte[] outputPdf)
    {
        try
        {
            var wanted = PdfRotation.ReadRotations(inputPdf);
            if (wanted.Values.All(v => v == 0)) return outputPdf; // input carries no rotation → nothing to restore
            var current = PdfRotation.ReadRotations(outputPdf);
            var delta = wanted.Where(kv => current.TryGetValue(kv.Key, out var c) && c != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            return delta.Count == 0 ? outputPdf : PdfRotation.WithRotations(outputPdf, delta);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not re-apply /Rotate to the DI searchable output ({ExType}); filing DI output as-is.", ex.GetType().Name);
            return outputPdf;
        }
    }
}

/// <summary>Recognized text + per-page DI-detected rotation angle from prebuilt-read (probe/diagnostic).</summary>
public sealed record ReadResult(string Content, IReadOnlyList<double> PageAngles);

/// <summary>A checkbox detected by Document Intelligence: state, the model's confidence, and its centre on
/// the page as a fraction (0..1) of page width/height — so positions are resolution- and unit-independent.</summary>
public sealed record SelectionMark(int Page, bool Selected, double Confidence, double Cx, double Cy);

/// <summary>A recognised text line with its centre and bounding box, all as page fractions (0..1).</summary>
public sealed record LayoutLine(int Page, string Content, double Cx, double Cy, double X0, double Y0, double X1, double Y1);

/// <summary>Parsed prebuilt-layout output: every selection mark and text line, with a helper that pairs each
/// mark to its most likely label (the nearest line on the same row). Label pairing is geometric and relative,
/// so it tolerates the skew and scaling of real scans far better than hard-coded pixel positions would.</summary>
public sealed record LayoutResult(IReadOnlyList<SelectionMark> Marks, IReadOnlyList<LayoutLine> Lines)
{
    public static LayoutResult Parse(string json)
    {
        var marks = new List<SelectionMark>();
        var lines = new List<LayoutLine>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("analyzeResult", out var ar) || !ar.TryGetProperty("pages", out var pages))
            return new LayoutResult(marks, lines);
        foreach (var page in pages.EnumerateArray())
        {
            var pno = page.TryGetProperty("pageNumber", out var pn) ? pn.GetInt32() : 0;
            double w = page.TryGetProperty("width", out var ww) ? ww.GetDouble() : 1;
            double h = page.TryGetProperty("height", out var hh) ? hh.GetDouble() : 1;
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;
            if (page.TryGetProperty("selectionMarks", out var sm))
                foreach (var m in sm.EnumerateArray())
                {
                    if (Box(m, w, h) is not { } b) continue; // skip a mark with no usable polygon
                    var state = m.TryGetProperty("state", out var st) ? st.GetString() : null;
                    var conf = m.TryGetProperty("confidence", out var cf) ? cf.GetDouble() : 0;
                    marks.Add(new SelectionMark(pno, state == "selected", conf, b.cx, b.cy));
                }
            if (page.TryGetProperty("lines", out var ln))
                foreach (var l in ln.EnumerateArray())
                {
                    if (Box(l, w, h) is not { } b) continue;
                    var content = l.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    lines.Add(new LayoutLine(pno, content, b.cx, b.cy, b.x0, b.y0, b.x1, b.y1));
                }
        }
        return new LayoutResult(marks, lines);
    }

    private static (double cx, double cy, double x0, double y0, double x1, double y1)? Box(JsonElement el, double w, double h)
    {
        // Need at least two complete (x,y) pairs; an odd-length array would silently drop the last coordinate.
        if (!el.TryGetProperty("polygon", out var poly) || poly.GetArrayLength() < 4 || poly.GetArrayLength() % 2 != 0)
            return null;
        var arr = poly.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        for (int i = 0; i + 1 < arr.Length; i += 2)
        {
            minx = Math.Min(minx, arr[i]); maxx = Math.Max(maxx, arr[i]);
            miny = Math.Min(miny, arr[i + 1]); maxy = Math.Max(maxy, arr[i + 1]);
        }
        return ((minx + maxx) / 2 / w, (miny + maxy) / 2 / h, minx / w, miny / h, maxx / w, maxy / h);
    }

    /// <summary>Pair each selection mark with its likely label: the nearest non-empty line on the same row
    /// (within <paramref name="rowTol"/> of the mark's vertical centre), biased toward the line to the right
    /// (German Muster forms read ☐ Label). Gap is the horizontal distance to the chosen label (NaN if none).</summary>
    public IReadOnlyList<(SelectionMark Mark, string Label, double Gap)> WithLabels(double rowTol = 0.012)
    {
        var outp = new List<(SelectionMark, string, double)>();
        foreach (var m in Marks)
        {
            LayoutLine? best = null;
            double bestScore = double.MaxValue;
            foreach (var l in Lines)
            {
                if (l.Page != m.Page || l.Content.Trim().Length <= 1) continue; // skip blanks and lone tick glyphs ("X")
                if (Math.Abs(l.Cy - m.Cy) > rowTol) continue;
                var gap = Math.Abs(l.Cx - m.Cx);
                var score = gap + (l.X0 >= m.Cx ? 0 : 0.05); // prefer the label starting to the mark's right
                if (score < bestScore) { bestScore = score; best = l; }
            }
            outp.Add((m, best?.Content ?? "(no label)", best is null ? double.NaN : Math.Abs(best.Cx - m.Cx)));
        }
        return outp;
    }

    /// <summary>A compact, reading-order list of confidently-detected checkboxes with their labels, for the extractor
    /// to consume as ground truth. Marks below <paramref name="minConf"/> are dropped — DI's false detections cluster
    /// near 0.10, the real boxes near 0.9, so the threshold separates them cleanly — as are degenerate labels.</summary>
    public string ToEvidence(double minConf = 0.5)
    {
        var rows = WithLabels()
            .Where(x => x.Mark.Confidence >= minConf && x.Label.Trim().Length > 1 && x.Label != "(no label)")
            .OrderBy(x => x.Mark.Page).ThenBy(x => x.Mark.Cy).ThenBy(x => x.Mark.Cx)
            .Select(x => $"{(x.Mark.Selected ? "[X]" : "[ ]")} {x.Label.Trim()}");
        return string.Join("\n", rows);
    }

    /// <summary>Deterministic Erst-/Folgeverordnung from the selection marks: the two boxes sit just left of the
    /// "Erstver…"/"Folgever…" labels, so take the mark nearest each label and return whichever is the SOLE ticked
    /// one. Null when the labels/marks aren't found confidently or the ticks are ambiguous (both or neither) — the
    /// caller then leaves the field to the vision reading. This is the no-barcode fallback for the one field the
    /// vision transcript most often gets wrong.</summary>
    public string? PrescriptionOrder(double minConf = 0.85, double rowTol = 0.02)
    {
        var erst = LabelLine("erstver");
        var folge = LabelLine("folgever");
        if (erst is null || folge is null) return null;
        var em = NearestMark(erst, minConf, rowTol);
        var fm = NearestMark(folge, minConf, rowTol);
        if (em is null || fm is null || ReferenceEquals(em, fm) || em.Selected == fm.Selected) return null;
        return em.Selected ? "Erstverordnung" : "Folgeverordnung";
    }

    private LayoutLine? LabelLine(string token) => Lines.FirstOrDefault(l =>
        l.Content.Replace("-", "").Replace(" ", "").Contains(token, StringComparison.OrdinalIgnoreCase));

    private SelectionMark? NearestMark(LayoutLine label, double minConf, double rowTol)
    {
        SelectionMark? best = null;
        var bestGap = double.MaxValue;
        foreach (var m in Marks)
        {
            if (m.Page != label.Page || m.Confidence < minConf || Math.Abs(m.Cy - label.Cy) > rowTol) continue;
            var gap = Math.Abs(m.Cx - label.Cx);
            if (gap < bestGap) { bestGap = gap; best = m; }
        }
        return best;
    }
}
