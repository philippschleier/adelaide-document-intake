using System.Text.Json;
using Adelaide.Core;
using Microsoft.Extensions.Logging;

namespace Adelaide.Ai;

/// <summary>
/// <see cref="ITranscriber"/> backed by gpt-4o vision: render PDF pages → run the free local barcode pass →
/// send page images to gpt-4o for a transcribe-only Markdown rendering (+ a legibility score for the gate).
/// Barcode decode piggybacks on the images we already render, so it adds no API cost.
/// </summary>
public sealed class VisionTranscriber(OpenAiClient ai, AiOptions opt, ILogger<VisionTranscriber> log) : ITranscriber
{
    private const int Dpi = 300;        // high enough for the DataMatrix barcode
    private const int MaxPages = 15;    // cost guard
    private const int VisionMaxWidth = 1500; // downscale for the vision payload

    // The prompt TEMPLATE, named so it can be content-hashed for provenance (see PromptHash). The user turn is a format
    // string over the page count — the only per-document variation — so hashing the template rather than the rendered
    // prompt yields one stable id per prompt revision instead of one per document.
    private const string SystemPrompt = "Du bist ein präziser Transkriptionsdienst für eingescannte deutsche Pflegedienst-Dokumente (oft Formulare wie Muster 12 oder Kassen-/Behördenschreiben). Gib den GESAMTEN sichtbaren Text VOLLSTÄNDIG und WORTGETREU als Markdown wieder — jedes Feld, jede Beschriftung, jeden Wert, jedes Datum, jeden Zeitraum und jeden Ankreuzstatus. Fasse nichts zusammen, lasse nichts aus, erfinde nichts; bei Ankreuzfeldern zählt ausschließlich der physisch sichtbare Zustand — ergänze niemals Kreuze aus dem Sinngehalt. Gib zusätzlich eine Lesbarkeit 'legibility' von 0 bis 1 an.";

    private const string UserPromptTemplate = "Transkribiere die {0} Seite(n) VOLLSTÄNDIG und wortgetreu nach Markdown und gib das Ergebnis als Array `pages` mit GENAU {0} Einträgen zurück — einen Eintrag pro Seite, in Seitenreihenfolge; jeder Eintrag enthält AUSSCHLIESSLICH die vollständige Markdown-Transkription genau dieser einen Seite (nichts von anderen Seiten). Transkribiere inklusive ALLER Formularfelder, Beschriftungen, Datumsangaben, Zeiträume (z. B. \"Verordnungszeitraum von … bis …\") und Ankreuzfelder. Stelle JEDES Ankreuzkästchen mit seinem TATSÄCHLICH sichtbaren Status dar: [X] = sichtbar angekreuzt, [ ] = nicht angekreuzt — auch bei Optionspaaren am Formularkopf wie Erstverordnung/Folgeverordnung. Leite Kreuze NIEMALS aus Bedeutung oder Logik ab: ist z. B. nur \"beidseits\" angekreuzt, bleiben \"rechts\" und \"links\" [ ], sofern sie nicht selbst ein Kreuz tragen. Ist nicht eindeutig erkennbar, ob ein Kästchen angekreuzt ist, schreibe [?]. Tabellen als Markdown-Tabellen. Fasse nichts zusammen und lasse nichts weg; markiere Unleserliches mit [unleserlich].";

    /// <summary>Identifies this prompt revision on every Transcribed event. Covers the two prompt literals, the render
    /// knobs that decide what the model actually sees, and the response schema — anything that changes the model's task.</summary>
    internal static readonly string TemplateHash =
        PromptHash.Of(SystemPrompt, UserPromptTemplate, $"dpi={Dpi};maxPages={MaxPages};maxWidth={VisionMaxWidth};jpegQ=80;detail=high", "schema:transcript/pages[]:string,legibility:number,strict");

    /// <summary>The render parameters as VALUES, captured on every transcription call record (decision 6:
    /// images by hash + parameters). Folding them into the promptHash proves they didn't change; recording them
    /// verbatim is what lets a replay actually re-render the same inputs without reading this source file.</summary>
    internal static readonly string RenderParamsJson =
        FormattableString.Invariant($"{{\"dpi\":{Dpi},\"maxPages\":{MaxPages},\"maxWidth\":{VisionMaxWidth},\"jpegQuality\":80,\"detail\":\"high\"}}");

    public async Task<Transcript> TranscribeAsync(IncomingDocument doc, CancellationToken ct = default)
    {
        // The source PDF's content hash, captured on the call record so it joins to the ledger's Received.sha256
        // (and survives the inbox copy's deletion). Best-effort: an unreadable file will fail loudly at Render below.
        string? sourceSha = null;
        try { sourceSha = ModelCallElision.Sha256Hex(await File.ReadAllBytesAsync(doc.SourcePath, ct)); }
        catch (IOException) { /* Render throws the real error */ }
        catch (UnauthorizedAccessException) { /* ditto */ }

        var pages = PdfRenderer.Render(doc.SourcePath, Dpi, MaxPages);
        try
        {
            var barcodes = BarcodeScanner.Scan(pages);

            var content = new List<object>
            {
                new { type = "text", text = string.Format(UserPromptTemplate, pages.Count) }
            };
            foreach (var page in pages)
            {
                var b64 = Convert.ToBase64String(PdfRenderer.Jpeg(page, quality: 80, maxWidth: VisionMaxWidth));
                content.Add(new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{b64}", detail = "high" } });
            }

            var messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = content },
            };

            var schema = new Dictionary<string, object?>
            {
                ["type"] = "json_schema",
                ["json_schema"] = new Dictionary<string, object?>
                {
                    ["name"] = "transcript",
                    ["strict"] = true,
                    ["schema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["pages"] = new Dictionary<string, object?>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                            },
                            ["legibility"] = new Dictionary<string, object?> { ["type"] = "number" },
                        },
                        ["required"] = new[] { "pages", "legibility" },
                    },
                },
            };

            // CompleteWithMetaAsync, not CompleteAsync: the meta call is what surfaces the served model id and the
            // system_fingerprint. Transcription previously used the non-meta call, so a Transcribed event carried no
            // model identity at all and a deployment swap was invisible in the audit trail.
            var (reply, served, fingerprint) = await ai.CompleteWithMetaAsync(messages, schema, 8000, opt.Deployment, ct, null, ReasoningEffort.Extraction,
                new CallContext(doc.DocumentId, ModelCallSite.Transcription, PromptHash: TemplateHash,
                    RenderParamsJson: RenderParamsJson, SourcePdfSha256: sourceSha));
            using var json = ParseJson(reply);
            var root = json.RootElement;
            var pageMds = new List<string>();
            if (root.TryGetProperty("pages", out var pg) && pg.ValueKind == JsonValueKind.Array)
                foreach (var el in pg.EnumerateArray())
                    pageMds.Add(el.ValueKind == JsonValueKind.String ? el.GetString()! : "");
            // 0.5 is a FALLBACK, not a reading: it is deliberately below the 0.60 floor so an unreadable response still
            // routes to review. Record that we substituted it, so a post-migration flood of low_quality is attributable
            // to a renamed/restructured field rather than to the scans suddenly getting worse. See ReasonCodes.ModelFieldUnparsed.
            var legParsed = root.TryGetProperty("legibility", out var l) && l.ValueKind == JsonValueKind.Number;
            var leg = legParsed ? Math.Clamp(l.GetDouble(), 0, 1) : 0.5;
            string[]? unparsed = null;
            if (!legParsed)
            {
                unparsed = ["legibility"];
                log.LogWarning(
                    "Transcription response field 'legibility' {Problem} (kind={Kind}) — substituted {Fallback}; the document will route to review as unparsed, NOT as low quality.",
                    root.TryGetProperty("legibility", out _) ? "was not a number" : "was absent",
                    root.TryGetProperty("legibility", out var lk) ? lk.ValueKind.ToString() : "(none)", 0.5);
            }

            // Per-page markdown is the slice primitive ONLY when the model returned exactly one entry per rendered page;
            // on any count mismatch keep the joined blob (extraction is unaffected) but expose no PageMarkdown, so a later
            // split/guard re-transcribes rather than slicing a misaligned array (build-auth: fail toward the safe re-run).
            var perPage = pageMds.Count == pages.Count ? (IReadOnlyList<string>)pageMds : null;
            var md = string.Join("\n\n", pageMds);

            return new Transcript(md, pages.Count, leg, barcodes, perPage, opt.Deployment, served, fingerprint, TemplateHash, unparsed,
                ModelCapabilities.EffortSentFor(opt.Deployment, ReasoningEffort.Extraction));
        }
        finally
        {
            foreach (var p in pages) p.Dispose();
        }
    }

    private static JsonDocument ParseJson(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.EndsWith("```", StringComparison.Ordinal)) s = s[..^3];
        }
        return JsonDocument.Parse(s);
    }
}
