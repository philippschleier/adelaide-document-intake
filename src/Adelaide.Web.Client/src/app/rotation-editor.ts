import { signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Api } from './api';

/**
 * Owns a document's orientation as an OPTIMISTIC, self-persisting value — the correctness-critical core shared by every
 * surface that can rotate (review, split, the preview modal). Extracted, not duplicated, on purpose: the revert-on-failure
 * and the whenSettled() submit guard are exactly what prevent the silent-failure bug this whole feature exists to fix, and
 * a drifted second copy would fail silently in one surface only.
 *
 * The parent OWNS an instance (the pane stays presentational): seed() it from the payload's savedRotation, bind the pane's
 * [rotate] to angle(), call rotate(id) from the pill's (rotateClick), and — before any submit that BAKES the angle — await
 * whenSettled(). Orientation is an objective document fact, so it saves per click (no debounce); saving is safe to
 * auto-apply ONLY because "which way is up" is objectively correct-or-not — never generalise this to type/patient.
 */
export class RotationEditor {
  /** The angle to DISPLAY (0/90/180/270) — optimistic: it jumps on rotate(), before the save lands. Bind [rotate] to this. */
  readonly angle = signal(0);
  /** Set when the most recent save FAILED (after the view already reverted); the surface shows it near the pill. Null otherwise. */
  readonly error = signal<string | null>(null);

  private confirmed = 0; // last angle the server acknowledged — the revert target on a failed save
  private seq = 0;       // out-of-order guard: only the LATEST save may revert / set confirmed
  private pending: Promise<void> | null = null;

  constructor(private readonly api: Api) {}

  /** Seed from the server's saved angle (on load / when the doc changes). Both the display and the revert target start here. */
  seed(angle: number): void {
    this.angle.set(angle);
    this.confirmed = angle;
    this.error.set(null);
  }

  /** Rotate one quarter-turn clockwise: bump the display immediately, then persist (absolute PUT, so last-write-wins
   *  converges even if two are briefly in flight). On the LATEST save failing, snap back to the confirmed angle + set error. */
  rotate(id: string): void {
    const next = (this.angle() + 90) % 360;
    const my = ++this.seq;
    this.angle.set(next);
    this.error.set(null);
    this.pending = firstValueFrom(this.api.saveRotation(id, next))
      .then(() => { if (my === this.seq) this.confirmed = next; })
      .catch(() => {
        if (my !== this.seq) return;    // a newer rotate supersedes this one — its result wins, don't fight it
        this.angle.set(this.confirmed); // snap back to the last angle the server acknowledged
        this.error.set('Drehung konnte nicht gespeichert werden.');
      });
  }

  /** Resolve once any in-flight save has settled. A submit that bakes the saved angle MUST await this before POSTing, or a
   *  fast rotate-then-file reads a stale sidecar and bakes an orientation nobody chose. Always resolves (a failed save has
   *  already reverted the view + surfaced the error), so callers await without a try/catch. */
  whenSettled(): Promise<void> { return this.pending ?? Promise.resolve(); }
}
