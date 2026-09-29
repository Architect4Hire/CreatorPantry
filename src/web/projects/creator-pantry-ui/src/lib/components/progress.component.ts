import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * How full the track reads. Presentation only — the tone carries no meaning the consumer's own text does not
 * already say, because a bar that changed colour and nothing else would be status by colour alone (WCAG 2.2
 * AA, 1.4.1). Pair it with a `cp-status-pill`, whose glyph and words are the real carrier.
 */
/**
 * `success` and `default` deliberately paint the same green: a healthy meter and an untoned one look alike,
 * and the pair exists so a consumer can say which it means rather than leaving the reader to infer it from
 * an absence. Naming the intent is the point; a fourth colour would not be.
 */
export type CpProgressTone = 'default' | 'success' | 'warning' | 'error';

@Component({
  selector: 'cp-progress',
  standalone: true,
  template: `<div class="meta">
      <span>{{ label() }}</span>
      <strong>{{ displayValue() }}</strong>
    </div>
    <div
      class="track"
      role="progressbar"
      [attr.aria-label]="label()"
      [attr.aria-valuenow]="normalized()"
      [attr.aria-valuetext]="valueText() || null"
      aria-valuemin="0"
      aria-valuemax="100"
    >
      <span [style.width.%]="normalized()"></span>
    </div>`,
  host: { '[attr.data-tone]': 'tone()' },
  styles: [
    `:host{display:grid;gap:.4rem}
    .meta{display:flex;justify-content:space-between;gap:var(--cp-space-2);color:var(--cp-text-muted);font-size:var(--cp-font-size-xs)}
    .track{height:.4rem;border-radius:var(--cp-radius-full);overflow:hidden;background:var(--cp-surface-subtle)}
    .track span{display:block;height:100%;border-radius:inherit;background:var(--cp-progress-fill);transition:width var(--cp-duration-normal) var(--cp-ease)}
    /* A component-local custom property, deliberately named for this component rather than sitting in the
       shared token namespace: it is plumbing for the rules below, not a semantic intent other components
       should reach for. A missing intent belongs in both themes; this is not one. */
    :host{--cp-progress-fill:var(--cp-primary)}
    :host([data-tone='success']){--cp-progress-fill:var(--cp-primary)}
    :host([data-tone='warning']){--cp-progress-fill:var(--cp-orange)}
    :host([data-tone='error']){--cp-progress-fill:var(--cp-danger)}`,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpProgressComponent {
  readonly value = input(0);

  readonly label = input('Progress');

  /**
   * The figure to print and announce instead of a percentage, e.g. `640 credits left`.
   *
   * A percentage is the right reading for "how far through this is" and the wrong one for "how much have I
   * got" — a creator with an allowance wants the amount, not the fraction. Empty keeps the percentage, which
   * is what every existing caller gets.
   */
  readonly valueText = input('');

  /** @see CpProgressTone — never the only carrier of a state. */
  readonly tone = input<CpProgressTone>('default');

  readonly normalized = computed(() => Math.min(100, Math.max(0, this.value())));

  protected readonly displayValue = computed(() => this.valueText() || `${this.value()}%`);
}
