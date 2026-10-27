// DTOs mirroring the .NET /api endpoints.

export interface DocRow {
  documentId: string;
  original?: string | null;
  state: string;
  contentType?: string | null;
  disposition?: string | null;
  reasons?: string | null;
  reasonCodes?: string[] | null; // structured B2 gate codes (new NeedsReview docs only) — drives the Option-D leitgrund render; null for pre-existing docs
  confidence?: number | null;    // gate identity/classification confidence 0..1 (new docs only)
  correctedFields?: string[] | null; // Tier A: field NAMES a human changed at review (PHI-free); null = not reviewed, [] = reviewed no change
  contentTypeChange?: { from: string; to: string } | null; // Tier A: type-correction (non-PHI); null unless the type was corrected — the confusion matrix

  duplicateOf?: string | null;
  lastUpdated?: string;
  savedRotation?: number;  // persisted display orientation (0/90/180/270) for the preview quick-look; 0/absent for terminal docs
  orientable?: boolean;    // an in-review inbox PDF exists (the exact PUT .../rotation gate) → the preview's rotate control is enabled
  undiscardable?: boolean; // Discarded rows only: POST /api/undiscard would succeed right now (in-window, artifacts present) — gates the restore action
}

export interface Stats {
  total: number;
  byState: Record<string, number>; // e.g. { Filed: 1847, NeedsReview: 23, DeadLetter: 5, ... }
  duplicates?: number; // open possible-duplicates (exact + business-key), spanning states — the "Duplikat?" chip badge
}

export interface CompareDoc {
  documentId: string;
  original?: string | null;
  state: string;
  contentType?: string | null;
  lastUpdated?: string;
  duplicateOf?: string | null; // drives the compare-screen discard button for business-key (NeedsReview) duplicates
  available?: boolean; // false ONLY on confirmed absence (deleted from SharePoint since filing) → placeholder, not a broken iframe
  hasLocator?: boolean; // the existing (b) doc carries a durable driveItemId → supersede can delete it by id; false = legacy pre-backfill (Ersetzen disabled)
  savedRotation?: number; // this side's saved orientation; the parent passes it to the pane as [rotate] (0 for a filed side — /Rotate already baked)
}

export interface CompareData {
  a: CompareDoc; // the new/suspect document
  b: CompareDoc; // the existing document it may duplicate
}

// The dashboard's "Original ansehen" quick-look target. savedRotation seeds the modal's orientation and canRotate gates its
// rotate control — both come straight from the dashboard row (savedRotation / orientable), so opening the modal needs no fetch.
export interface PreviewDoc {
  id: string;
  name: string;
  savedRotation: number;
  canRotate: boolean;
}

export interface PatientCandidate {
  listItemId: number;
  kvnr: string;
  lastName: string;
  firstName: string;
  birthDate?: string | null;
  status: string;
  patientNr?: string | null; // KlientenNr — the number staff recognize; disambiguates same-name patients
  klientId?: string | null;  // stable MediFox UUID (cross-system key)
}

export interface ModelField {
  name: string;
  value: string | null;
  confidence: number;
  source: string;
}

export interface ReviewPayload {
  documentId: string;
  original?: string | null;
  contentType: string;
  duplicateOf?: { documentId: string; original?: string | null; fileName?: string | null; state?: string | null; hasLocator?: boolean; savedRotation?: number } | null; // state/hasLocator: the TARGET's live status → gates "Vorhandenes ersetzen"; savedRotation: the target pane's angle (0 for a filed target)
  classifyConfidence: number;
  markdown?: string;
  pageCount?: number;       // TRANSCRIPT page count (caps at 15 for cost)
  filePageCount?: number;   // real PDF page count — the split rail uses this so it covers the whole document
  savedRotation?: number;   // persisted document orientation (0/90/180/270); the client seeds its optimistic angle from this so a re-opened doc keeps the reviewer's rotation
  fields: ModelField[];
  match: { outcome: string; method?: string; candidate?: PatientCandidate | null; candidates?: PatientCandidate[] | null };
  reasons: string[];
}

export interface FieldSchema {
  column: string;
  label: string;
  kind: string; // Text | Date | Bool | Choice
  required?: boolean; // unconditionally mandatory — review blocks submit until filled
  requiredUnless?: { field: string; values: string[] } | null; // mandatory unless `field` holds one of `values`
  allowed?: string[] | null;
}

export interface ReviewData {
  payload: ReviewPayload;
  schema: FieldSchema[];                        // the document's ORIGINAL type schema (back-compat / fallback)
  schemas?: Record<string, FieldSchema[]>;      // schema per content type — the form renders the SELECTED type's fields
  types: string[];
}

export interface ReviewSubmission {
  documentId: string;
  contentType: string;
  patientListItemId: number;
  fields: Record<string, string | null>;
  fieldProvenance?: Record<string, string>; // last writer per field: prefill | patient | typed — the observed provenance bit; server validates names+enum and records it (Reviewed event + field_adjudications)
  acknowledgedDuplicate?: boolean; // set once the reviewer confirms a flagged possible-duplicate is distinct
  supersedeTarget?: string; // the duplicateOf doc this filing REPLACES — server files the new doc, then deletes + marks the target Superseded
  isBusiness?: boolean; // reviewer marked this a non-patient business document → files to the business holding library, no patient (server relaxes the patient guard)
  zielbereich?: string; // business only: the reviewer's functional-area label (HR/Finance/unklar) → recorded on the audit ledger (system of record) + best-effort Zielbereich column
  // NOTE: rotation is no longer part of this payload. Orientation is a persisted document property saved via
  // Api.saveRotation (PUT /api/documents/{id}/rotation); the server READS the saved angle and bakes it at submit.
}
