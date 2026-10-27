import { Component, DestroyRef, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, forkJoin } from 'rxjs';
import { cleanName } from '../text-safe';
import { RouterLink } from '@angular/router';
import { ScrollingModule } from '@angular/cdk/scrolling';
import { Api } from '../api';
import { DocRow, PreviewDoc, Stats } from '../models';
import { PdfViewer } from '../pdf-viewer/pdf-viewer';
import { Icon } from '../ui/icon';

const STATE_MAP: Record<string, [string, string]> = {
  Received: ['Eingegangen', 's-wait'],
  Stored: ['Gespeichert', 's-work'],      // in-flight: file persisted, pipeline continues
  Transcribed: ['Transkribiert', 's-work'],
  Separated: ['Getrennt', 's-work'],      // in-flight: passed the separation stage (legacy nominal name), pipeline continues
  Extracted: ['Daten erkannt', 's-work'],
  Matched: ['Patient zugeordnet', 's-work'],
  Filing: ['Wird abgelegt…', 's-work'],
  Filed: ['Abgelegt', 's-ok'],
  NeedsReview: ['Prüfung', 's-rev'],
  DeadLetter: ['Fehler', 's-err'],
  DuplicateSuspect: ['Duplikat?', 's-rev'],
  Discarded: ['Verworfen', 's-wait'],
  Superseded: ['Ersetzt', 's-wait'],      // terminal: replaced by a newer document (old SharePoint item deleted)
  Segmented: ['Aufgeteilt', 's-wait'],    // terminal: file fanned out into units (manual split / auto-separation) — NOT in-flight, not open work
  Purged: ['Entfernt', 's-wait'],         // sealed: recovery window elapsed, artifacts released — accepts no further events
};
// States that are still moving through the pipeline (neither terminal-done nor awaiting a human) — drives the
// "processing" count and the keep-polling decision.
const INFLIGHT_STATES = ['Received', 'Stored', 'Transcribed', 'Separated', 'Extracted', 'Matched', 'Filing'];

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, PdfViewer, ScrollingModule, Icon],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.css',
})
export class Dashboard implements OnInit, OnDestroy {
  private api = inject(Api);
  private destroyRef = inject(DestroyRef);

  docs = signal<DocRow[]>([]);
  stats = signal<Stats | null>(null);
  // "a fetch attempt has completed" — gates the empty-list message so it never flashes during the initial in-flight load
  // (the reload shift this fixes). Set true on success AND error (a failed first fetch still surfaces the empty state
  // rather than a permanent blank). NOT reset on scope switches — once we've loaded, we've loaded.
  loaded = signal(false);
  scope = signal<string>('NeedsReview'); // 'active' | 'all' | an exact state name (Filed, NeedsReview, …) for chip filters
  selected = signal<File[]>([]);
  dragDepth = signal(0); // counter-based drag tracking (inc on dragenter, dec on dragleave) — avoids per-child flicker
  uploading = signal(false);
  viewerDoc = signal<PreviewDoc | null>(null);
  processing = computed(() => {
    const s = this.stats();
    return s ? INFLIGHT_STATES.reduce((n, st) => n + (s.byState[st] ?? 0), 0) : 0;
  });
  live = computed(() => this.processing() > 0);
  // The "In Bearbeitung" chip lists the IN-FLIGHT docs specifically (so it matches its own count). We deliberately do NOT
  // fetch the server's 'active' scope here: it orders review docs first and is LIMIT-capped (300), so once NeedsReview
  // exceeds the cap the in-flight docs are truncated out entirely (confirmed: 'active' returned 300 rows, all NeedsReview,
  // zero in-flight). Instead fetch 'all' (recent-first) — in-flight docs are the most recently updated, so they sit near
  // the top — and narrow to the in-flight states here. Stays distinct from the "Nur offene" toggle (which IS 'active').
  visible = computed(() =>
    this.scope() === 'processing' ? this.docs().filter((d) => INFLIGHT_STATES.includes(d.state)) : this.docs());

  // ⓪ Honest "N of M": the TRUE total for the current scope (already client-side via /api/stats.byState), so a list
  // capped server-side can never hide work silently. byState[scope] for a state chip; sum-except-Filed/Discarded/Superseded for
  // open-work; duplicates/total for the dups/Gesamt chips; the in-flight count for "In Bearbeitung".
  totalForScope = computed(() => {
    const s = this.stats();
    if (!s) return this.visible().length;
    const sc = this.scope();
    if (sc === 'all') return s.total;
    if (sc === 'duplicates') return s.duplicates ?? 0;
    if (sc === 'processing') return this.processing();
    if (sc === 'active') return Object.entries(s.byState).reduce((n, [st, c]) => n + (st === 'Filed' || st === 'Discarded' || st === 'Superseded' || st === 'Segmented' || st === 'Purged' || st === 'SeededHistory' ? 0 : c), 0); // mirrors the server's active-scope exclusion (the full settled set — SeededHistory was missing from this hand-written list since the C9 seed shipped)
    return s.byState[sc] ?? 0;
  });

  readonly rowH = 56; // CDK virtual-scroll fixed itemSize (px) — MUST equal .vrow height in dashboard.css
  trackId = (_: number, d: DocRow) => d.documentId;

  private timer?: ReturnType<typeof setTimeout>;

  ngOnInit() { this.refresh(); }
  ngOnDestroy() { clearTimeout(this.timer); }

  pillLabel(s: string) { return (STATE_MAP[s] ?? ['…', 's-wait'])[0]; }
  pillClass(s: string) { return (STATE_MAP[s] ?? ['…', 's-wait'])[1]; }

  // Uppercase ONLY character 0; the ENTIRE REST of the string is left byte-for-byte unchanged — no lowercasing, no
  // title-casing, no per-clause capitalization ("kein…; Inhaltstyp…" → "Kein…; Inhaltstyp…"; "KVNR…" stays "KVNR…").
  capFirst(s: string | null | undefined): string {
    if (!s) return '';        // null / undefined / empty
    if (!s.trim()) return s;  // whitespace-only → unchanged
    return s[0].toUpperCase() + s.slice(1);
  }
  // The fixed-height hint line + its hover tooltip: the existing prose reasons (capitalize-first), else a per-state default.
  hint(d: DocRow): string {
    switch (d.state) {
      case 'Filed': return 'In SharePoint abgelegt';
      case 'Discarded': return 'Als Duplikat verworfen';
      case 'Superseded': return 'Durch neueres Dokument ersetzt';
      case 'Segmented': return 'In eigenständige Dokumente aufgeteilt';
      case 'Purged': return 'Endgültig entfernt (Wiederherstellungsfrist abgelaufen)';
      case 'NeedsReview': return d.reasons ? this.capFirst(d.reasons) : 'Prüfung nötig';
      case 'DuplicateSuspect': return d.reasons ? this.capFirst(d.reasons) : 'Inhaltsgleich mit bereits vorhandenem Dokument';
      case 'DeadLetter': return d.reasons ? this.capFirst(d.reasons) : 'Verarbeitung fehlgeschlagen';
      default: return 'in Bearbeitung…';
    }
  }
  kb(bytes: number) { return Math.round(bytes / 1024); }
  count(s: string) { return this.stats()?.byState[s] ?? 0; }
  setScope(s: string) { if (this.scope() !== s) { this.scope.set(s); this.refresh(); } }

  refresh() {
    clearTimeout(this.timer);
    // Counts (cheap aggregate over all docs) + the listed rows for the current scope, fetched together so the summary
    // and the table stay in sync. Keep polling only while something is still processing — once the backfill settles,
    // the list stops churning.
    const listScope = this.scope() === 'processing' ? 'all' : this.scope(); // 'processing' = in-flight subset; fetch 'all' (recent-first), NOT 'active' (review-ordered + LIMIT-capped → truncates in-flight out)
    forkJoin({ docs: this.api.documents(listScope), stats: this.api.stats() })
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: ({ docs, stats }) => {
          this.docs.set(docs);
          this.stats.set(stats);
          this.loaded.set(true);
          if (this.live()) this.timer = setTimeout(() => this.refresh(), 2000);
        },
        error: () => { this.loaded.set(true); this.timer = setTimeout(() => this.refresh(), 3000); },
      });
  }

  onFiles(list: FileList | null) {
    if (!list) return;
    const cur = this.selected();
    const add: File[] = [];
    for (const f of Array.from(list)) {
      if (f.type === 'application/pdf' && !cur.some((x) => x.name === f.name && x.size === f.size)) add.push(f);
    }
    if (add.length) this.selected.set([...cur, ...add]);
  }
  removeSel(f: File) { this.selected.set(this.selected().filter((x) => x !== f)); }
  clearSel() { this.selected.set([]); }

  // Reveal-on-drag overlay over the main content. Counter-based (dragenter/leave bubble per child, so a boolean flickers);
  // only for FILE drags (types includes 'Files', so dragging text/reordering doesn't show it); preventDefault on
  // dragover+drop or the browser navigates to the file instead of letting us handle it. Both the overlay drop and the
  // "+"-button picker funnel to the SAME onFiles() — one upload path.
  private isFileDrag(e: DragEvent) { return e.dataTransfer?.types.includes('Files') ?? false; }
  onDragEnter(e: DragEvent) { if (!this.isFileDrag(e)) return; e.preventDefault(); this.dragDepth.update((n) => n + 1); }
  onDragLeave(e: DragEvent) { if (!this.isFileDrag(e)) return; this.dragDepth.update((n) => Math.max(0, n - 1)); }
  onDragOver(e: DragEvent) { if (this.isFileDrag(e)) e.preventDefault(); }
  onDrop(e: DragEvent) { e.preventDefault(); this.dragDepth.set(0); this.onFiles(e.dataTransfer?.files ?? null); }

  submit() {
    const files = this.selected();
    if (!files.length) return;
    this.uploading.set(true);
    this.api.upload(files).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.selected.set([]); this.uploading.set(false); this.refresh(); },
      error: () => { this.uploading.set(false); this.refresh(); },
    });
  }

  // savedRotation + canRotate come straight from the (enriched) row — no fetch. canRotate = orientable (an in-review inbox
  // PDF exists, the exact PUT .../rotation gate); filed docs open at their baked angle, non-rotatable.
  openViewer(d: DocRow) { this.viewerDoc.set({ id: d.documentId, name: cleanName(d.original || d.documentId), savedRotation: d.savedRotation ?? 0, canRotate: d.orientable ?? false }); }
  closeViewer() { this.viewerDoc.set(null); }
  // A rotate inside the preview persisted a new angle → patch the cached row so a close-and-reopen seeds the CURRENT angle
  // (not the stale one it was listed with — that would be a smaller version of the propagation bug). Keyed to the open doc.
  onPreviewRotated(angle: number) {
    const id = this.viewerDoc()?.id;
    if (!id) return;
    this.docs.update((rows) => rows.map((r) => r.documentId === id ? { ...r, savedRotation: angle } : r));
  }

  busy = signal<Set<string>>(new Set());
  actionErrors = signal<Map<string, string>>(new Map());

  retry(d: DocRow) {
    this.execute(d, this.api.retry(d.documentId));
  }

  // Row-level discard visibility — mirrors the /api/duplicate-discard guard EXACTLY: exact-byte suspects OR
  // business-key duplicates (NeedsReview + duplicateOf). A bare NeedsReview row NEVER shows a one-click discard.
  canDiscardDup(d: DocRow): boolean {
    return d.state === 'DuplicateSuspect' || (d.state === 'NeedsReview' && !!d.duplicateOf);
  }
  discardDup(d: DocRow) {
    if (this.busy().has(d.documentId)) return;
    const name = cleanName(d.original || d.documentId);
    if (!window.confirm(`„${name}" als Duplikat verwerfen?\nDas Dokument wird nicht verarbeitet; der Vorgang wird im Audit-Log festgehalten.`)) return;
    this.execute(d, this.api.duplicateDiscard(d.documentId));
  }

  // Restore a discarded document (discard-recovery). Visibility is SERVER-decided (d.undiscardable — computed
  // with the same UndiscardGuard the POST runs), so the button never shows when the recovery window lapsed or
  // the artifacts are gone. The free-text WHY is required — an empty prompt aborts, nothing is sent.
  undiscard(d: DocRow) {
    if (this.busy().has(d.documentId)) return;
    const name = cleanName(d.original || d.documentId);
    const reason = window.prompt(`„${name}" wiederherstellen?\nBitte den Grund angeben (wird im Audit-Log verknüpft):`);
    if (!reason || !reason.trim()) return;
    this.execute(d, this.api.undiscard(d.documentId, reason.trim()));
  }

  // Reclassify a filed doc: the server removes the SharePoint item (or notes it's already gone) and re-runs the
  // pipeline with forced review, so the corrected type/metadata are human-confirmed before re-filing.
  reclassify(d: DocRow) {
    if (this.busy().has(d.documentId)) return;
    const name = cleanName(d.original || d.documentId);
    if (!window.confirm(`„${name}" wird neu verarbeitet und landet zur Bestätigung in der Prüfung.\nDer bisherige SharePoint-Eintrag wird dabei entfernt (falls noch vorhanden). Fortfahren?`)) return;
    this.execute(d, this.api.reprocess(d.documentId));
  }

  private execute(d: DocRow, op: Observable<unknown>) {
    if (this.busy().has(d.documentId)) return;
    this.busy.update((s) => new Set(s).add(d.documentId));
    this.actionErrors.update((m) => { const n = new Map(m); n.delete(d.documentId); return n; });
    const done = () => {
      this.busy.update((s) => { const n = new Set(s); n.delete(d.documentId); return n; });
      this.refresh();
    };
    op.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: done,
      error: (e: { error?: unknown }) => {
        const msg = typeof e?.error === 'string' && e.error ? e.error : 'Aktion fehlgeschlagen.';
        this.actionErrors.update((m) => new Map(m).set(d.documentId, msg));
        done();
      },
    });
  }
}
