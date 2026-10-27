import { Component, HostListener, effect, inject, input, output, signal } from '@angular/core';
import { PdfPane } from '../pdf-pane/pdf-pane';
import { Icon } from '../ui/icon';
import { Api } from '../api';
import { PreviewDoc } from '../models';
import { RotationEditor } from '../rotation-editor';

/** Modal PDF viewer — the dashboard's "Original ansehen" quick-look. A centered window over a dimmed full-screen scrim,
 *  with an enlarge/restore toggle. Parent binds [doc] (null hides it) and handles (close). The shell opens INSTANTLY;
 *  the pane inside owns the content states (skeleton → server-rendered page images, or a designed not-found/failed
 *  message). Rotation: for an in-review doc (doc.canRotate) the pill's rotate button persists the orientation via the
 *  SAME shared RotationEditor as review — the modal OWNS the angle (pane stays presentational), seeds it from the
 *  dashboard row, and emits (rotated) so the row stays consistent on a close-and-reopen. Filed docs open at their baked
 *  angle, non-rotatable. */
@Component({
  selector: 'app-pdf-viewer',
  imports: [PdfPane, Icon],
  templateUrl: './pdf-viewer.html',
  styleUrl: './pdf-viewer.css',
})
export class PdfViewer {
  private api = inject(Api);

  doc = input<PreviewDoc | null>(null);
  close = output<void>();
  /** Emits the new angle after a rotate so the dashboard can patch its cached row — otherwise a close-and-reopen would
   *  seed from the stale row and briefly show the old angle (a smaller version of the very propagation bug this fixes). */
  rotated = output<number>();
  max = signal(false);

  // The orientation lives here (parent-owned); the pane just renders [rotate]="roter.angle()".
  readonly roter = new RotationEditor(this.api);

  constructor() {
    // Whenever the target doc changes: (re)open windowed AND seed the orientation from that row's savedRotation.
    effect(() => { const d = this.doc(); this.max.set(false); this.roter.seed(d?.savedRotation ?? 0); });
  }

  // Pill rotate → optimistic bump + persist, then tell the dashboard the new (optimistic) angle so its row stays current.
  onRotate() {
    const d = this.doc();
    if (!d) return;
    this.roter.rotate(d.id);
    this.rotated.emit(this.roter.angle());
  }

  toggleMax() { this.max.update((v) => !v); }

  onBackdrop(e: MouseEvent) {
    if ((e.target as HTMLElement).classList.contains('modal')) this.close.emit();
  }

  @HostListener('document:keydown.escape')
  onEsc() { if (this.doc()) this.close.emit(); }
}
