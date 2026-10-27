import { Component, input } from '@angular/core';
import { Icon } from './icon';

/**
 * Chip: a small rounded, tinted label. Tones (from the contract):
 *   accent  — links / attachments (blue tint)
 *   neutral — plain tags (gray tint)
 *   locked  — immutable / retired records (muted, pair with icon="lock")
 * Content is projected, so the caller supplies the text; `icon` optionally
 * prepends an outline glyph. Display-only — no behavior.
 */
@Component({
  selector: 'app-chip',
  imports: [Icon],
  template: `
    <span class="ui-chip ui-chip--{{ tone() }}">
      @if (icon()) { <app-icon [name]="icon()" [size]="11" /> }
      <ng-content />
    </span>
  `,
})
export class Chip {
  tone = input<'neutral' | 'accent' | 'locked'>('neutral');
  /** Optional leading icon name (see app-icon). */
  icon = input<string>('');
}
