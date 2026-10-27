using System.Globalization;
using System.IO;
using System.Text.Json;
using Adelaide.Core;
using Microsoft.Extensions.Logging;

namespace Adelaide.Ai;

/// <summary>
/// gpt-4o extractor: (1) classify the document into one SharePoint content type, then (2) extract that type's
/// fields plus patient-identity fields. Choice columns are constrained to the <see cref="Vocab"/> allowed values
/// (invalid choices are dropped), dates normalised to ISO, and a checksum-valid barcode KVNR overrides text.
/// Both steps use JSON-schema structured output so the model returns well-formed, schema-valid JSON.
/// </summary>
// A9: ILogger is now REQUIRED (was optional/nullable, which silently no-op'd the diagnostics below). DI resolves by
// type, and the only construction site is AddTransient<IExtractor, AzureOpenAiExtractor>, so the reorder is safe.
public sealed class AzureOpenAiExtractor(OpenAiClient ai, AiOptions opt, ILogger<AzureOpenAiExtractor> log, DocIntelligencePdf? di = null) : IExtractor
{
    public async Task<ExtractedData> ExtractAsync(DocumentUnit unit, CancellationToken ct = default)
    {
        var md = Truncate(unit.Transcript.Markdown, 12000);
        var seed = ExtractionSeed.FromContentSha(unit.ContentSha); // reproducible per document (from the dedup content hash); null → unseeded
        // GATE (decoder-upgrade safety): trust a barcode KVNR ONLY when it comes from a structurally-valid KBV
        // payload — a Muster 12 BFB or a BMP. A decoded barcode is not itself evidence of identity: the upgraded
        // engine now reads franking / routing DataMatrix codes (e.g. on Krankenkasse letters) that never decoded
        // before, and a coincidentally checksum-valid KVNR-shaped token inside one must never become the matcher's
        // primary key (that would file the document under the wrong patient). See BarcodeIdentity.StructuralKvnr.
        var barcodeKvnr = unit.Transcript.Barcodes
            .Select(b => BarcodeIdentity.StructuralKvnr(b.RawText))
            .FirstOrDefault(k => k is not null);
        // The Muster 12 BFB barcode is the prescription exactly as the practice software encoded it — deterministic
        // and authoritative over any vision reading. When present it supplies Erst/Folge, Zeitraum and Leistungen.
        var bfb = unit.Transcript.Barcodes
            .Select(b => BfbBarcode.Parse(b.RawText))
            .FirstOrDefault(x => x is not null);

        // DECISION 1: which lane? A business document must never enter the medical path — it has no patient, no
        // catalog type and no field schema — so the medical classification and extraction calls are skipped entirely.
        // On the 2026-08-05 ledger every one of the 358 business documents was fully extracted BEFORE the reviewer
        // ticked the business box (extracted-first: 358, reviewed-first: 0), so this also removes two model calls
        // from 23% of the corpus. The reviewer's checkbox now confirms or corrects this proposal instead of being
        // the only source of the answer.
        var (domain, domainConf, domainConfUnparsed) = await ClassifyDomainAsync(md, seed, unit.UnitId, ct);
        if (DocumentDomain.IsBusiness(domain))
        {
            var businessFields = new List<ExtractedField> { new("Domain", DocumentDomain.Business, domainConf, "vision") };
            return new ExtractedData(businessFields, seed, null, opt.Deployment, null, TemplateHash,
                domainConfUnparsed ? ["domain.confidence"] : null, null,
                ModelCapabilities.EffortSentFor(opt.MiniDeployment, ReasoningEffort.Classification));
        }

        // DECISION 2: the medical type, over DocumentCatalog's vocabulary — reached only on the medical lane.
        var (contentType, classifyConf, classifySource, originalType, originalConf, unparsedFields) = await ClassifyAsync(md, seed, unit.UnitId, ct);
        if (domainConfUnparsed)
            unparsedFields = (unparsedFields ?? []).Append("domain.confidence").ToList();
        var def = DocumentCatalog.Find(contentType);

        // A Muster 12 BFB barcode is near-definitive that the document is a Verordnung. If the classifier disagreed,
        // its deterministic data is intentionally NOT applied (gated below) — surface that so a wrong classification
        // that hid an authoritative barcode doesn't pass silently.
        if (bfb is not null && !CheckboxTypes.Contains(contentType))
            log.LogWarning("Muster 12 BFB barcode present but document classified as {ContentType} — barcode data not applied; verify classification.", contentType);

        // Checkbox/Leistung state for Verordnungen: prefer the deterministic BFB barcode; else read DI selection
        // marks (barcode missing/unreadable). The DI layout is reused below to set Erst/Folge deterministically.
        var diLayout = bfb is null ? await ReadLayoutAsync(unit, contentType, ct) : null;
        var checkboxEvidence = bfb is not null && CheckboxTypes.Contains(contentType)
            ? BfbEvidence(bfb)
            : diLayout?.ToEvidence() is { Length: > 0 } diEv ? diEv : null;
        var ex = await ExtractFieldsAsync(md, contentType, def, barcodeKvnr, checkboxEvidence, seed, unit.UnitId, ct);

        var fields = new List<ExtractedField>
        {
            new("Domain", DocumentDomain.Medical, domainConf, "vision"),   // the proposal the reviewer confirms or corrects
            new("ContentType", contentType, classifyConf, classifySource),
        };
        if (originalType is not null)
            fields.Add(new("OriginalContentType", originalType, originalConf, "vision")); // the overturned first classification — visible to the reviewer and the audit

        if (barcodeKvnr is not null)
            fields.Add(new("KVNR", barcodeKvnr, 0.99, "barcode"));
        else if (NonEmpty(ex.Kvnr))
        {
            var raw = ex.Kvnr!.Trim().ToUpperInvariant();
            if (Kvnr.IsValid(raw))
                fields.Add(new("KVNR", raw, 0.95, "vision"));
            else if (Kvnr.Repair(raw) is { } repaired) // OCR read the leading letter as a look-alike digit (e.g. AOK 'O' → '0')
                fields.Add(new("KVNR", repaired, 0.90, "vision/repaired"));
            else
                fields.Add(new("KVNR", raw, 0.50, "vision"));
        }

        AddIf(fields, "LastName", ex.LastName, ex.Overall);
        AddIf(fields, "FirstName", ex.FirstName, ex.Overall);
        AddIf(fields, "BirthDate", NormDate(ex.BirthDate), ex.Overall);
        AddIf(fields, "DocumentDate", NormDate(ex.DocumentDate), ex.Overall);

        foreach (var (column, value, confidence) in ex.Fields)
        {
            if (!NonEmpty(value) || IdentityCols.Contains(column)) continue; // identity handled above
            // The response schema cannot enum column names, so the model can hallucinate one (a German label, a
            // near-miss) — drop anything outside the type's schema at the source: such a name would ride the review
            // sidecar into the client's submission and, post-C6, field NAMES are ledger inputs.
            //
            // TWO gates, because they cover different cases. For a CATALOG type the type's own schema is the tighter
            // test. For the bare fallback type def is null and there IS no schema to check against — and that is not
            // a rare path: IntakePipeline routes every fallback-typed document to review. So fall back to the
            // cross-type closed vocabulary. It must be enforced HERE, at the source, and not only at the review
            // diff: ProcessAsync's capture block writes data.Fields straight into corpus_fields BEFORE any review,
            // into a table the retention purge deliberately exempts, so a review-time filter never reaches that copy
            // — and would additionally leave those rows unstampable, because the review outcome path stamps by name.
            if (def is not null)
            {
                if (!def.Fields.Any(d => string.Equals(d.Column, column, StringComparison.OrdinalIgnoreCase))) continue;
            }
            else if (!FieldVocabulary.IsSafe(column)) continue;

            var val = value!.Trim();
            var src = "vision";
            var allowed = Vocab.For(column);
            if (allowed is not null && !allowed.Contains(val, StringComparer.OrdinalIgnoreCase)) continue; // invalid choice → drop
            if (def?.Fields.FirstOrDefault(d => string.Equals(d.Column, column, StringComparison.OrdinalIgnoreCase))?.Kind == FieldKind.Date)
            {
                // Unparseable date → DROP the field (NormDate returns null): a required date then routes to review via
                // missing_required, instead of a junk or silently-swapped value passing through to SharePoint.
                if (NormDate(val) is not { } isoDate) continue;
                val = isoDate;
            }
            if (string.Equals(column, "ApprovalNumber", StringComparison.OrdinalIgnoreCase) && Approval.Repair(val) is var rep && rep != val)
            {
                val = rep; src = "vision/repaired"; // OCR read a leading 'G' as the look-alike '6' (AOK Genehmigungsnummer)
            }
            fields.Add(new(column, val, Math.Clamp(confidence, 0, 1), src));
        }

        if (bfb is not null && CheckboxTypes.Contains(contentType))
            ApplyBfb(fields, bfb); // deterministic barcode values supersede the vision reading
        else if (diLayout is not null)
            ApplyDiOrder(fields, diLayout); // DI Erst/Folge: trust when clean & agreeing; flag the rest for review
        // checkboxEvidence rides out with the result so it can be RETAINED: it is a model input (DI selection marks or
        // the decoded BFB payload) and was previously discarded, leaving a DI/decoder upgrade nothing to measure.
        // The effort stamp describes THE EXTRACTION CALL this record's fields came from — strong deployment,
        // Extraction op — not the mini classify call it previously (wrongly) named: harmless while both resolve to
        // null on gpt-4o, a false provenance claim the moment a reasoning deployment lands. The per-call
        // model_calls rows are authoritative either way; this stage stamp now agrees with them.
        return new ExtractedData(fields, seed, ex.Fingerprint, opt.Deployment, ex.ServedModel, TemplateHash, unparsedFields, checkboxEvidence,
            ModelCapabilities.EffortSentFor(opt.Deployment, ReasoningEffort.Extraction));
    }

    /// <summary>Build a "[X] label" evidence list from the decoded BFB barcode (Erst/Folge + ticked Leistungen),
    /// so the field extractor gets the prescription's measures as ground truth — same shape as the DI fallback.</summary>
    private static string BfbEvidence(BfbVerordnung b)
    {
        var lines = new List<string>();
        if (b.PrescriptionOrder is not null) lines.Add($"[X] {b.PrescriptionOrder}");
        foreach (var l in b.Leistungen) lines.Add($"[X] {l}");
        return string.Join("\n", lines);
    }

    /// <summary>Overwrite the FORM-METADATA fields the barcode decodes deterministically (Erst/Folge, Zeitraum) at
    /// source "barcode", and add a non-schema "Leistungen" summary (ignored by the sink, shown to reviewers).
    /// Deliberately does NOT touch identity (LastName/FirstName/BirthDate): the barcode is what the practice keyed
    /// into the form, not the patient registry — letting it silently change the name+DOB the matcher falls back on
    /// could match the wrong patient. The KVNR (the matcher's primary key) still comes from the barcode separately.</summary>
    private static void ApplyBfb(List<ExtractedField> fields, BfbVerordnung b)
    {
        void Set(string name, string? value, double conf = 0.99)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            fields.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            fields.Add(new ExtractedField(name, value.Trim(), conf, "barcode"));
        }
        Set("PrescriptionOrder", b.PrescriptionOrder);
        Set("ValidFrom", b.ValidFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Set("ValidUntil", b.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (b.Leistungen.Count > 0) Set("Leistungen", b.LeistungenSummary);
    }

    private static void SetField(List<ExtractedField> fields, string name, string value, double conf, string source)
    {
        fields.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        fields.Add(new ExtractedField(name, value, conf, source));
    }

    // Informative only — the gate routes Erst/Folge to review on its SOURCE, not this number (the model's ~0.9
    // self-report on a checkbox is not evidence; cross-source agreement is).
    private const double CheckboxReviewConf = 0.5;

    /// <summary>Resolve Erst/Folge from DI selection marks when there's no barcode. Trust DI when it reads the boxes
    /// cleanly AND the model agreed; if the model (handed the same DI evidence) disagreed, or DI couldn't read the
    /// boxes, mark the field with a source the gate routes to review — rather than auto-filing on a guess.</summary>
    private static void ApplyDiOrder(List<ExtractedField> fields, LayoutResult diLayout)
    {
        var vision = fields.FirstOrDefault(f => string.Equals(f.Name, "PrescriptionOrder", StringComparison.OrdinalIgnoreCase) && f.Source == "vision")?.Value;
        var di = diLayout.PrescriptionOrder();
        if (di is not null)
        {
            var agree = vision is null || string.Equals(vision, di, StringComparison.OrdinalIgnoreCase);
            SetField(fields, "PrescriptionOrder", di, agree ? 0.95 : CheckboxReviewConf, agree ? "di" : "di/conflict");
        }
        else if (vision is not null)
        {
            SetField(fields, "PrescriptionOrder", vision, CheckboxReviewConf, "vision/unverified");
        }
    }

    /// <summary>For a checkbox-heavy type, analyse the form pages with DI selection marks. Null when DI is
    /// unavailable, the type isn't checkbox-heavy, or the source isn't on disk — the extractor then relies on the
    /// vision transcript. The result feeds both the evidence list (for the model) and the deterministic Erst/Folge.</summary>
    private async Task<LayoutResult?> ReadLayoutAsync(DocumentUnit unit, string contentType, CancellationToken ct)
    {
        if (di is null || !CheckboxTypes.Contains(contentType) || !NonEmpty(unit.SourcePath) || !File.Exists(unit.SourcePath))
            return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(unit.SourcePath!, ct);
            return await di.AnalyzeLayoutAsync(bytes, CheckboxPageRange, new CallContext(unit.UnitId, ModelCallSite.DiLayout), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A9: previously a silent swallow. Surface the cause (TYPE only — never the message, which could embed the
            // source path) so a checkbox doc that lost its DI selection-mark evidence is diagnosable. Behaviour unchanged:
            // still best-effort falls back to the vision reading.
            log.LogWarning("DI layout read failed for {ContentType}; falling back to vision. {ExType} [{ExChain}]", contentType, ex.GetType().Name, Redact.Chain(ex));
            return null;
        }
    }

    private async Task<(string ContentType, double Conf, string Source, string? OriginalType, double OriginalConf, IReadOnlyList<string>? Unparsed)> ClassifyAsync(string md, int? seed, string docId, CancellationToken ct)
    {
        // Any response field we could not read is collected by NAME (PHI-safe) and surfaced on ExtractedData, so the
        // gate can flag ReasonCodes.ModelFieldUnparsed instead of the document looking merely low-confidence.
        var unparsed = new List<string>();
        void Note(string f) { if (!unparsed.Contains(f)) unparsed.Add(f); }
        IReadOnlyList<string>? Un() => unparsed.Count > 0 ? unparsed : null;

        var (type, conf, confUnparsed) = await ClassifyWithAsync(md, opt.MiniDeployment, seed, docId, ModelCallSite.Classify, ct);   // cheap first pass (gpt-4o-mini)
        if (confUnparsed) Note("classify.confidence");
        if (conf < ClassifyEscalateBelow || Confusable.Contains(type) || string.Equals(type, DocumentCatalog.Fallback, StringComparison.OrdinalIgnoreCase))
        {
            // Its own call SITE: which lane answered was previously unrecoverable from any record (§7.4 item 7).
            (type, conf, confUnparsed) = await ClassifyWithAsync(md, opt.Deployment, seed, docId, ModelCallSite.ClassifyEscalation, ct); // escalate uncertain/fallback/confusable to the strong model
            if (confUnparsed) Note("classify.confidence");
        }

        // Self-verification for confusable types: a confidence threshold cannot catch a confidently-wrong
        // classification (a Pflegesachleistung-Kostenzusage once auto-filed as Genehmigung at 0.95). Checking the
        // type's DEFINING criterion can. On disagreement, take the corrected type but route to review (the gate keys
        // on the source AND the capped confidence) — the self-check re-routes; it never auto-files its own answer.
        // The self-check is an augmentation: its own failure degrades to review, never to a processing failure.
        if (Confusable.Contains(type) && DefiningCriteria.TryGetValue(type, out var criterion))
        {
            try
            {
                var v = await VerifySelfAsync(md, type, criterion, seed, docId, ct);
                if (v.ConfidenceUnparsed) Note("selfcheck.confidence");
                if (v.Confirmed) return (type, conf, "vision/verified", null, 0, Un());
                var corrected = DocumentCatalog.Find(v.CorrectedType) is not null ? v.CorrectedType : DocumentCatalog.Fallback;
                return (corrected, Math.Min(Math.Clamp(v.Confidence, 0, 1), SelfCheckReviewCap), "vision/self-corrected", type, conf, Un());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A9: previously a silent swallow. Record WHY the self-check degraded (TYPE only) so a doc routed to
                // review as "vision/unverified" isn't an unexplained downgrade. Behaviour unchanged: still degrades to review.
                log.LogWarning("Classification self-check failed for {ContentType}; degrading to review (vision/unverified). {ExType} [{ExChain}]", type, ex.GetType().Name, Redact.Chain(ex));
                return (type, Math.Min(conf, SelfCheckReviewCap), "vision/unverified", null, 0, Un());
            }
        }
        return (type, conf, "vision", null, 0, Un());
    }

    private sealed record SelfCheck(bool Confirmed, string CorrectedType, double Confidence, bool ConfidenceUnparsed = false);

    /// <summary>Asks the strong model whether the document actually satisfies the classified type's defining
    /// criterion — the question that separates the confusable types, which raw classification confidence misses.</summary>
    private async Task<SelfCheck> VerifySelfAsync(string md, string type, string criterion, int? seed, string docId, CancellationToken ct)
    {
        var enumVals = DocumentCatalog.Types.Select(t => t.ContentType).Append(DocumentCatalog.Fallback).ToArray();
        var typeList = string.Join("\n", DocumentCatalog.Types.Select(t =>
            $"- {t.ContentType}{(string.IsNullOrWhiteSpace(t.Description) ? "" : $" — {t.Description}")}"));

        var schema = Schema("selfcheck", new Dictionary<string, object?>
        {
            ["bestaetigt"] = new Dictionary<string, object?> { ["type"] = "boolean" },
            ["korrigierterTyp"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = enumVals },
            ["confidence"] = new Dictionary<string, object?> { ["type"] = "number" },
        }, "bestaetigt", "korrigierterTyp", "confidence");

        var messages = new object[]
        {
            new { role = "system", content = string.Format(SelfCheckSystemPromptTemplate, type, criterion) },
            new { role = "user", content = $"Geprüfter Typ: {type}\n\nMögliche Typen:\n{typeList}\n\nDokumenttext (Markdown):\n{md}" },
        };

        using var doc = ParseJson((await ai.CompleteWithMetaAsync(messages, schema, 200, opt.Deployment, ct, seed, ReasoningEffort.Classification,
            new CallContext(docId, ModelCallSite.SelfCheck, PromptHash: TemplateHash))).Content);
        var r = doc.RootElement;
        var confirmed = r.TryGetProperty("bestaetigt", out var b) && b.ValueKind == JsonValueKind.True;
        var corrected = r.TryGetProperty("korrigierterTyp", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : type;
        var confParsed = r.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number;
        if (!confParsed) LogUnparsed("selfcheck.confidence", r);
        return new SelfCheck(confirmed, corrected, confParsed ? c.GetDouble() : 0.5, !confParsed);
    }

    /// <summary>Decision 1 of 2: which lane is this document in? Runs on the cheap model, before any medical
    /// classification, so a business document never enters the medical extraction path at all (and costs 2 fewer
    /// model calls). Its own enum and schema — see <see cref="DocumentDomain"/> for why this is not a content type.</summary>
    private async Task<(string Domain, double Conf, bool ConfidenceUnparsed)> ClassifyDomainAsync(string md, int? seed, string docId, CancellationToken ct)
    {
        var schema = Schema("domain", new Dictionary<string, object?>
        {
            ["domain"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = DocumentDomain.All },
            ["confidence"] = new Dictionary<string, object?> { ["type"] = "number" },
        }, "domain", "confidence");

        var messages = new object[]
        {
            new { role = "system", content = DomainSystemPrompt },
            new { role = "user", content = $"Dokumenttext (Markdown):\n{md}" },   // already truncated by the caller
        };

        using var doc = ParseJson((await ai.CompleteWithMetaAsync(messages, schema, 200, opt.MiniDeployment, ct, seed, ReasoningEffort.Classification,
            new CallContext(docId, ModelCallSite.Domain, PromptHash: TemplateHash))).Content);
        var domain = doc.RootElement.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var confParsed = doc.RootElement.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number;
        if (!confParsed) LogUnparsed("domain.confidence", doc.RootElement);

        // An unrecognised domain must not silently become medical: fall back to the medical lane (which gates on
        // everything else) but treat the field as unparsed so the gate says so.
        if (!DocumentDomain.IsKnown(domain))
        {
            LogUnparsed("domain", doc.RootElement);
            return (DocumentDomain.Medical, 0.5, true);
        }
        return (domain!, Math.Clamp(confParsed ? c.GetDouble() : 0.5, 0, 1), !confParsed);
    }

    private async Task<(string ContentType, double Conf, bool ConfidenceUnparsed)> ClassifyWithAsync(string md, string model, int? seed, string docId, ModelCallSite site, CancellationToken ct)
    {
        var typeList = string.Join("\n", DocumentCatalog.Types.Select(t =>
        {
            var aliases = t.Aliases is { Length: > 0 } a ? $" (auch: {string.Join(", ", a)})" : "";
            var desc = !string.IsNullOrWhiteSpace(t.Description) ? $" — {t.Description}" : "";
            return $"- {t.ContentType}{aliases}{desc}";
        }));
        var enumVals = DocumentCatalog.Types.Select(t => t.ContentType).Append(DocumentCatalog.Fallback).ToArray();

        var schema = Schema("classification", new Dictionary<string, object?>
        {
            ["contentType"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = enumVals },
            ["confidence"] = new Dictionary<string, object?> { ["type"] = "number" },
        }, "contentType", "confidence");

        var messages = new object[]
        {
            new { role = "system", content = ClassifySystemPrompt },
            new { role = "user", content = $"Mögliche Typen:\n{typeList}\n\nDokumenttext (Markdown):\n{md}" },
        };

        using var doc = ParseJson((await ai.CompleteWithMetaAsync(messages, schema, 200, model, ct, seed, ReasoningEffort.Classification,
            new CallContext(docId, site, PromptHash: TemplateHash))).Content);
        var contentType = doc.RootElement.GetProperty("contentType").GetString() ?? DocumentCatalog.Fallback;
        var confParsed = doc.RootElement.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number;
        if (!confParsed) LogUnparsed("classify.confidence", doc.RootElement);
        return (contentType, Math.Clamp(confParsed ? c.GetDouble() : 0.5, 0, 1), !confParsed);
    }

    /// <summary>One place to say "the model did not give us this number, so the value below is a fallback". The 0.5
    /// fallback sits under every gate, so the document routes to review either way — this only separates "unparseable
    /// response" from "genuinely low confidence", which is the distinction a model migration turns on.</summary>
    private void LogUnparsed(string field, JsonElement root)
    {
        var leaf = field.Split('.').Last();
        log.LogWarning(
            "Model response field '{Field}' {Problem} (kind={Kind}) — substituted 0.5, which is below every gate; the document routes to review as unparsed, NOT as low confidence.",
            field,
            root.TryGetProperty(leaf, out _) ? "was not a number" : "was absent",
            root.TryGetProperty(leaf, out var k) ? k.ValueKind.ToString() : "(none)");
    }

    private async Task<ExtractResult> ExtractFieldsAsync(string md, string contentType, DocTypeDef? def, string? barcodeKvnr, string? checkboxEvidence, int? seed, string docId, CancellationToken ct)
    {
        var cols = (def?.Fields ?? Array.Empty<DocField>())
            .Where(f => f.Kind != FieldKind.Lookup && !IdentityCols.Contains(f.Column)).ToList();
        var colText = cols.Count > 0
            ? string.Join("\n", cols.Select(f =>
            {
                var allowed = Vocab.For(f.Column);
                var kind = f.Kind switch { FieldKind.Date => "Datum yyyy-MM-dd", FieldKind.Bool => "true/false", FieldKind.Choice => "Auswahl", _ => "Text" };
                var allow = allowed is not null ? $"; erlaubte Werte: {string.Join(" | ", allowed)}" : "";
                var syn = FieldSynonyms.TryGetValue(f.Column, out var s) ? $"; im Dokument oft genannt: {string.Join(" / ", s)}" : "";
                return $"- {f.Column} ({f.Label}) [{kind}{allow}{syn}]";
            }))
            : "(keine typ-spezifischen Felder)";

        var schema = Schema("extraction", new Dictionary<string, object?>
        {
            ["kvnr"] = NullableStr(),
            ["lastName"] = NullableStr(),
            ["firstName"] = NullableStr(),
            ["birthDate"] = NullableStr(),
            ["documentDate"] = NullableStr(),
            ["fields"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["column"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["value"] = NullableStr(),
                        ["confidence"] = new Dictionary<string, object?> { ["type"] = "number" },
                    },
                    ["required"] = new[] { "column", "value", "confidence" },
                },
            },
            ["overallConfidence"] = new Dictionary<string, object?> { ["type"] = "number" },
        }, "kvnr", "lastName", "firstName", "birthDate", "documentDate", "fields", "overallConfidence");

        var hint = barcodeKvnr is not null
            ? $"\nHinweis: Aus einem Barcode wurde die Versichertennummer {barcodeKvnr} dekodiert — verwende diese als kvnr."
            : "";
        var checkboxBlock = string.IsNullOrWhiteSpace(checkboxEvidence) ? "" :
            "\n\n=== VERIFIZIERTE ANKREUZ-ZUSTÄNDE (von einem dedizierten Detektor erkannt; GILT ALS WAHRHEIT) ===\n" + checkboxEvidence +
            "\nREGEL: Für jedes aus Ankreuzkästchen abgeleitete Feld (z. B. Erst-/Folgeverordnung, Kompressionsbehandlung samt Seite rechts/links/beidseits, Art der Leistung) richte dich AUSSCHLIESSLICH nach dieser Liste — [X] = angekreuzt, [ ] = nicht angekreuzt — und NICHT nach dem Fließtext. Labels können abgekürzt oder zeilenweise getrennt sein (z. B. \"Erstver-\" steht für Erstverordnung). Werte, die du so aus einem [X] ableitest, gib mit hoher confidence (≥ 0.9) an. Ist das relevante Kästchen nicht aufgeführt oder sein Zustand unklar, gib für das Feld null zurück.\n";
        var messages = new object[]
        {
            new { role = "system", content = ExtractSystemPrompt },
            new { role = "user", content = $"Dokumenttyp: {contentType}\n\nTyp-spezifische Felder (im 'fields'-Array zurückgeben, je Eintrag column/value/confidence):\n{colText}\n\nZusätzlich immer: kvnr (10-stellige Versichertennummer), lastName, firstName, birthDate, documentDate (Ausstellungs-/Erstellungsdatum des Dokuments — z. B. Arzt-Unterschriftsdatum, Briefdatum —, NICHT ein Behandlungs-/Verordnungs- oder Gültigkeitszeitraum).{hint}{checkboxBlock}\n\nDokumenttext (Markdown):\n{md}" },
        };

        var (raw, served, fingerprint) = await ai.CompleteWithMetaAsync(messages, schema, 900, opt.Deployment, ct, seed, ReasoningEffort.Extraction,
            new CallContext(docId, ModelCallSite.Extraction, PromptHash: TemplateHash));
        using var doc = ParseJson(raw);
        var r = doc.RootElement;
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var overall = r.TryGetProperty("overallConfidence", out var oc) && oc.ValueKind == JsonValueKind.Number ? Math.Clamp(oc.GetDouble(), 0, 1) : 0.7;

        var list = new List<(string Column, string? Value, double Confidence)>();
        if (r.TryGetProperty("fields", out var fa) && fa.ValueKind == JsonValueKind.Array)
            foreach (var it in fa.EnumerateArray())
            {
                var col = it.TryGetProperty("column", out var cc) ? cc.GetString() : null;
                if (string.IsNullOrWhiteSpace(col)) continue;
                var val = it.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.String ? vv.GetString() : null;
                var cf = it.TryGetProperty("confidence", out var ce) && ce.ValueKind == JsonValueKind.Number ? ce.GetDouble() : overall;
                list.Add((col!, val, cf));
            }
        return new ExtractResult(S("kvnr"), S("lastName"), S("firstName"), S("birthDate"), S("documentDate"), list, overall, fingerprint, served);
    }

    private sealed record ExtractResult(
        string? Kvnr, string? LastName, string? FirstName, string? BirthDate, string? DocumentDate,
        List<(string Column, string? Value, double Confidence)> Fields, double Overall, string? Fingerprint = null,
        string? ServedModel = null);

    // ---- prompt templates ----
    // Named (rather than inline at the call site) so they can be content-hashed for provenance — see PromptHash and
    // TemplateHash below. Keep the call sites referencing these consts: an inline copy would drift and the stamped
    // hash would silently stop tracking the prompt that actually ran.

    private const string ClassifySystemPrompt = "Du klassifizierst eingescannte Dokumente eines deutschen ambulanten Pflegedienstes in genau EINEN Inhaltstyp aus der unten angegebenen Liste (jeder Typ mit Kurzbeschreibung). Wähle anhand von Absender/Briefkopf und Schlüsselwörtern den am besten passenden Typ; achte besonders auf ähnliche Typen (z. B. Verordnung vs. Genehmigung vs. Bescheid). Wenn kein Typ passt oder du unsicher bist, wähle \"Document\". Antworte nur mit JSON.";

    // {0} = classified type, {1} = that type's defining criterion.
    private const string SelfCheckSystemPromptTemplate = "Du verifizierst die Klassifizierung eines gescannten Dokuments eines deutschen Pflegedienstes. Ein Dokument ist NUR dann \"{0}\", wenn gilt: {1} Prüfe streng anhand des Dokumenttexts, ob diese Definition erfüllt ist. bestaetigt=true nur bei klarer Erfüllung; setze korrigierterTyp auf den insgesamt am besten passenden Typ (bei bestaetigt=true also \"{0}\"; sonst den korrekten Typ, oder \"Document\" wenn keiner passt). Antworte nur mit JSON.";

    // Decision 1 of 2 (see DocumentDomain). Deliberately describes the BUSINESS lane by its functional areas — the
    // Zielbereich labels reviewers already assign (HR / Finance) — rather than by document type, because the whole
    // point is that business documents have no place in the medical type vocabulary.
    private const string DomainSystemPrompt = "Du ordnest ein gescanntes Dokument eines deutschen Pflegedienstes GENAU EINEM Bereich zu. 'Medical' = patientenbezogene Versorgungsdokumentation: ärztliche Verordnungen, Genehmigungen und Bescheide der Kranken-/Pflegekassen zu einem Patienten, Pflegeverträge mit Patienten, Leistungsnachweise, Beratungsbesuche, Medikamentenpläne, Arztbriefe, Gutachten, Vorsorgevollmachten und Patientenverfügungen. 'Business' = betriebliche Unterlagen OHNE Patientenbezug: Eingangs- und Ausgangsrechnungen, Mahnungen, Kontoauszüge, Steuer- und Buchhaltungsunterlagen, Verträge mit Lieferanten oder Dienstleistern, Versicherungs- und Behördenpost zum Unternehmen selbst, sowie Personalangelegenheiten (Bewerbungen, Arbeitsverträge, Lohnabrechnungen, Arbeitsunfähigkeitsbescheinigungen von MITARBEITERN, Fortbildungsnachweise). Entscheidend ist, ob das Dokument einen PATIENTEN der Pflege betrifft oder den BETRIEB. Ein Schreiben einer Krankenkasse ist nur dann 'Medical', wenn es sich auf einen konkreten Patienten und dessen Versorgung bezieht; Abrechnungs- und Vertragspost zum Unternehmen ist 'Business'. Antworte nur mit JSON.";

    private const string ExtractSystemPrompt ="Du extrahierst strukturierte Felder aus einem deutschen Pflegedienst-Dokument. Verwende ausschließlich im Text vorhandene Informationen; fehlt ein Feld, gib null zurück. Felder können im Dokument anders benannt sein — z. B. 'Verordnungszeitraum von TT.MM.JJJJ bis TT.MM.JJJJ' liefert ValidFrom (von) UND ValidUntil (bis). Datumsangaben als ISO yyyy-MM-dd. Alle numerischen Datumsangaben im Dokument sind deutsche Notation TT.MM.JJJJ (Tag.Monat.Jahr): '02.07.2026' = 2. Juli 2026, niemals der 7. Februar — konvertiere unter dieser Lesart nach ISO yyyy-MM-dd. Für Auswahlfelder gib EXAKT einen der erlaubten Werte oder null zurück. Beruht ein Auswahlfeld auf Ankreuzkästchen (z. B. Erstverordnung/Folgeverordnung): Gib den Wert NUR zurück, wenn das zugehörige Kästchen eindeutig als angekreuzt markiert ist (z. B. [X]); ist keines eindeutig markiert, gib null zurück — NICHT raten oder den Standard wählen. Liegen verifizierte Ankreuz-Zustände bei, haben diese Vorrang vor dem Fließtext. Antworte nur mit JSON.";

    // ---- helpers ----
    private const double ClassifyEscalateBelow = 0.80; // classify on mini; escalate to the strong model below this confidence

    // Checkbox-heavy types whose tick state is read from DI selection marks rather than inferred from the transcript.
    private static readonly HashSet<string> CheckboxTypes = new(StringComparer.OrdinalIgnoreCase) { "Verordnung" };
    private const string CheckboxPageRange = "1-2"; // the Muster 12 face — bounds DI billing to the form pages

    // Easily-confused types (a Genehmigung quotes the Verordnung it approves) → always confirm on the strong model.
    private static readonly HashSet<string> Confusable =
        new(StringComparer.OrdinalIgnoreCase) { "Verordnung", "Genehmigung", "Bescheid", "Gutachten" };

    // A self-check disagreement caps confidence below the 0.80 auto-file gate → the document goes to review.
    private const double SelfCheckReviewCap = 0.75;

    // The single criterion that DEFINES each confusable type — what the self-check verifies.
    private static readonly Dictionary<string, string> DefiningCriteria = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Verordnung"] = "Es ist das ärztliche Verordnungsformular selbst (z. B. Muster 12, häusliche Krankenpflege), von Arzt/Praxis ausgestellt — nicht ein Schreiben, das sich nur auf eine Verordnung bezieht.",
        ["Genehmigung"] = "Es ist ein Schreiben einer Kranken-/Pflegekasse, das eine KONKRETE ärztliche Verordnung genehmigt, abweichend genehmigt oder ablehnt. Allgemeine Kostenzusagen ohne Bezug zu einer ärztlichen Verordnung (z. B. Pflegesachleistung/Pflegegeld nach Pflegegrad) erfüllen das NICHT.",
        ["Bescheid"] = "Es ist ein Bescheid der Pflegekasse über den PFLEGEGRAD (Feststellung, Erhöhung, Senkung oder Ablehnung) auf Basis der Begutachtung durch den Medizinischen Dienst.",
        ["Gutachten"] = "Es ist das Pflegegutachten des Medizinischen Dienstes (MD/MDK) selbst — der Begutachtungsbericht —, nicht der daraus folgende Bescheid der Kasse.",
    };

    // Real-world German labels per column, so the extractor maps document wording to our columns.
    private static readonly Dictionary<string, string[]> FieldSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ValidFrom"] = new[] { "Gültig ab", "Verordnungszeitraum von", "Behandlungszeitraum von", "Bewilligungszeitraum von" },
        ["ValidUntil"] = new[] { "Gültig bis", "Verordnungszeitraum bis", "Behandlungszeitraum bis", "Bewilligungszeitraum bis" },
        ["PrescriptionOrder"] = new[] { "Erstverordnung", "Folgeverordnung" },
        ["CareLevel"] = new[] { "Pflegegrad" },
    };

    /// <summary>Identifies this extraction-prompt revision on every Extracted event. Covers all three prompts
    /// (classify / self-check / field extraction) as ONE id, because a document's extraction is their joint product.
    ///
    /// Includes a digest of <see cref="DocumentCatalog"/> and <see cref="Vocab"/>: the type list, descriptions,
    /// aliases, field columns and allowed choice values are injected verbatim into the prompts, so editing the catalog
    /// changes the model's task exactly as editing a literal does — and must move the hash. Also includes the
    /// escalation thresholds and the transcript truncation limit, which decide which prompt runs and on how much text.</summary>
    /// <remarks>Declared AFTER the fields it reads, and lazy on top of that. Static field initializers run in textual
    /// declaration order, so an eager initializer placed above <see cref="DefiningCriteria"/> observes it as null and
    /// throws TypeInitializationException on the first extraction — the placement is load-bearing, not cosmetic.
    /// Deferring to first access keeps it correct even if these fields are later reordered.</remarks>
    private static readonly Lazy<string> LazyTemplateHash = new(() => PromptHash.Of(
        DomainSystemPrompt, ClassifySystemPrompt, SelfCheckSystemPromptTemplate, ExtractSystemPrompt,
        $"domainEnum={string.Join("/", DocumentDomain.All)}",
        string.Join("|", DocumentCatalog.Types.Select(t =>
            $"{t.ContentType}~{t.Description}~{string.Join(",", t.Aliases ?? Array.Empty<string>())}" +
            $"~{string.Join(",", t.Fields.Select(f => $"{f.Column}:{f.Kind}:{f.Label}:{string.Join("/", Vocab.For(f.Column) ?? Enumerable.Empty<string>())}"))}")),
        string.Join("|", DefiningCriteria.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")),
        string.Join("|", FieldSynonyms.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={string.Join(",", k.Value)}")),
        ThresholdFragment()));

    /// <summary>The numeric half of the hash input. A METHOD, not an interpolated literal inline above, so a test can
    /// evaluate it under an arbitrary culture — a lazily-initialised static would bake in whichever culture happened
    /// to touch it first and could not be re-checked.
    ///
    /// INVARIANT formatting is load-bearing here. These are doubles and default interpolation is CULTURE-SENSITIVE:
    /// on a de-DE machine 0.80 renders "0,8", on an en-US/invariant CI runner "0.8". The hash that identifies a prompt
    /// REVISION therefore differed per machine, so the deployed Linux container would have stamped a different
    /// promptHash than the dev box for byte-identical prompts — precisely the attribution failure the hash exists to
    /// prevent. Caught by CI disagreeing with the pinned value.</summary>
    internal static string ThresholdFragment() => FormattableString.Invariant(
        $"escalateBelow={ClassifyEscalateBelow};selfCheckCap={SelfCheckReviewCap};truncate=12000;maxTokens=200/200/900");

    internal static string TemplateHash => LazyTemplateHash.Value;

    private static readonly HashSet<string> IdentityCols =
        new(StringComparer.OrdinalIgnoreCase) { "ContentType", "KVNR", "LastName", "FirstName", "BirthDate", "DocumentDate" };

    private static object NullableStr() => new Dictionary<string, object?> { ["type"] = new[] { "string", "null" } };

    private static object Schema(string name, Dictionary<string, object?> props, params string[] required) => new Dictionary<string, object?>
    {
        ["type"] = "json_schema",
        ["json_schema"] = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["strict"] = true,
            ["schema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = props,
                ["required"] = required,
            },
        },
    };

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

    // German day-first formats, EXACT-only. Two-digit years resolve via the invariant calendar (TwoDigitYearMax:
    // "26" → 2026); slashed dates on German documents are still day-first (dd/MM).
    private static readonly string[] GermanDateFormats = { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "dd/MM/yyyy", "d/M/yyyy" };

    /// <summary>Normalise a German-document date to ISO yyyy-MM-dd via EXACT day-first formats only — NEVER a lenient
    /// parse (invariant+lenient reads month-first and silently swaps day/month on ambiguous dates: "02.07" → Feb 7).
    /// Unparseable input returns NULL — the field is treated as ABSENT, so a required date routes the document to
    /// review (missing_required) instead of filing junk or a swapped value.</summary>
    private static string? NormDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s.Trim(), GermanDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }

    private static bool NonEmpty(string? s) => !string.IsNullOrWhiteSpace(s);
    private static void AddIf(List<ExtractedField> f, string name, string? val, double conf) { if (NonEmpty(val)) f.Add(new(name, val!.Trim(), conf, "vision")); }
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
