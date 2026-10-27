import { Component, input } from '@angular/core';

/**
 * Shared outline icon set (replaces the emoji/unicode glyphs the app used as icons).
 * One 24×24 stroke grid, `currentColor`, so an icon inherits its parent's text color and
 * scales with the `size` input (~14 inline, ~16 in headers). Decorative by construction:
 * the host <svg> is aria-hidden, so an icon NEVER supplies the accessible name — the
 * labelling belongs on the button/link around it (aria-label / visible text).
 *
 * No dependency (the reference's lucide set isn't installable here); these are equivalent
 * standard geometric outlines authored in-house.
 */
@Component({
  selector: 'app-icon',
  template: `
    <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 24 24" fill="none"
         stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"
         aria-hidden="true" focusable="false">
      @switch (name()) {
        @case ('scissors') { <circle cx="6" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><line x1="20" y1="4" x2="8.12" y2="15.88"/><line x1="14.47" y1="14.48" x2="20" y2="20"/><line x1="8.12" y1="8.12" x2="12" y2="12"/> }
        @case ('rotate-cw') { <path d="M21 12a9 9 0 1 1-3-6.7"/><polyline points="21 3 21 9 15 9"/> }
        @case ('rotate-ccw') { <path d="M3 12a9 9 0 1 0 3-6.7"/><polyline points="3 3 3 9 9 9"/> }
        @case ('refresh') { <polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10"/><path d="M20.49 15a9 9 0 0 1-14.85 3.36L1 14"/> }
        @case ('undo') { <path d="M3 7v6h6"/><path d="M21 17a9 9 0 0 0-9-9 9 9 0 0 0-6 2.3L3 13"/> }
        @case ('x') { <line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/> }
        @case ('check') { <polyline points="20 6 9 17 4 12"/> }
        @case ('plus') { <line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/> }
        @case ('minus') { <line x1="5" y1="12" x2="19" y2="12"/> }
        @case ('maximize') { <path d="M8 3H5a2 2 0 0 0-2 2v3"/><path d="M21 8V5a2 2 0 0 0-2-2h-3"/><path d="M3 16v3a2 2 0 0 0 2 2h3"/><path d="M16 21h3a2 2 0 0 0 2-2v-3"/> }
        @case ('minimize') { <path d="M8 3v3a2 2 0 0 1-2 2H3"/><path d="M21 8h-3a2 2 0 0 1-2-2V3"/><path d="M3 16h3a2 2 0 0 1 2 2v3"/><path d="M16 21v-3a2 2 0 0 1 2-2h3"/> }
        @case ('chevron-left') { <polyline points="15 18 9 12 15 6"/> }
        @case ('chevron-right') { <polyline points="9 18 15 12 9 6"/> }
        @case ('chevron-down') { <polyline points="6 9 12 15 18 9"/> }
        @case ('arrow-left') { <line x1="19" y1="12" x2="5" y2="12"/><polyline points="12 19 5 12 12 5"/> }
        @case ('arrow-right') { <line x1="5" y1="12" x2="19" y2="12"/><polyline points="12 5 19 12 12 19"/> }
        @case ('upload') { <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/> }
        @case ('download') { <line x1="12" y1="4" x2="12" y2="16"/><polyline points="7 11 12 16 17 11"/><line x1="5" y1="20" x2="19" y2="20"/> }
        @case ('file-text') { <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/><line x1="10" y1="9" x2="8" y2="9"/> }
        @case ('files') { <path d="M15 2H9a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2V7z"/><polyline points="15 2 15 7 20 7"/><path d="M4 6v14a2 2 0 0 0 2 2h9"/> }
        @case ('copy') { <rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/> }
        @case ('columns') { <rect x="3" y="3" width="18" height="18" rx="2"/><line x1="12" y1="3" x2="12" y2="21"/> }
        @case ('lock') { <rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/> }
        @case ('edit') { <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.12 2.12 0 0 1 3 3L12 15l-4 1 1-4z"/> }
        @case ('search') { <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/> }
        @case ('info') { <circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/> }
        @case ('alert-triangle') { <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/> }
        @case ('link') { <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/> }
        @case ('loader') { <line x1="12" y1="2" x2="12" y2="6"/><line x1="12" y1="18" x2="12" y2="22"/><line x1="4.93" y1="4.93" x2="7.76" y2="7.76"/><line x1="16.24" y1="16.24" x2="19.07" y2="19.07"/><line x1="2" y1="12" x2="6" y2="12"/><line x1="18" y1="12" x2="22" y2="12"/><line x1="4.93" y1="19.07" x2="7.76" y2="16.24"/><line x1="16.24" y1="7.76" x2="19.07" y2="4.93"/> }
      }
    </svg>
  `,
})
export class Icon {
  /** Icon name — see the @switch cases above for the available set. */
  name = input.required<string>();
  /** Edge length in px (~14 inline, ~16 in headers). */
  size = input(14);
}
