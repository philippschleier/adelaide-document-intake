import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { cleanName } from '../text-safe';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../api';
import { CompareData } from '../models';
import { PdfPane } from '../pdf-pane/pdf-pane';
import { Icon } from '../ui/icon';

/** Side-by-side duplicate comparison: both documents shown as scrollable server-rendered page images (the shared
 *  pdf-pane), plus an optional server-rendered pixel diff (matches faded gray, differences red). For an exact-duplicate
 *  suspect the reviewer decides here: discard as duplicate, or process anyway. */
@Component({
  selector: 'app-compare',
  imports: [RouterLink, PdfPane, Icon],
  templateUrl: './compare.html',
  styleUrl: './compare.css',
})
export class Compare implements OnInit {
  private api = inject(Api);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private destroyRef = inject(DestroyRef);

  id = signal('');
  other = signal('');
  info = signal<CompareData | null>(null);
  loading = signal(true);
  error = signal<string | null>(null);

  showDiff = signal(false);
  page = signal(1);
  diffNote = signal<string | null>(null);

  acting = signal(false);
  actionError = signal<string | null>(null);

  diffSrc = computed(() => this.api.diffUrl(this.id(), this.other(), this.page()));

  // Each side renders at its own saved orientation (the pane's [rotate]); the client passes it explicitly so the URL is
  // deterministic. A filed side reports 0 — its /Rotate is already baked into the filed artifact.
  aRotation = computed(() => this.info()?.a.savedRotation ?? 0);
  bRotation = computed(() => this.info()?.b.savedRotation ?? 0);

  // "Trotzdem verarbeiten" applies only to an unresolved exact-byte suspect (duplicate-process accepts only that state)
  isSuspect = computed(() => this.info()?.a.state === 'DuplicateSuspect');
  fromReview = computed(() => this.info()?.a.state === 'NeedsReview');
  // Discard covers BOTH duplicate layers — exact-byte suspects AND business-key flags (NeedsReview + duplicateOf).
  // Mirrors the endpoint guard exactly; a bare NeedsReview (low_confidence, unclassified, …) NEVER shows discard.
  canDiscard = computed(() => {
    const a = this.info()?.a;
    return !!a && (a.state === 'DuplicateSuspect' || (a.state === 'NeedsReview' && !!a.duplicateOf));
  });
  // Supersede (the new doc REPLACES the existing one): NARROWER than discard — only a business-key flag (NeedsReview +
  // duplicateOf; NEVER a DuplicateSuspect, whose identical bytes can't be a "better scan"), and only when the target (b)
  // is Filed AND id-bearing. A Filed-but-legacy target (no driveItemId) shows the action disabled with a backfill hint.
  private supersedeBase = computed(() => {
    const d = this.info();
    return !!d && d.a.state === 'NeedsReview' && !!d.a.duplicateOf && d.b.state === 'Filed';
  });
  canSupersede = computed(() => this.supersedeBase() && this.info()!.b.hasLocator === true);
  supersedeBlockedLegacy = computed(() => this.supersedeBase() && this.info()!.b.hasLocator !== true);

  ngOnInit() {
    this.id.set(this.route.snapshot.paramMap.get('id') ?? '');
    this.other.set(this.route.snapshot.paramMap.get('other') ?? '');
    // Each pane fetches its own page count + page images lazily (available=false short-circuits to the placeholder).
    this.api.compare(this.id(), this.other()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (d) => {
        this.info.set(d);
        this.loading.set(false);
      },
      error: () => { this.error.set('Vergleichsdaten nicht gefunden.'); this.loading.set(false); },
    });
  }

  toggleDiff() { this.showDiff.update((v) => !v); this.diffNote.set(null); }
  nextPage() { this.page.update((p) => p + 1); this.diffNote.set(null); }
  prevPage() { if (this.page() > 1) { this.page.update((p) => p - 1); this.diffNote.set(null); } }
  onDiffError() {
    if (this.page() > 1) { this.page.update((p) => p - 1); this.diffNote.set('Keine weitere Seite.'); }
    else { this.diffNote.set('Pixel-Diff nicht verfügbar.'); this.showDiff.set(false); }
  }

  discard() {
    const name = cleanName(this.info()?.a.original || this.id());
    if (!window.confirm(`„${name}" als Duplikat verwerfen?\nDas Dokument wird nicht verarbeitet; der Vorgang wird im Audit-Log festgehalten.`)) return;
    this.run(this.api.duplicateDiscard(this.id()));
  }
  processAnyway() {
    this.run(this.api.duplicateProcess(this.id()));
  }
  // Supersede is a HAND-OFF into the review screen in supersede-mode (supersede = a review-submit with a flag; the
  // reviewer confirms type/patient/fields there, then the single window.confirm names both fates). NOT executed here.
  supersede() {
    this.router.navigate(['/review', this.id()], { queryParams: { supersede: this.other() } });
  }
  private run(op: ReturnType<Api['duplicateDiscard']>) {
    if (this.acting()) return;
    this.acting.set(true);
    this.actionError.set(null);
    op.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.router.navigate(['/']),
      error: (e: { error?: unknown }) => {
        this.acting.set(false);
        this.actionError.set(typeof e?.error === 'string' && e.error ? e.error : 'Aktion fehlgeschlagen.');
      },
    });
  }
}
