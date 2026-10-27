import { Component, DestroyRef, ElementRef, computed, effect, inject, input, output, signal, untracked, viewChild, viewChildren } from '@angular/core';
import { Api } from '../api';
import { Icon } from '../ui/icon';

/**
 * The shared document-viewing surface: a scrollable column of SERVER-RASTERIZED page images (GET /api/page) — one
 * rendering path for review, split, compare, the duplicate overlay and the enlarge modal. No PDF engine runs in the
 * reviewer's browser (the input is scanned images anyway) and orientation comes from a SINGLE `rot` path, so the
 * surfaces can't disagree (the old "upside down in split" bug came from review's native iframe vs split's page images).
 *
 * States are COMMUNICATED — a bare grey rectangle is impossible:
 *   loading  — one A4-portrait skeleton while the page count resolves
 *   ready    — the page column; each page has its OWN skeleton→image cross-fade and per-page error+retry
 *   notfound — designed message (confirmed 404, or the caller's [missing] pre-check)
 *   failed   — designed message + retry (the page-count request failed)
 * Pages render LAZILY (only near the viewport) so a 30-page split stack doesn't rasterize all at once. Exposes
 * (pageChange) + scrollToPage() so split's rail can jump the viewer and highlight the page you're scrolling through.
 */
@Component({
  selector: 'app-pdf-pane',
  imports: [Icon],
  templateUrl: './pdf-pane.html',
  styleUrl: './pdf-pane.css',
})
export class PdfPane {
  private api = inject(Api);

  docId = input.required<string>();
  /** Pre-checked absent (compare's available=false): straight to the not-found message, no fetch attempt. */
  missing = input(false);
  notFoundText = input('Dokument nicht gefunden — evtl. gelöscht.');
  /** Document-wide absolute orientation (0/90/180/270) to render EVERY page at. The PARENT owns it (via a RotationEditor)
   *  and passes the doc's savedRotation — optimistically bumped on the surfaces that can rotate; the pane is presentational
   *  and just renders at this angle. Always a number (0 = upright), so the image URLs stay deterministic. Changing it
   *  re-requests images only — it does NOT re-fetch the page count. */
  rotate = input(0);
  /** Optional: the caller already knows the page count (review has filePageCount) → skip the /api/pagecount round-trip. */
  pageCountHint = input<number | null>(null);
  /** Whether to render the rotate button in the viewer pill. Rotation is a REVIEW-flow action that persists to the filed
   *  record, so only the review/split/modal surfaces set this true and handle (rotateClick); the pane never owns rotation
   *  state. Default false → no rotate button (compare + the duplicate overlay get zoom only). */
  canRotate = input(false);

  /** The page currently at the top of the viewport as the user scrolls — drives split's rail highlight. */
  pageChange = output<number>();
  /** The pill's rotate button was pressed (or `R` upstream) — the parent advances its own document-wide rotation. */
  rotateClick = output<void>();

  state = signal<'loading' | 'ready' | 'notfound' | 'failed'>('loading');
  pages = signal<number[]>([]);            // 1..N once ready
  private activated = signal<Set<number>>(new Set()); // pages whose <img> src is live (lazy)
  private loaded = signal<Set<number>>(new Set());     // pages whose image finished loading (cross-fade in)
  private errored = signal<Set<number>>(new Set());    // pages whose image failed (per-page retry)
  private pageAspect = signal<Map<number, string>>(new Map()); // real aspect-ratio per page, pinned once loaded — keeps the
                                                               // slot height stable when a page is released/reactivated
                                                               // (no scroll jump) and matches rotated (sideways) pages

  private scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private slots = viewChildren<ElementRef<HTMLElement>>('pgSlot');

  private seq = 0;                  // stale-load guard: docId can change under an in-flight page-count fetch
  private current = 0;              // last emitted current page (plain field → no feedback loop with pageChange)
  private destroyRef = inject(DestroyRef);
  private detachWheel?: () => void; // teardown for the manual non-passive ctrl+wheel listener
  private readonly dpr = (typeof window !== 'undefined' && window.devicePixelRatio) || 1;

  // ---- Zoom (the retired native viewer's zoom, rebuilt) ----
  zoom = signal(1);
  private readonly ZOOM_STEPS = [0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];
  zoomPct = computed(() => Math.round(this.zoom() * 100));
  canZoomIn = computed(() => this.zoom() < this.ZOOM_STEPS[this.ZOOM_STEPS.length - 1]);
  canZoomOut = computed(() => this.zoom() > this.ZOOM_STEPS[0]);
  // Source render width tracks zoom so zooming IN re-renders at higher DPI (real detail, like the native viewer) rather
  // than upscaling pixels; quantised to 200px so a small nudge reuses the cached render, never below the 1× baseline
  // (zoom-OUT just downscales in display), capped at the server max. Reading it re-requests the visible pages' images.
  private reqWidth = computed(() => {
    const raw = 1000 * this.dpr * Math.max(1, this.zoom());
    return Math.min(2400, Math.max(500, Math.round(raw / 200) * 200));
  });

  constructor() {
    // Page-count load keys ONLY on docId/missing/hint — NOT rotate (a rotation re-requests images, same page count).
    effect(() => {
      const id = this.docId();
      const miss = this.missing();
      const hint = this.pageCountHint();
      untracked(() => void this.load(id, miss, hint));
    });
    // Ctrl+wheel zoom (the expected PDF-viewer gesture). A manual NON-PASSIVE listener so preventDefault stops the
    // browser's own page zoom — Angular's (wheel) binding may be passive. Re-attached if the scroller element changes.
    effect(() => {
      const el = this.scroller()?.nativeElement;
      untracked(() => {
        this.detachWheel?.();
        if (!el) return;
        const onWheel = (e: WheelEvent) => {
          if (!e.ctrlKey) return; // plain scroll → normal vertical scrolling
          e.preventDefault();
          if (e.deltaY < 0) this.zoomIn(); else if (e.deltaY > 0) this.zoomOut();
        };
        el.addEventListener('wheel', onWheel, { passive: false });
        this.detachWheel = () => el.removeEventListener('wheel', onWheel);
      });
    });
    this.destroyRef.onDestroy(() => this.detachWheel?.());
  }

  // Document-wide orientation: every page renders at the same `rotate()` angle (0 = upright). Reads rotate() reactively,
  // so a parent's optimistic rotate re-requests images at the new angle immediately.
  pageSrc(p: number): string {
    return this.api.pageThumbUrl(this.docId(), p, this.reqWidth(), this.rotate());
  }

  isActive(p: number): boolean { return this.activated().has(p); }
  isLoaded(p: number): boolean { return this.loaded().has(p); }
  isErrored(p: number): boolean { return this.errored().has(p); }
  aspectFor(p: number): string | null { return this.pageAspect().get(p) ?? null; }

  onImgLoad(p: number, img: HTMLImageElement): void {
    if (img.naturalWidth > 0 && img.naturalHeight > 0) // pin the real aspect so the slot never resizes on release/reload
      this.pageAspect.update((m) => new Map(m).set(p, `${img.naturalWidth} / ${img.naturalHeight}`));
    this.loaded.update((s) => new Set(s).add(p));
  }
  onImgError(p: number): void { this.errored.update((s) => new Set(s).add(p)); }
  retryPage(p: number): void {
    // Drop from loaded+errored and force a fresh <img> by nudging a cache-busting flag is unnecessary — clearing
    // errored re-renders the <img>, whose src is unchanged but re-requested by the browser on element re-creation.
    this.errored.update((s) => { const n = new Set(s); n.delete(p); return n; });
    this.loaded.update((s) => { const n = new Set(s); n.delete(p); return n; });
    this.activated.update((s) => new Set(s).add(p));
  }

  retry(): void { void this.load(this.docId(), this.missing(), this.pageCountHint()); }

  // Direct measure on each scroll event (no rAF throttle): measure only WRITES signals when the sliding window actually
  // shifts, so most events are a cheap read-only loop, and it must not depend on requestAnimationFrame (which is throttled
  // to ~never when the tab isn't compositing — e.g. a background/preview view — which would freeze lazy loading).
  onScroll(): void { const el = this.scroller()?.nativeElement; if (el) this.measure(el); }

  /** Jump the viewer to a page (split rail tile click). Pre-activates a window around the target so it isn't blank on
   *  arrival; a FAR jump scrolls instantly (a smooth animation across dozens of pages would churn the sliding window). */
  scrollToPage(page: number): void {
    const sc = this.scroller()?.nativeElement;
    const slot = this.slots()[page - 1]?.nativeElement;
    if (!sc || !slot) return;
    this.activateRange(page - 2, page + 3);
    const far = Math.abs(page - this.current) > 4;
    sc.scrollTo({ top: slot.offsetTop, behavior: far ? 'auto' : 'smooth' });
  }

  zoomIn(): void { const n = this.ZOOM_STEPS.find((z) => z > this.zoom() + 1e-3); if (n != null) this.setZoom(n); }
  zoomOut(): void { const p = [...this.ZOOM_STEPS].reverse().find((z) => z < this.zoom() - 1e-3); if (p != null) this.setZoom(p); }
  resetZoom(): void { this.setZoom(1); }

  // Change zoom while keeping the current page ANCHORED: capture how far we're scrolled into the dominant page, then
  // restore that after the column rescales (the aspect-ratio boxes' height scales with the new width) and the src tier
  // swaps. setTimeout (not rAF — dead in a non-compositing view) lets Angular apply the new --zoom before we re-read offsets.
  private setZoom(z: number): void {
    const sc = this.scroller()?.nativeElement;
    const anchor = this.current || 1;
    const a0 = this.slots()[anchor - 1]?.nativeElement;
    const frac = sc && a0 && a0.offsetHeight > 0 ? (sc.scrollTop - a0.offsetTop) / a0.offsetHeight : 0;
    this.zoom.set(z);
    setTimeout(() => {
      const el = this.scroller()?.nativeElement;
      const a = this.slots()[anchor - 1]?.nativeElement;
      if (el && a) { el.scrollTop = a.offsetTop + frac * a.offsetHeight; this.measure(el); }
    });
  }

  private async load(id: string, miss: boolean, hint: number | null): Promise<void> {
    const my = ++this.seq;
    this.pages.set([]);
    this.activated.set(new Set());
    this.loaded.set(new Set());
    this.errored.set(new Set());
    this.pageAspect.set(new Map());
    this.zoom.set(1); // a new document opens at fit (100%)
    this.current = 0;
    if (miss || !id) { this.state.set(miss ? 'notfound' : 'loading'); return; }

    if (hint && hint > 0) { this.ready(hint); return; } // caller knows the count → no round-trip

    this.state.set('loading');
    try {
      const res = await fetch(this.api.pageCountUrl(id), { headers: { Accept: 'application/json' } });
      if (my !== this.seq) return; // superseded
      if (res.status === 404) { this.state.set('notfound'); return; }
      if (!res.ok) { this.state.set('failed'); return; }
      const body = (await res.json()) as { pages?: number };
      if (my !== this.seq) return;
      this.ready(Math.max(1, body.pages ?? 1));
    } catch {
      if (my === this.seq) this.state.set('failed');
    }
  }

  private ready(n: number): void {
    this.pages.set(Array.from({ length: n }, (_, i) => i + 1));
    this.state.set('ready');
    this.scheduleMeasure(); // activate the first visible pages once the column is laid out
  }

  // Activate the first visible pages after the column renders. Polls until the scroller actually HAS height and slots —
  // on a split mode-switch the flex layout sizes the pane a few frames late, so a one-shot measure would run at 0-height
  // and activate nothing (leaving the viewport blank until the first scroll). setTimeout is used deliberately: this pane
  // must work where IntersectionObserver/ResizeObserver don't (non-compositing preview) — scroll + timers always run.
  private scheduleMeasure(): void {
    const my = this.seq;
    let tries = 0;
    const tick = () => {
      if (my !== this.seq) return; // a newer load superseded this one
      const el = this.scroller()?.nativeElement;
      if (el && el.clientHeight > 0 && this.slots().length) {
        this.measure(el);
        if (tries++ < 2) setTimeout(tick, 120); // two more passes catch late flex sizing / first-image reflow
      } else if (tries++ < 25) {
        setTimeout(tick, 40); // pane not laid out yet (mode-switch) — wait for it
      }
    };
    tick();
  }

  // Dominant-page scroll-spy + sliding-window lazy loading in one pass. Current page = the one covering the MOST of the
  // viewport, so the active thumbnail switches when the incoming page crosses ~50% of the viewport (not when its top
  // merely reaches the top edge). Around that page a sliding WINDOW activates a few pages ahead (prefetch) + a couple
  // behind and RELEASES pages far outside it — so a 50+ page document never floods the browser with requests or holds
  // every decoded image in memory (a released page re-renders cheaply on scroll-back; the server caches the source bytes,
  // and each page's aspect-ratio is pinned once loaded so releasing/reactivating never shifts the scroll position).
  private measure(el: HTMLElement): void {
    const slots = this.slots();
    if (!slots.length) return;
    const top = el.scrollTop;
    const bottom = top + el.clientHeight;

    // dominant page = greatest visible height within the viewport (strict > keeps the lower page on ties → switch just past 50%)
    let cur = 1;
    let best = -1;
    for (let i = 0; i < slots.length; i++) {
      const n = slots[i].nativeElement;
      const o = n.offsetTop;
      const vis = Math.min(o + n.offsetHeight, bottom) - Math.max(o, top);
      if (vis > best) { best = vis; cur = i + 1; }
    }

    // sliding window (page numbers around cur): activate [cur-2, cur+4]; release beyond [cur-4, cur+8]. The gap between
    // the two bounds is hysteresis so a page hovering at the edge doesn't thrash activate↔release.
    const act = new Set(this.activated());
    const load = new Set(this.loaded());
    let aChanged = false, lChanged = false;
    for (let p = 1; p <= slots.length; p++) {
      if (p >= cur - 2 && p <= cur + 4) {
        if (!act.has(p)) { act.add(p); aChanged = true; }
      } else if (p < cur - 4 || p > cur + 8) {
        if (act.delete(p)) aChanged = true;
        if (load.delete(p)) lChanged = true; // release the decoded image (re-renders cheaply if scrolled back)
      }
    }
    if (aChanged) this.activated.set(act);
    if (lChanged) this.loaded.set(load);
    if (cur !== this.current) { this.current = cur; this.pageChange.emit(cur); }
  }

  private activateRange(fromIdx: number, toIdx: number): void {
    const n = this.pages().length;
    const add = new Set(this.activated());
    let changed = false;
    for (let i = Math.max(0, fromIdx); i <= Math.min(n - 1, toIdx); i++) {
      if (!add.has(i + 1)) { add.add(i + 1); changed = true; }
    }
    if (changed) this.activated.set(add);
  }
}
