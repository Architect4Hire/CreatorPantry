import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * What a notice is about. The same four words `CpStatusPillComponent` uses for the equivalent states, so a
 * notice and a pill describing one situation cannot disagree about what to call it.
 */
export type CpNoticeTone = 'neutral' | 'success' | 'warning' | 'error';

const GLYPHS: Readonly<Record<CpNoticeTone, string>> = {
  neutral: 'ℹ',
  success: '✓',
  warning: '!',
  error: '!',
};

/**
 * One sentence the product has to say about the thing on screen: saved, refused, shelved, still working.
 *
 * **Why a component.** `RecipeEditorComponent` worked this shape out first as a `.banner` family — a soft tone
 * fill, a glyph, small text, an optional inline action — and every screen written after it either copied those
 * rules or drifted from them. Four stylesheets had their own by the time this was written.
 *
 * **Tone never carries the meaning on its own.** Each tone brings a glyph as well as a fill, because a creator
 * who cannot tell two hues apart still has to be able to tell "saved" from "could not save". A consumer may
 * replace the glyph (an archived recipe flies a flag) but not switch it off; what it must not do is rely on the
 * fill alone, which is why the glyph is not optional.
 *
 * **Liveness belongs to the consumer.** A notice announces nothing by itself: the consumer puts `role="status"`
 * or `role="alert"` on the element, because only the consumer knows whether this sentence interrupts. A live
 * region also has to be in the DOM *before* it has text to be announced reliably — hence {@link quiet}, which
 * keeps an always-present region invisible until it has something to say.
 *
 * **What it does not own.** Its own text, its own actions, and when it is showing. Content is projected, so an
 * inline `Reload latest` button or a link to a copy sits in the flow of the sentence it belongs to.
 */
@Component({
  selector: 'cp-notice',
  standalone: true,
  template: `@if (!quiet()) {
      <span class="glyph" aria-hidden="true">{{ resolvedGlyph() }}</span>
    }
    <span class="body"><ng-content /></span>`,
  styles: [
    `
      :host {
        display: flex;
        gap: var(--cp-space-3);
        /* Baseline, not centre: a one-line notice reads as centred anyway, and a three-line one keeps its
           glyph beside the first line instead of halfway down the paragraph. */
        align-items: baseline;
        margin: 0;
        padding: var(--cp-space-2) var(--cp-space-3);
        border-radius: var(--cp-radius-md);
        background: var(--cp-surface-subtle);
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
        line-height: var(--cp-leading-normal);
      }
      .glyph {
        flex: 0 0 auto;
        font-weight: 700;
      }
      /* A block in a flex row, so projected text and an inline button wrap inside it rather than pushing the
         row wider than its container. */
      .body {
        flex: 1 1 auto;
        min-width: 0;
      }
      :host(.cp-notice--success) {
        background: var(--cp-primary-soft);
        color: var(--cp-text);
      }
      :host(.cp-notice--success) .glyph {
        color: var(--cp-primary);
      }
      :host(.cp-notice--warning) {
        background: var(--cp-orange-soft);
        color: var(--cp-orange);
      }
      :host(.cp-notice--error) {
        background: var(--cp-danger-soft);
        color: var(--cp-danger);
      }
      /* An always-present live region with nothing to announce yet: no fill, no padding, no glyph, so an empty
         one leaves no mark on the layout it is waiting in. */
      :host(.cp-notice--quiet) {
        padding: 0;
        background: none;
      }
    `,
  ],
  host: {
    '[class.cp-notice--success]': "tone() === 'success' && !quiet()",
    '[class.cp-notice--warning]': "tone() === 'warning' && !quiet()",
    '[class.cp-notice--error]': "tone() === 'error' && !quiet()",
    '[class.cp-notice--quiet]': 'quiet()',
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpNoticeComponent {
  readonly tone = input<CpNoticeTone>('neutral');

  /**
   * Hold the space without drawing anything. For a live region that must exist before it has text: the fill,
   * the padding and the glyph all go, and what is left is the projected content.
   */
  readonly quiet = input(false);

  /** A different glyph for the same tone — a flag for something shelved. Never blank: see the class remarks. */
  readonly glyph = input('');

  protected readonly resolvedGlyph = computed(() => this.glyph() || GLYPHS[this.tone()]);
}
