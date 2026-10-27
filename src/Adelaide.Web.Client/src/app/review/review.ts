import { Component, DestroyRef, ElementRef, HostListener, OnInit, computed, effect, inject, signal, viewChild, viewChildren } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { RotationEditor } from '../rotation-editor';
import { cleanName } from '../text-safe';
import { FormsModule } from '@angular/forms';
import { Api } from '../api';
import { FieldSchema, PatientCandidate, ReviewData } from '../models';
import { PdfPane } from '../pdf-pane/pdf-pane';
import { DateInput } from '../date-input/date-input';
import { Icon } from '../ui/icon';

/** Identity signals — rendered as a fixed block (the columns the auto-file gate keys on). */
const IDENTITY: { col: string; label: string; kind: string }[] = [
  { col: 'KVNR', label: 'Versichertennummer', kind: 'Text' },
  { col: 'LastName', label: 'Nachname', kind: 'Text' },
  { col: 'FirstName', label: 'Vorname', kind: 'Text' },
  { col: 'BirthDate', label: 'Geburtsdatum', kind: 'Date' },
  { col: 'DocumentDate', label: 'Dokumentdatum', kind: 'Date' },
];

/** Functional-area labels offered when filing a non-patient business document. The SharePoint column keeps "Patient"
 *  too (for Phase-2 reuse), but the business action offers only this non-patient subset. Values MUST match the column choices. */
const ZIELBEREICHE = ['HR', 'Finance', 'unklar'] as const;

@Component({
  selector: 'app-review',
  imports: [FormsModule, RouterLink, PdfPane, DateInput, Icon],
  templateUrl: './review.html',
  styleUrl: './review.css',
})
export class Review implements OnInit {
  private api = inject(Api);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private destroyRef = inject(DestroyRef);

  id = signal('');
  loading = signal(true);
  error = signal<string | null>(null);
  data = signal<ReviewData | null>(null);

  // form state
  contentType = signal('');
  values = signal<Record<string, string>>({}); // column -> value (identity + type-specific)
  patient = signal<PatientCandidate | null>(null);

  // The provenance bit (docs/CAPTURE.md WP4): the LAST WRITER of each field's value — 'prefill' (seeded from the
  // model's proposal), 'patient' (applyPatient overwrote it from the patient record), 'typed' (the reviewer edited
  // it). Sent with the submission so the backend can record HOW each value got into the form — the observation that
  // MATCH/CORRECTION/FILL could previously only reconstruct structurally. Purely observational: nothing in this
  // component reads it back.
  fieldProv = signal<Record<string, string>>({});
  private markProv(cols: string[], writer: 'prefill' | 'patient' | 'typed') {
    if (cols.length === 0) return;
    this.fieldProv.update((m) => {
      const n = { ...m };
      for (const c of cols) n[c] = writer;
      return n;
    });
  }

  // Business-document toggle (Phase 1): when on, this is a non-patient "Geschäftsdokument" → the patient picker and
  // medical type-specific fields are hidden, the patient requirement is relaxed, and submit files it to the business
  // holding library with no patient. The server records the domain reassignment (domainChange) as labeled data.
  isBusiness = signal(false);
  setBusiness(v: boolean) {
    this.isBusiness.set(v);
    if (v) this.clearPatient(); // drop any selected patient so nothing stale is sent
    else this.zielbereich.set(''); // drop the business label when leaving the business path
  }

  // Business-only functional-area label (HR/Finance/unklar) — REQUIRED to file a Geschäftsdokument; the server records
  // it on the audit ledger (system of record) and best-effort to the SharePoint Zielbereich column.
  zielbereich = signal('');
  readonly zielbereiche = ZIELBEREICHE;

  // patient search
  query = signal('');
  candidates = signal<PatientCandidate[]>([]);
  searching = signal(false);

  // Panel collapse (review steady state only): hide the "Dokument prüfen" form so the document pane fills the width —
  // reading room without a modal. Starts expanded on every document (a fresh component per entry, not persisted). Form
  // state is untouched (the panel is [hidden], not destroyed).
  panelCollapsed = signal(false);
  toggleCollapse() { this.panelCollapsed.update((v) => !v); }

  submitting = signal(false);
  submitError = signal<string | null>(null);

  readonly identityFields = IDENTITY;

  // per-field model confidence (lower-cased name -> confidence) for highlighting
  private conf = computed(() => {
    const m = new Map<string, number>();
    for (const f of this.data()?.payload.fields ?? []) m.set(f.name.toLowerCase(), f.confidence);
    return m;
  });

  // Schema fields for the SELECTED content type minus the identity columns (those render in their own block). The type
  // dropdown is editable, so this MUST track contentType() — not the document's original classification — otherwise a
  // reviewer who switches type never sees the new type's fields (e.g. Verordnung's required Verordnungsfolge) yet the
  // server still requires them. Falls back to the single `schema` if an older server didn't send the per-type map.
  typeFields = computed<FieldSchema[]>(() => {
    const idcols = new Set(IDENTITY.map((i) => i.col.toLowerCase()));
    const d = this.data();
    const s = d?.schemas?.[this.contentType()] ?? d?.schema ?? [];
    return s.filter((f) => !idcols.has(f.column.toLowerCase()));
  });

  // Identity fields to render: for a business document only the DocumentDate (the sole holding-library metadata);
  // the patient-identity columns (KVNR/name/DOB) are irrelevant and hidden.
  visibleIdentityFields = computed(() => (this.isBusiness() ? IDENTITY.filter((f) => f.col === 'DocumentDate') : IDENTITY));

  // the document's ORIGINAL extracted identity (immutable) — shown next to the chosen patient for comparison,
  // independent of the editable fields (which get reconciled to the chosen patient on selection)
  private modelVal(name: string): string {
    const f = (this.data()?.payload.fields ?? []).find((x) => x.name.toLowerCase() === name.toLowerCase());
    return f?.value ?? '';
  }
  docName = computed(() => `${this.modelVal('LastName')}, ${this.modelVal('FirstName')}`.replace(/^,\s*|,\s*$/g, ''));
  docDob = computed(() => this.modelVal('BirthDate'));
  docKvnr = computed(() => this.modelVal('KVNR'));

  // KVNR format check (server remains source of truth for the check digit)
  kvnrValid = computed(() => {
    const k = (this.values()['KVNR'] ?? '').trim().toUpperCase();
    return k === '' || /^[A-Z]\d{9}$/.test(k);
  });

  // A field can be mandatory unconditionally (required) or conditionally (requiredUnless another field holds an exempt
  // value — e.g. Genehmigungsnummer is required unless ApprovalOutcome is "Abgelehnt"). Re-evaluated as the form changes.
  isRequired(f: FieldSchema): boolean {
    if (f.required) return true;
    const ru = f.requiredUnless;
    if (!ru) return false;
    const cur = (this.val(ru.field) ?? '').trim().toLowerCase();
    return !ru.values.some((v) => v.toLowerCase() === cur);
  }
  requiredFieldsValid = computed(() => this.typeFields().every((f) => !this.isRequired(f) || this.val(f.column).trim() !== ''));

  canSubmit = computed(() =>
    this.isBusiness()
      ? this.datesOk() && this.zielbereich() !== '' && !this.submitting() // business: no patient / medical required-fields; a valid date if present + a chosen Zielbereich
      : !!this.patient() && !!this.contentType() && this.kvnrValid() && this.datesOk() && this.requiredFieldsValid() && !this.submitting());

  ngOnInit() {
    const id = this.route.snapshot.paramMap.get('id') ?? '';
    this.id.set(id);
    this.supersedeTarget.set(this.route.snapshot.queryParamMap.get('supersede') ?? ''); // compare hand-off → supersede mode
    this.api.review(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (d) => { this.data.set(d); this.prefill(d); this.loading.set(false); },
      error: () => {
        this.error.set('Kein Prüf-Datensatz gefunden — bitte das Dokument erneut hochladen.');
        this.loading.set(false);
      },
    });
  }

  private prefill(d: ReviewData) {
    // A fallback-classified doc seeds the bare type ("Document"), which is NOT in the dropdown: the select would
    // render blank while the model silently holds the non-catalog value, and the server refuses non-catalog types
    // on the medical lane. Seed '' instead, so the required-type check blocks submit until a real type is chosen.
    this.contentType.set(d.types?.includes(d.payload.contentType) ? d.payload.contentType : '');
    // Seed the persisted orientation so a re-opened document keeps the reviewer's earlier rotation (both the optimistic
    // display angle and the confirmed revert target start from the server's saved value).
    this.roter.seed(d.payload.savedRotation ?? 0);
    const v: Record<string, string> = {};
    for (const f of d.payload.fields) {
      if (f.value != null && f.name.toLowerCase() !== 'contenttype') v[f.name] = f.value;
    }
    this.values.set(v);
    this.markProv(Object.keys(v), 'prefill'); // everything seeded from the model's proposal starts as 'prefill'
    if (d.payload.match?.candidate) this.applyPatient(d.payload.match.candidate);
    // ambiguous match → the matcher already found the contenders; present them instead of forcing a re-search
    else if (d.payload.match?.candidates?.length) this.candidates.set(d.payload.match.candidates);
  }

  display(p: PatientCandidate): string {
    return `${p.lastName}, ${p.firstName}${p.patientNr ? ` (${p.patientNr})` : ''}`;
  }

  confidence(col: string): number | null {
    const c = this.conf().get(col.toLowerCase());
    return c == null ? null : c;
  }
  uncertain(col: string): boolean {
    const c = this.confidence(col);
    return c != null && c < 0.8;
  }
  pct(col: string): string {
    const c = this.confidence(col);
    return c == null ? '' : Math.round(c * 100) + '%';
  }

  val(col: string): string { return this.values()[col] ?? ''; }
  setValue(col: string, value: string) {
    this.values.update((m) => ({ ...m, [col]: value }));
    this.markProv([col], 'typed'); // every template input routes here — a reviewer keystroke is the last writer
  }

  // Date fields are localized Material datepickers (DateInput: TT.MM.JJJJ typing + German calendar);
  // submit stays blocked while any date field holds an unparseable entry.
  private dateInputs = viewChildren(DateInput);
  datesOk = computed(() => this.dateInputs().every((c) => !c.invalid()));

  search() {
    const q = this.query().trim();
    if (q.length < 2) { this.candidates.set([]); return; }
    this.searching.set(true);
    this.api.patients(q).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (list) => { this.candidates.set(list); this.searching.set(false); },
      error: () => { this.candidates.set([]); this.searching.set(false); },
    });
  }
  choose(p: PatientCandidate) { this.applyPatient(p); this.candidates.set([]); this.query.set(''); }
  clearPatient() { this.patient.set(null); }

  // Reconcile the editable identity fields to the chosen patient — these values (not the listItemId) drive the
  // filed KVNR/name/DOB columns and the archived filename, so they must agree with the linked patient. Fields the
  // patient record actually supplied are marked provenance 'patient'; a fallback to the existing value keeps its
  // previous writer (the record wrote nothing there).
  private applyPatient(p: PatientCandidate) {
    this.patient.set(p);
    const wrote: string[] = [];
    this.values.update((v) => {
      const next = { ...v };
      const put = (col: string, val: string | null | undefined) => {
        if (val) { next[col] = val; wrote.push(col); }
        else next[col] = v[col] || '';
      };
      put('KVNR', p.kvnr);
      put('LastName', p.lastName);
      put('FirstName', p.firstName);
      put('BirthDate', p.birthDate);
      return next;
    });
    this.markProv(wrote, 'patient');
  }

  // Top bar's Download: the ORIGINAL, untouched PDF (not the rotated/split preview). Same-origin → the anchor's
  // `download` attribute saves it.
  downloadUrl = computed(() => this.api.fileUrl(this.id()));
  downloadName = computed(() => {
    const n = cleanName(this.data()?.payload.original || this.id());
    return /\.pdf$/i.test(n) ? n : n + '.pdf';
  });

  // `R` advances the same document-wide rotation as the viewer pill — except while a form field has focus.
  @HostListener('document:keydown', ['$event'])
  onRotateKey(e: KeyboardEvent): void {
    if ((e.key !== 'r' && e.key !== 'R') || e.ctrlKey || e.metaKey || e.altKey) return;
    const t = e.target as HTMLElement | null;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) return;
    if (this.loading() || this.error()) return;
    this.rotateDoc();
  }

  pageCount = computed(() => this.data()?.payload.pageCount ?? 1);

  // The REAL PDF page count (the transcript's pageCount caps at 15 for cost). The split rail + ranges use THIS so a
  // 20–30-page fax stack shows every page and the cuts cover the whole document. Falls back to the transcript count.
  filePageCount = computed(() => this.data()?.payload.filePageCount ?? this.pageCount());
  railPages = computed(() => Array.from({ length: this.filePageCount() }, (_, i) => i + 1));

  // ---- Page orientation (persisted document property) ----------------------------------------------------------------
  // The reviewer sets the document-wide absolute orientation (0/90/180/270). It is an OBJECTIVE document fact (like the
  // detected type or matched patient), NOT a per-session view setting — so it is SAVED the instant they rotate and
  // survives leaving/re-opening the queue; `prefill` seeds it from the GET. The submit no longer carries rotation: the
  // server READS the saved sidecar and bakes it at filing. The optimistic angle + per-click save + revert-on-failure +
  // the submit guard all live in a shared RotationEditor — identical core to the preview modal, EXTRACTED not duplicated,
  // so the correctness-critical revert/guard can't drift between surfaces.
  readonly roter = new RotationEditor(this.api);
  savedRotation = this.roter.angle;   // optimistic display angle — the pane [rotate] + the rail thumbs read this
  rotationError = this.roter.error;   // shown near the pill on a failed save (the view reverting is the primary signal)

  // Any rotation applied? Drives the "Gedrehte Seiten werden so abgelegt" commit note.
  hasRotation = computed(() => this.savedRotation() !== 0);

  // The pill's rotate button and the `R` key both route here → one quarter-turn clockwise, optimistic + persisted.
  rotateDoc() { this.roter.rotate(this.id()); }

  // SPLIT (net-new, a SEPARATE action that does NOT file): the reviewer marks where a multi-page scan should be CUT into
  // independent documents. A cut is modelled as "cut AFTER page p" for p in 1..pageCount-1; the set of cuts partitions
  // the doc into contiguous parts. doSplit() re-enters each part into the pipeline as its own document and the parent
  // leaves the review queue (server records the terminal Segmented fan-out). It never touches the file/rotate/dup flows.
  cuts = signal<Set<number>>(new Set());
  hasCut(p: number): boolean { return this.cuts().has(p); }
  // Toggle a cut after page p — guarded to the valid 1..pageCount-1 range so no stale boundary can slip in.
  toggleCut(p: number) {
    if (p < 1 || p >= this.filePageCount()) return;
    this.cuts.update((s) => { const n = new Set(s); if (n.has(p)) n.delete(p); else n.add(p); return n; });
  }
  // Derive the contiguous [first,last] parts from the cuts: each cut after page c closes the current part at c and opens
  // the next at c+1; the final part runs to the last page. ALWAYS emits length-2 pairs — a single-page part is [p,p],
  // because the server filters ranges to exactly [first,last] pairs (a bare [6] would be dropped, then fail coverage);
  // [6,6] is what it requires. Cuts {2,5} on a 6-page doc → [[1,2],[3,5],[6,6]].
  splitRanges = computed<number[][]>(() => {
    const n = this.filePageCount();
    const cuts = [...this.cuts()].filter((c) => c >= 1 && c < n).sort((a, b) => a - b);
    const ranges: number[][] = [];
    let start = 1;
    for (const c of cuts) { ranges.push([start, c]); start = c + 1; }
    ranges.push([start, n]);
    return ranges;
  });
  // Split is possible only for a multi-page doc with ≥1 cut (→ ≥2 parts) — mirrors the server's ≥2-ranges guard.
  canSplit = computed(() => this.filePageCount() > 1 && this.cuts().size > 0);

  splitting = signal(false);
  splitError = signal<string | null>(null);

  // SEPARATE action — does NOT file. Partition the doc and re-enter each part into the pipeline; on success the parent
  // has left the review queue (terminal Segmented), so navigate back to the queue exactly like fileNow does. Simple
  // in-flight guard (splitting) + error surface (splitError), mirroring the existing submit.
  doSplit() {
    if (!this.canSplit() || this.splitting()) return;
    this.splitting.set(true);
    this.splitError.set(null);
    this.api.split({ documentId: this.id(), ranges: this.splitRanges(), names: this.splitRanges().map((r, i) => this.nameFor(r, i)) }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.splitting.set(false); this.router.navigate(['/']); },
      error: (e) => {
        this.splitting.set(false);
        this.splitError.set(e?.error?.detail ?? e?.error ?? 'Aufteilen fehlgeschlagen.');
      },
    });
  }

  // ---- Split MODE (launched, not a permanent section) ----
  // Splitting is a mode the reviewer ENTERS from the steady-state panel: it takes over the document pane (a thumbnail
  // rail with cut-gaps) and swaps the panel to the resulting-documents list. The cut model + doSplit are UNCHANGED —
  // this only gates when that UI is shown. Enter/leave resets the cuts so a mode always starts clean.
  splitMode = signal(false);
  enterSplit() { this.cuts.set(new Set()); this.partNames.set({}); this.splitError.set(null); this.splitMode.set(true); }
  exitSplit() { this.splitMode.set(false); this.cuts.set(new Set()); this.partNames.set({}); }

  // Rail tiles: a small render carrying the reviewer's saved document orientation so the overview matches the reading
  // view — a document rotated upright in review shows upright here too, not as the raw (upside-down) scan. Passes the
  // OPTIMISTIC angle (an explicit number) so a rotate updates the rail immediately, before the save round-trips.
  thumb(page: number): string { return this.api.pageThumbUrl(this.id(), page, 160, this.savedRotation()); }

  // ---- Split rail ↔ viewer sync ----
  // The viewport IS the shared page-image viewer (app-pdf-pane, present only in split mode). We drive it imperatively:
  // clicking a rail tile jumps it, and it reports its top page back via (pageChange) → currentPage → rail highlight.
  // (A shared server-image viewer, not the old native iframe, because the iframe exposed neither a page-jump nor its
  // scroll position — and one image path for review+split closes the orientation mismatch by construction.)
  currentPage = signal(1);

  private splitViewer = viewChild('viewer', { read: PdfPane });
  private railTiles = viewChildren<ElementRef<HTMLElement>>('railTile');
  // Keep the current page's rail tile in view as the reader moves (from a scroll or a jump). block:'nearest' only moves
  // the rail (its own overflow) minimally — nothing else scrolls.
  private railFollow = effect(() => {
    const cur = this.currentPage();
    if (!this.splitMode()) return;
    this.railTiles()[cur - 1]?.nativeElement.scrollIntoView({ block: 'nearest' });
  });

  // Rail tile click → jump the viewer to that page; highlight it immediately (the viewer confirms via pageChange).
  jumpToPage(page: number) { this.currentPage.set(page); this.splitViewer()?.scrollToPage(page); }

  // Which resulting document (0-based) each page belongs to — colours each rail tile to match its card (per the split
  // reference). Derived from splitRanges so it tracks the cuts live.
  pagePartIndex = computed<Record<number, number>>(() => {
    const m: Record<number, number> = {};
    this.splitRanges().forEach((r, i) => { for (let p = r[0]; p <= r[1]; p++) m[p] = i; });
    return m;
  });
  partIndexOf(page: number): number { return this.pagePartIndex()[page] ?? 0; }

  // Editable per-document display names, keyed by the part's FIRST PAGE (stable as other cuts change). Default is the
  // detected type + part number ("Erstgespräch — Teil 1"), else "Teil N". Carried to /api/split as the child's name;
  // the child still RE-PROCESSES (fresh classify/match + its own review) — only its display name is the reviewer's.
  partNames = signal<Record<number, string>>({});
  defaultPartName(i: number): string { const t = this.contentType(); return t ? `${t} — Teil ${i + 1}` : `Teil ${i + 1}`; }
  nameFor(range: number[], i: number): string { return this.partNames()[range[0]] ?? this.defaultPartName(i); }
  setName(firstPage: number, value: string) { this.partNames.update((m) => ({ ...m, [firstPage]: value })); }

  // Duplicate guardrail: hitting submit on a flagged document opens a full-screen overlay that shows BOTH documents
  // side by side (plus an optional pixel diff) with the confirm controls right there — the reviewer compares and
  // decides in one place, never leaving the page or opening a tab. The server enforces the acknowledgement too.
  dupModalOpen = signal(false);
  dupAck = signal(false);
  isDuplicate = computed(() => !!this.data()?.payload.duplicateOf);
  dupId = computed(() => this.data()?.payload.duplicateOf?.documentId ?? '');
  dupName = computed(() => this.data()?.payload.duplicateOf?.original || this.dupId());
  // The duplicate TARGET's saved orientation (0 for a filed target — its /Rotate is baked), passed to its overlay pane.
  dupSavedRotation = computed(() => this.data()?.payload.duplicateOf?.savedRotation ?? 0);
  showDupDiff = signal(false);
  dupDiffPage = signal(1);
  dupDiffSrc = computed(() => this.api.diffUrl(this.id(), this.dupId(), this.dupDiffPage()));

  // Supersede mode: reached via the compare screen's "Vorhandenes ersetzen…" (query param ?supersede=<targetId>). Active
  // ONLY when the target equals this doc's duplicateOf (mirrors the server guard); then submit files the new doc and the
  // server deletes + marks the target Superseded. The target's name is dupName() (the target IS the duplicateOf).
  supersedeTarget = signal<string>('');
  supersedeActive = computed(() => !!this.supersedeTarget() && this.supersedeTarget() === this.dupId());

  // Offer supersede INSIDE the duplicate-confirm overlay (the diff viewer — the reviewer's real decision point) exactly
  // when the target is still Filed AND id-bearing. A legacy Filed target (no driveItemId) shows the disabled backfill
  // hint instead — same rule as the compare screen. state/hasLocator come live from /api/review.
  dupSupersedable = computed(() => {
    const d = this.data()?.payload.duplicateOf;
    return !!d && d.state === 'Filed' && d.hasLocator === true;
  });
  dupSupersedeLegacy = computed(() => {
    const d = this.data()?.payload.duplicateOf;
    return !!d && d.state === 'Filed' && d.hasLocator !== true;
  });

  submit() {
    // Business document: no patient. Still honour the duplicate compare-and-confirm overlay if one was flagged
    // (the server enforces the ack too), then file to the business holding library.
    if (this.isBusiness()) {
      if (!this.canSubmit()) return;
      if (this.isDuplicate() && !this.dupAck()) { this.dupModalOpen.set(true); return; }
      this.fileBusiness();
      return;
    }
    const p = this.patient();
    if (!p || !this.canSubmit()) return;
    // Supersede: the reviewer already compared both docs on the compare screen, so skip the dup-ack overlay and go
    // straight to ONE window.confirm that names BOTH fates. (Supersede implies the ack; the server enforces that too.)
    if (this.supersedeActive()) { this.confirmSupersede(p.listItemId); return; }
    if (this.isDuplicate() && !this.dupAck()) { this.dupModalOpen.set(true); return; } // compare-and-confirm overlay
    this.fileNow(p.listItemId);
  }
  // The single supersede confirm+submit, reached from BOTH paths: the ?supersede= hand-off (submit() → here, target
  // already set) and the overlay's "Vorhandenes ersetzen" button (here → sets target + closes overlay). One
  // window.confirm names BOTH fates; only after it does it enter supersede mode and file, so a cancel changes nothing.
  confirmSupersede(patientListItemId?: number) {
    const pid = patientListItemId ?? this.patient()?.listItemId;
    if (pid == null) return;
    const newName = cleanName(this.data()?.payload.original || this.id());
    const oldName = cleanName(this.dupName());
    // Recoverability wording stays intentionally NON-specific. The bin itself was verified (supersede deletions matched
    // Recycle Bin entries), but the WINDOW is not: 93 days across both stages is
    // Microsoft's default, owner-attested as unchanged, never read from the API — and any deleter or admin can
    // empty a stage early. So it is a ceiling, and the German text keeps "i. d. R." rather than naming a number.
    if (!window.confirm(`„${newName}" wird abgelegt und ersetzt „${oldName}".\n\nDas vorhandene Dokument „${oldName}" wird aus SharePoint entfernt (i. d. R. über den Papierkorb wiederherstellbar).\n\nFortfahren?`)) return;
    this.supersedeTarget.set(this.dupId()); // enter supersede mode (no-op if already set via the ?supersede= hand-off)
    this.dupModalOpen.set(false);           // close the compare-and-confirm overlay if that's where we came from
    this.fileNow(pid);
  }
  cancelDuplicate() { this.dupModalOpen.set(false); }
  confirmDuplicate() {
    this.dupAck.set(true);
    this.dupModalOpen.set(false);
    if (this.isBusiness()) { this.fileBusiness(); return; }
    const p = this.patient();
    if (p) this.fileNow(p.listItemId);
  }
  toggleDupDiff() { this.showDupDiff.update((v) => !v); }
  dupDiffPrev() { if (this.dupDiffPage() > 1) this.dupDiffPage.update((p) => p - 1); }
  dupDiffNext() { this.dupDiffPage.update((p) => p + 1); }
  onDupDiffError() { if (this.dupDiffPage() > 1) this.dupDiffPage.update((p) => p - 1); else this.showDupDiff.set(false); }

  private async fileNow(patientListItemId: number) {
    this.submitting.set(true);
    this.submitError.set(null);
    await this.roter.whenSettled(); // race guard: the bake reads the saved sidecar, so any in-flight PUT must land first
    this.api.submitReview({
      documentId: this.id(),
      contentType: this.contentType(),
      patientListItemId,
      fields: this.values(),
      fieldProvenance: this.fieldProv(), // how each value got into the form — observational, server-validated
      acknowledgedDuplicate: this.dupAck() || undefined,
      supersedeTarget: this.supersedeActive() ? this.supersedeTarget() : undefined,
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.submitting.set(false); this.router.navigate(['/']); },
      error: (e) => {
        this.submitting.set(false);
        this.submitError.set(e?.error?.detail ?? e?.error ?? 'Ablage fehlgeschlagen.');
      },
    });
  }

  // Business filing: no patient (patientListItemId 0), isBusiness=true → the server files to the holding library and
  // records the domainChange. contentType stays the model's guess (the server uses the configured business type for
  // the destination); acknowledgedDuplicate carries through if the reviewer confirmed a flagged duplicate.
  private async fileBusiness() {
    this.submitting.set(true);
    this.submitError.set(null);
    await this.roter.whenSettled(); // race guard: the bake reads the saved sidecar, so any in-flight PUT must land first
    this.api.submitReview({
      documentId: this.id(),
      // prefill blanks a non-catalog seeded type for the MEDICAL dropdown's sake; the business lane still wants the
      // model's guess to travel (the server only requires non-empty here — the destination uses the business type).
      contentType: this.contentType() || this.data()?.payload.contentType || 'Geschäftsdokument',
      patientListItemId: 0,
      fields: this.values(),
      fieldProvenance: this.fieldProv(), // same observational map on the business lane
      acknowledgedDuplicate: this.dupAck() || undefined,
      isBusiness: true,
      zielbereich: this.zielbereich(),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.submitting.set(false); this.router.navigate(['/']); },
      error: (e) => {
        this.submitting.set(false);
        this.submitError.set(e?.error?.detail ?? e?.error ?? 'Ablage fehlgeschlagen.');
      },
    });
  }
}
