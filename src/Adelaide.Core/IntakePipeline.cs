using System.Globalization;

namespace Adelaide.Core;

// Today is injectable for the date-validity guard (deterministic tests); null → UTC today at gate time.
public sealed record PipelineOptions(double MinQuality = 0.60, double MinConfidence = 0.80, IReadOnlySet<string>? AutoFileTypes = null, DateOnly? Today = null);

/// <summary>
/// Orchestrates the intake stages and applies the verify-or-review gate.
/// Robustness principle: never lose a document, never be silently wrong — anything unverifiable
/// is routed to review, and any unhandled failure is dead-lettered (held for a human), not dropped.
///
/// REFERENCE / DEMO ORCHESTRATOR ONLY — not the production filing path. It files with a single
/// <c>FileAsync</c> + <c>SetStateAsync</c> and has NO outbox: a crash between the SharePoint upload and the
/// state write leaves a filed-but-unrecorded document with no recovery. The production path
/// (Adelaide.Web's UploadProcessor) uses the "Filing" intent + deletable filing sidecar + reconcile
/// complete-from-intent. Do not route real intake through this type; keep it for unit-testing the stage wiring.
/// </summary>
public sealed class IntakePipeline(
    ITranscriber transcriber,
    ISeparator separator,
    IExtractor extractor,
    IPatientDirectory patients,
    IDocumentSink sink,
    ILedger ledger,
    PipelineOptions? options = null)
{
    private readonly PipelineOptions _opt = options ?? new PipelineOptions();

    public async Task<IReadOnlyList<ProcessingResult>> ProcessAsync(IncomingDocument doc, CancellationToken ct = default)
    {
        var results = new List<ProcessingResult>();
        try
        {
            await ledger.SetStateAsync(doc.DocumentId, ProcessingState.Received, ct);
            var whole = await transcriber.TranscribeAsync(doc, ct);
            await ledger.SetStateAsync(doc.DocumentId, ProcessingState.Transcribed, ct);

            var units = await separator.SeparateAsync(doc, whole, ct);
            await ledger.SetStateAsync(doc.DocumentId, ProcessingState.Separated, ct);

            foreach (var unit in units)
            {
                var data = await extractor.ExtractAsync(unit, ct);
                var match = await patients.MatchAsync(data, ct);
                var result = Decide(unit, unit.Transcript, data, match, _opt);

                await sink.FileAsync(doc, unit, result, ct);   // sink routes to target site or to the review library
                await ledger.SetStateAsync(unit.UnitId,
                    result.Disposition == Disposition.AutoFile ? ProcessingState.Filed : ProcessingState.NeedsReview, ct);
                results.Add(result);
            }
            return results;
        }
        catch (Exception)
        {
            // never lost: a poison document becomes a flagged dead-letter for a human, not a silent drop.
            await ledger.SetStateAsync(doc.DocumentId, ProcessingState.DeadLetter, ct);
            throw;
        }
    }

    /// <summary>The verify-or-review gate. Auto-file only when every check passes; otherwise route to review. Pure/static for testability.</summary>
    public static ProcessingResult Decide(DocumentUnit unit, Transcript transcript, ExtractedData data, PatientMatch match, PipelineOptions opt)
    {
        var reasons = new List<string>();
        var codes = new List<string>();                       // B2: structural condition tags parallel to reasons (PHI-safe; the prose stays in reasons)
        void Flag(string reason, string code) { reasons.Add(reason); codes.Add(code); }

        // A field the model did not supply is NOT evidence of a bad scan. Flag it as its own condition FIRST and skip the
        // quality reason, so a renamed/restructured response field cannot masquerade as a corpus-wide quality collapse
        // (the 0.5 fallback sits below every gate, so the routing to review is identical either way — only the reason
        // differs, and the reason is what a post-migration investigation reads). Uses the constant deliberately.
        var unparsed = (transcript.UnparsedFields ?? []).Concat(data.UnparsedFields ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (unparsed.Count > 0)
            Flag($"Modellantwort unvollständig — Feld(er) nicht lesbar: {string.Join(", ", unparsed)}; bitte prüfen", ReasonCodes.ModelFieldUnparsed);

        if (transcript.QualityScore < opt.MinQuality && !unparsed.Contains("legibility", StringComparer.Ordinal))
            Flag($"low scan quality ({transcript.QualityScore:F2} < {opt.MinQuality:F2})", "low_quality");

        // An empty/near-empty transcript is its own failure, independent of the legibility score that failed to notice
        // it (see TranscriptQuality). Placed BEFORE the business short-circuit so it applies to both lanes: an empty
        // business document is no more fileable than an empty medical one.
        if (TranscriptQuality.IsUnusable(transcript))
            Flag("Kein verwertbarer Text erkannt (Transkript leer oder zu kurz) — bitte Scan prüfen", ReasonCodes.EmptyTranscript);

        // The BUSINESS lane short-circuits the medical checks: there is no patient to match, no catalog type to
        // allowlist and no medical field schema, so running them would only manufacture spurious reasons
        // (no_match_no_kvnr, unclassified_type) on a document that was never medical. It always goes to a human —
        // the reviewer still owns the Zielbereich the filing target needs. See DocumentDomain.
        if (DocumentDomain.IsBusiness(data.Value("Domain")))
        {
            Flag("Betriebliches Dokument (kein Patientenbezug) — bitte Zielbereich wählen und bestätigen", ReasonCodes.BusinessDomain);
            return new ProcessingResult(unit.UnitId, Disposition.Review, reasons, data, match,
                data.Get("Domain")?.Confidence ?? 0d, codes);
        }

        var kvnr = data.Value("KVNR");
        if (!string.IsNullOrEmpty(kvnr) && !Kvnr.IsValid(kvnr))
            Flag("KVNR check-digit failed", "kvnr_checkdigit");

        // Match-failure reasons are reviewer-facing: say precisely WHY (blank field vs. unmatched number vs.
        // genuine collision), because each implies a different reviewer task.
        switch (match.Outcome)
        {
            case MatchOutcome.None when string.IsNullOrEmpty(kvnr):
                Flag("kein Patiententreffer — keine Versichertennummer im Dokument gefunden; bitte Patient über die Suche zuordnen", "no_match_no_kvnr");
                break;
            case MatchOutcome.None when !Kvnr.IsValid(kvnr):
                Flag("kein Patiententreffer — Versichertennummer im Dokument unplausibel; bitte Patient über die Suche zuordnen", "no_match_invalid_kvnr");
                break;
            case MatchOutcome.None:
                Flag("kein Patiententreffer — gültige Versichertennummer ohne Treffer im Patientenstamm (auch Name + Geburtsdatum erfolglos)", "no_match_unmatched_kvnr");
                break;
            case MatchOutcome.Multiple when match.Method == "kvnr":
                Flag($"mehrere Patienten mit dieser Versichertennummer im Stamm ({match.Candidates?.Count ?? 2}) — bitte auswählen", "ambiguous_kvnr");
                break;
            case MatchOutcome.Multiple:
                Flag($"mehrere Patienten passen zu Name + Geburtsdatum ({match.Candidates?.Count ?? 2}) — bitte auswählen", "ambiguous_name_dob");
                break;
        }

        // Gate on IDENTITY + CLASSIFICATION confidence only — a shaky metadata field (e.g. a date) must not block
        // filing a correctly-identified document; low-confidence metadata is flagged for correction, not reviewed wholesale.
        var idConf = IdentityConfidence(data);
        if (idConf < opt.MinConfidence)
            Flag($"low identity/classification confidence ({idConf:F2} < {opt.MinConfidence:F2})", "low_confidence");

        // Classification routing that must hold regardless of how MinConfidence is tuned: the fallback type and any
        // self-check outcome other than "confirmed" always get a human look — never auto-filed on the model's own say-so.
        var ctField = data.Get("ContentType");
        if (string.Equals(ctField?.Value, DocumentCatalog.Fallback, StringComparison.OrdinalIgnoreCase))
            Flag("unclassified document type", "unclassified_type");
        switch (ctField?.Source)
        {
            case "vision/self-corrected": Flag("classification self-check overruled the classifier", "classify_self_corrected"); break;
            case "vision/unverified": Flag("classification could not be verified", "classify_unverified"); break;
        }

        // Deny-by-default auto-file allowlist: only validated content types may file without a human. A type NOT on the
        // list — including a high-confidence-but-WRONG classification (the confusable cases like Rezept→Medikamentenplan
        // or Datenschutz-Vollmacht→Vorsorgevollmacht) — always routes to review. The allowlist DOMINATES confidence:
        // it is a gate, not a hint. (Fallback already adds its own reason above.)
        var ct = ctField?.Value;
        if (!string.IsNullOrWhiteSpace(ct)
            && !string.Equals(ct, DocumentCatalog.Fallback, StringComparison.OrdinalIgnoreCase)
            && !(opt.AutoFileTypes ?? DefaultAutoFileTypes).Contains(ct))
            Flag("Inhaltstyp noch nicht für automatische Ablage freigegeben — bitte Typ prüfen und bestätigen", "not_allowlisted");

        // Erst/Folge that no trusted source could settle goes to a human. A checkbox the model "felt 0.9 sure" about
        // is not a basis to auto-file; only the deterministic barcode, or DI selection marks the model agreed with, are.
        switch (data.Get("PrescriptionOrder")?.Source)
        {
            case "di/conflict": Flag("Erst-/Folgeverordnung unklar: kein Barcode und der Ankreuz-Detektor widerspricht der Bilderkennung — bitte prüfen", "prescription_order_conflict"); break;
            case "vision/unverified": Flag("Erst-/Folgeverordnung unklar: kein Barcode, Ankreuzfeld nicht eindeutig erkannt — bitte prüfen", "prescription_order_unverified"); break;
        }

        // A mandatory field the model couldn't fill must be completed by a human before filing. Requiredness can be
        // conditional: a Genehmigungsnummer is required unless the outcome is an Ablehnung (rejections carry no number).
        if (!string.IsNullOrWhiteSpace(ctField?.Value) && DocumentCatalog.Find(ctField.Value) is { } typeDef)
            foreach (var f in typeDef.Fields)
                if (f.IsRequiredGiven(data.Value) && string.IsNullOrWhiteSpace(data.Value(f.Column)))
                    Flag($"Pflichtfeld '{f.Label}' fehlt — bitte ergänzen", $"missing_required:{f.Column}");

        // Date VALIDITY guard: a confidently day/month-swapped date is invisible to confidence gating (the model is
        // confidently wrong, and the gate deliberately excludes date confidence) — validity is the guard. An issue
        // date in the FUTURE is near-impossible legitimately; the +1 day grace absorbs the UTC-vs-German-local
        // midnight skew (a doc legitimately dated "today" in Germany can be tomorrow relative to the UTC date).
        var today = opt.Today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (DateOnly.TryParseExact(data.Value("DocumentDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var docDate)
            && docDate > today.AddDays(1))
            Flag("Dokumentendatum liegt in der Zukunft — bitte prüfen", "date_implausible");

        var disposition = reasons.Count == 0 ? Disposition.AutoFile : Disposition.Review;
        return new ProcessingResult(unit.UnitId, disposition, reasons, data, match, idConf, codes);
    }

    private static readonly string[] IdentitySignals = { "ContentType", "KVNR", "LastName", "BirthDate" };

    // Auto-file is OPT-IN per content type (deny-by-default): only types validated as reliable file without a human.
    // New/weakly-tuned types default to review until they earn a place here. Override via PipelineOptions for tests.
    private static readonly IReadOnlySet<string> DefaultAutoFileTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Verordnung", "Genehmigung" };

    /// <summary>Confidence of the signals that establish WHO the document is for and WHAT it is — not the metadata fields.</summary>
    private static double IdentityConfidence(ExtractedData data)
    {
        var signals = data.Fields.Where(f => IdentitySignals.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return signals.Count == 0 ? 0d : signals.Min(f => f.Confidence);
    }
}
