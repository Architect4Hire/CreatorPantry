import { booleanAttribute, ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * A set of chips that answer one question: which statuses, whose recipes.
 *
 * **Why a component.** The recipe library and the Brand library each drew the same chips and the same wrapping
 * row for them. This is that row: an attribute on the consumer's own element, so it can also carry
 * `cpToolbarFilters` and sit in a toolbar.
 *
 * **It is a group, and it is named.** The chips are read out as one set, so `label` is required: "Filter by
 * status". Whether one chip may be pressed or several is the consumer's rule — this holds no selection.
 */
@Component({
  selector: '[cpChipGroup]',
  standalone: true,
  template: '<ng-content />',
  host: { role: 'group', '[attr.aria-label]': 'label()' },
  styles: [
    `
      :host {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
        min-width: 0;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpChipGroupComponent {
  /** What the chips in this group choose between, for a screen reader. Not shown. */
  readonly label = input.required<string>();
}

/**
 * One chip: a toggle the creator can see the state of, on the consumer's own `<button>`.
 *
 * **Pressed is said three ways.** `aria-pressed` for a screen reader, and for the eye a border, a fill and a
 * heavier label — so the state survives both themes and a display with no colour.
 *
 * **What it does not own.** What pressing it means. The consumer handles `(click)` and sets `pressed`, which
 * is how the same chip serves a filter where several may be on and one where exactly one is.
 */
@Component({
  selector: 'button[cpChip]',
  standalone: true,
  template: '<ng-content />',
  host: { type: 'button', '[attr.aria-pressed]': 'pressed()' },
  styles: [
    `
      :host {
        /* 40px, the minimum target. */
        min-height: 2.5rem;
        padding: 0 var(--cp-space-3);
        border: 1px solid var(--cp-border);
        border-radius: var(--cp-radius-full);
        background: var(--cp-surface);
        color: var(--cp-text-muted);
        font: inherit;
        cursor: pointer;
      }
      /* --cp-primary, not --cp-primary-ink: the ink token is for text on a solid primary fill, and on the
         soft background the label would disappear at the moment it is selected. This is the pairing
         cp-status-pill's success tone already uses with the same background. */
      :host([aria-pressed='true']) {
        border-color: var(--cp-primary);
        background: var(--cp-primary-soft);
        color: var(--cp-primary-text);
        font-weight: 600;
      }
      :host(:focus-visible) {
        outline: 2px solid var(--cp-focus);
        outline-offset: 2px;
      }
      :host(:disabled) {
        cursor: not-allowed;
        opacity: 0.6;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpChipComponent {
  /** Whether this chip is on. A bare `pressed` attribute is enough. */
  readonly pressed = input(false, { transform: booleanAttribute });
}
