import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { CpSpinnerComponent } from './spinner.component';

export type CpStatusPillTone = 'neutral' | 'progress' | 'success' | 'warning' | 'error' | 'stale';
const CP_STATUS_PILL_DEFAULT_ICONS: Record<CpStatusPillTone, string> = {
  neutral: '○',
  progress: '◐',
  success: '✓',
  warning: '▲',
  error: '✕',
  stale: '↻',
};
/**
 * A state, in a word, with a glyph and a tone that add nothing the word does not say.
 *
 * **`busy` is a process running now** — a request queued or being worked on, which the page updates on its own. It
 * draws `cp-spinner` in place of the glyph, still under reduced motion, so the one thing on the page that is
 * actually happening is the one that moves. It is opt-in, not the `progress` tone's: "Drafting" on a card is a state
 * the creator's work is in, not something turning over. Leave it off when the page no longer knows the process is
 * running — its checks are failing — rather than spin over a state that may be stale. Like the glyph, it is
 * `aria-hidden`; the word is what is read.
 */
@Component({
  selector: 'cp-status-pill',
  standalone: true,
  imports: [CpSpinnerComponent],
  template:
    '@if (busy()) {<cp-spinner class="icon" />} @else if (!iconHidden()) {<span class="icon" aria-hidden="true">{{ displayIcon() }}</span>}<span class="label"><ng-content /></span>',
  host: { '[attr.data-tone]': 'tone()', '[attr.data-busy]': 'busy() ? "" : null' },
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        gap: var(--cp-space-1);
        min-height: 1.5rem;
        padding: 0 0.55rem;
        border-radius: var(--cp-radius-full);
        font-size: var(--cp-font-size-xs);
        font-weight: 700;
        background: var(--cp-surface-subtle);
        color: var(--cp-text-muted);
      }
      :host([data-tone='progress']) {
        background: var(--cp-blue-soft);
        color: var(--cp-blue);
      }
      :host([data-tone='success']) {
        background: var(--cp-primary-soft);
        color: var(--cp-primary-text);
      }
      :host([data-tone='warning']) {
        background: var(--cp-orange-soft);
        color: var(--cp-orange);
      }
      :host([data-tone='error']) {
        background: var(--cp-danger-soft);
        color: var(--cp-danger);
      }
      :host([data-tone='stale']) {
        background: var(--cp-purple-soft);
        color: var(--cp-purple);
      }
      .icon {
        font-size: 0.85em;
        line-height: 1;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpStatusPillComponent {
  readonly tone = input<CpStatusPillTone>('neutral');
  readonly icon = input<string>();
  readonly iconHidden = input(false);
  /** A process running now: a spinner in place of the glyph. Shown even when `iconHidden` is set. */
  readonly busy = input(false);
  readonly displayIcon = computed(() => this.icon() ?? CP_STATUS_PILL_DEFAULT_ICONS[this.tone()]);
}
