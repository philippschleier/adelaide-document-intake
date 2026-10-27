# Pipeline walkthrough

This follows a single scanned PDF through the production path, with pointers into the code. The orchestrating method
is `UploadProcessor.ProcessAsync` in [`src/Adelaide.Web/Program.cs`](../src/Adelaide.Web/Program.cs).
[`IntakePipeline`](../src/Adelaide.Core/IntakePipeline.cs) in Core is a smaller reference orchestrator used to unit-test
the stage wiring and the gate.

## 0. Intake

| Channel | Entry point | Notes |
|---|---|---|
| Browser upload | `POST /api/upload` | Reviewer-authenticated, CSRF header required, rate-limited. Up to 20 files and 25 MB per file. |
| Scanner / fax | `DrainPoller` (in-app) or `Adelaide.Drain` (on-prem) → `POST /api/drain/upload` | The idempotency key is the file's share-relative path (`SourceRecordId`). The share copy is deleted only after a confirmed ack. |

Both entry points check type and size, check the PDF signature, run optional malware scanning, compute a SHA-256, and
append `Received`. Identical bytes that are already known become a `DuplicateSuspect` **before** any model is called.

## 1. Transcribe — [`VisionTranscriber`](../src/Adelaide.Ai/VisionTranscriber.cs)

- PDFium renders each page at 300 dpi, up to a 15-page cost guard.
- **Local barcode pass** ([`BarcodeScanner`](../src/Adelaide.Ai/BarcodeScanner.cs)): zxing-cpp over the same bitmaps
  reads a whole page first, then crops and upscales corners for small codes. Payloads are kept in full but are only
  ever consumed by structural parsers:
  - [`BfbBarcode`](../src/Adelaide.Core/BfbBarcode.cs): the Muster 12 PDF417, with patient, KVNR, dates, first or
    follow-up order, and services;
  - [`BmpBarcode`](../src/Adelaide.Core/BmpBarcode.cs): the medication-plan DataMatrix `<MP>` XML.
- **GPT-4o vision** gets the page images with a transcribe-only instruction: reproduce what is visible, never complete
  or infer, and mark illegible text. It returns per-page Markdown plus a legibility score.
- The `Transcribed` event records the model deployment requested, the model version actually served, the prompt hash
  and the reasoning effort, plus the transcript's **SHA-256**, never its text.

## 2. Segment

`SegmentFile` produces document units with a `DerivationKey` = hash(file hash, page range, separator version). This
makes replay converge on the same `UnitId` instead of creating a duplicate. The default is one unit per file. A
reviewer split (`POST /api/split`) cuts the PDF losslessly ([`PdfPageOps`](../src/Adelaide.Ai/PdfPageOps.cs)) and hands
each child its slice of the parent transcript, so children skip a second vision call. Barcodes are re-decoded
**per child**, so a child never inherits a sibling's identity.

## 3. Classify and extract — [`AzureOpenAiExtractor`](../src/Adelaide.Ai/AzureOpenAiExtractor.cs)

- **Domain and type.** The model chooses Medical vs Business, then one of the 16 catalog types in
  [`DocumentCatalog`](../src/Adelaide.Core/DocumentCatalog.cs). Each type carries its own German definition, which is
  the single source of truth the classifier reads.
- **Self-check.** A second, constrained pass confirms or overrules the classification. The field source records the
  result (`vision/verified`, `vision/self-corrected`, `vision/unverified`).
- **Field extraction.** A forced JSON schema covers only that type's columns, with choice columns constrained to the
  [`Vocab`](../src/Adelaide.Core/Vocab.cs) values. Each field carries a confidence and a provenance.
- **Deterministic overrides.** Barcode identity (KVNR, name, DOB, first or follow-up order) beats vision. For
  checkbox-heavy forms, Document Intelligence layout selection marks are fed in as evidence. Where they disagree with
  the vision reading, the field is marked `di/conflict` and the gate sends the document to review.
- **Vocabulary guard.** [`FieldVocabulary`](../src/Adelaide.Core/FieldVocabulary.cs) drops any model-authored field
  name outside the closed set, because a free-text "field name" can itself contain PHI.

## 4. Match — [`PatientMatching`](../src/Adelaide.Core/PatientMatching.cs)

1. If the KVNR passes its check digit, look it up exactly. A hit, or an ambiguous hit, wins.
2. Otherwise, or on zero hits, use surname plus exact date of birth.
3. Resolve the hits: 0 → `None`. 1 → `Single`. More than one → `Single` only if exactly one record is active,
   else `Multiple` with the candidates attached for the reviewer's picker.

## 5. Gate — [`IntakePipeline.Decide`](../src/Adelaide.Core/IntakePipeline.cs)

The gate is pure and static. Each failing rule adds a German reason for the reviewer and a machine code for logs,
metrics and the ledger:

| Code | Rule |
|---|---|
| `model_field_unparsed` | the model response was missing or mistyped a field (so a fallback value is not mistaken for a bad scan) |
| `low_quality` | legibility below 0.60 |
| `empty_transcript` | no usable text |
| `business_domain` | not a patient document: business lane, reviewer chooses the target area |
| `kvnr_checkdigit` | KVNR present but invalid |
| `no_match_*`, `ambiguous_*` | zero or several patients, with the exact reason (blank KVNR, invalid KVNR, unmatched, collision) |
| `low_confidence` | min(confidence of type, KVNR, surname, DOB) below 0.80 |
| `unclassified_type`, `classify_*` | fallback type, or the self-check did not confirm |
| `not_allowlisted` | type not on the auto-file allowlist (deny-by-default) |
| `prescription_order_*` | first or follow-up order not settled by barcode or agreeing selection marks |
| `missing_required:<column>` | a required field is blank, including conditional requirements |
| `date_implausible` | document date in the future (catches confident day/month swaps) |

## 6. Capture

Every processed document lands its transcript, model inputs and an evaluation **corpus row** in the capture store.
The corpus row holds labels, confidences, provenance and the gate outcome, with no PHI. Capture is fail-open but
counted: a capture failure never fails a document, and it shows up on `/api/stats`.

## 7. Deduplicate

The three layers are exact SHA-256, the business key `{date}_{type}_{kvnr}_{period}`, and a business text fingerprint.
Each is protected by a non-blocking claim, so two identical documents processed concurrently cannot both pass the
"already filed?" check. Any hit → review with a compare view. A duplicate is never rejected automatically.

## 8. File or review

- **Auto-file:** the filing sidecar is written, then `Filing` is appended. `IFiler.FileAsync(spec)` uploads the DI
  searchable rendition (or the original, if DI is unavailable), applies the content type, sets the patient lookup and
  columns, and sets Volltext. Then `Filed` is appended with the driveItem id and whether the artifact declares PDF/A.
  Finally the inputs are released once the item is verified to exist.
- **Review:** a review sidecar is written (fields, candidates, reasons, transcript lead) and `NeedsReview` is appended.
  The Angular review screen (`/review/:id`) offers:
  - confirm or correct fields;
  - choose a patient (pre-filled from the match or candidates, with search by KVNR or surname);
  - rotate pages, split pages, or mark the document as a business document;
  - compare against a suspected duplicate, and supersede it.

  Submission re-runs the guards server-side ([`ReviewGuard`](../src/Adelaide.Web/Program.cs)), records a
  `Reviewed` event with a PHI-free diff of what the human changed, and files through the same outbox.

## 9. Reconcile

A periodic sweep (every 2 minutes by default) does four things:
- re-drives documents left in `Filing` and other intent states;
- re-queues stalled documents;
- releases filed inputs and discarded documents past their windows, when purge is enabled;
- reports sweep health.
