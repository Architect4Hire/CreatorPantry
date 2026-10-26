import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Where a step stands. `skipped` is a step the creator chose to pass over, which is not the same as done. */
export type CpStepStatus = 'upcoming' | 'current' | 'done' | 'skipped';

/** One step of a flow, as the stepper shows it. */
export interface CpStep {
  /** Stable for the life of the list; what a row is tracked by. */
  readonly id: string;
  /** The step's name, in the flow's own words. */
  readonly label: string;
  readonly status: CpStepStatus;
}

/** A state is said in a word as well as a glyph and a fill, so it survives a display with no colour. */
const WORD: Readonly<Record<CpStepStatus, string>> = {
  upcoming: 'Not started',
  current: 'You are here',
  done: 'Done',
  skipped: 'Skipped',
};

const GLYPH: Readonly<Record<CpStepStatus, string>> = {
  upcoming: '○',
  current: '●',
  done: '✓',
  skipped: '↷',
};

/**
 * The steps of a flow, in order, saying where the creator is in it.
 *
 * **Why a component.** The content pipeline and Brand setup each drew this list, with the same words and
 * two different sets of glyphs. This is that list, once.
 *
 * **A read-out, not navigation.** A row is not a link and takes no click: a flow moves by its own Back and
 * Continue, which know what has to be saved first and what the step still needs. So there is nothing here to
 * tab to, and no output.
 *
 * **Every state is said three ways.** A glyph, a word under the step's name, and for the current step a
 * border and a fill. The glyph is decoration and is hidden from a screen reader; the word is what is read.
 * The current row also carries `aria-current="step"`.
 *
 * **What it does not own.** The landmark it sits in, or whether it is shown at all at a narrow width: the
 * consumer wraps it in its own `<nav>` or disclosure, beside its own progress bar. It knows nothing about what
 * a step is, what makes one reachable, or how a flow is saved.
 */
@Component({
  selector: 'cp-stepper',
  standalone: true,
  template: `@if (steps().length > 0) {
    <ol [attr.aria-label]="label()">
      @for (step of steps(); track step.id) {
        <li [attr.aria-current]="step.status === 'current' ? 'step' : null" [attr.data-status]="step.status">
          <span class="glyph" aria-hidden="true">{{ glyph[step.status] }}</span>
          <span class="name">{{ step.label }}</span>
          <span class="word">{{ word[step.status] }}</span>
        </li>
      }
    </ol>
  }`,
  styles: [
    `
      :host {
        display: block;
      }
      ol {
        display: grid;
        gap: var(--cp-space-2);
        margin: 0;
        padding: 0;
        list-style: none;
      }
      li {
        display: grid;
        grid-template-columns: 1.5rem minmax(0, 1fr);
        column-gap: var(--cp-space-2);
        padding: var(--cp-space-2) var(--cp-space-3);
        border: 1px solid transparent;
        border-radius: var(--cp-radius-md);
        color: var(--cp-text-muted);
        overflow-wrap: anywhere;
      }
      /* The tool's tone, as the flow's progress (DESIGN-SYSTEM.md, "Warmth inside the app"): the primary's four on
         an untoned page. The glyph is the tone as text, so a light accent still reads beside the word. */
      li[aria-current='step'] {
        border-color: var(--cp-tone);
        background: var(--cp-tone-soft);
        color: var(--cp-text);
        font-weight: 700;
      }
      li[data-status='done'] .glyph {
        color: var(--cp-tone-text);
      }
      .glyph {
        grid-row: span 2;
        line-height: var(--cp-leading-normal);
      }
      .word {
        grid-column: 2;
        font-size: var(--cp-font-size-xs);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpStepperComponent {
  /** What these are the steps of, for a screen reader: "Setup steps". Not shown. */
  readonly label = input.required<string>();
  /** The steps, in the order the flow takes them. Nothing is rendered for an empty list. */
  readonly steps = input.required<readonly CpStep[]>();

  protected readonly word = WORD;
  protected readonly glyph = GLYPH;
}
