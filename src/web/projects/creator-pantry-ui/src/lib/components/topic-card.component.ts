import { booleanAttribute, ChangeDetectionStrategy, Component, ElementRef, computed, input, viewChild } from '@angular/core';

import { CpCardComponent } from './card.component';

/** The level of a topic card's heading: 2 under a page head, 3 inside something that already has one. */
export type CpTopicCardHeadingLevel = 2 | 3;

let nextId = 0;

/**
 * One topic on a page about a single thing: a heading, what there is to read about it, and what can be done
 * about it underneath.
 *
 * **Why a component.** DAM's detail page is a column of these — the picture, about it, where it came from,
 * its versions — each a `cp-card` holding a `.block`, and every other detail page had drawn its own. This is
 * that block as built: a display heading, a body held to a reading measure, and the actions at the bottom.
 *
 * **Reading measure by default.** A card stretches to the frame, and a paragraph that does too is a line
 * nobody can read. The body is capped at `--cp-measure-prose`; `wide` lifts the cap for a table or a preview,
 * which need the room a paragraph should not be given.
 *
 * **It names its own region.** The section is labelled by its heading, so a screen reader's region list reads
 * as the page's table of contents. `headingId` lets a page point something else at the same heading — a
 * scrolling table named by its card's title — and is generated when not given.
 *
 * **What it does not own.** Anything in the body, and which actions there are. The actions slot only places
 * them: last, in a row that wraps, and absent when nothing is projected.
 */
@Component({
  selector: 'cp-topic-card',
  standalone: true,
  imports: [CpCardComponent],
  host: { '[class.cp-topic-card--wide]': 'wide()' },
  template: `<cp-card>
    <section [attr.aria-labelledby]="resolvedHeadingId()">
      @if (headingLevel() === 3) {
        <h3 #title class="heading" tabindex="-1" [id]="resolvedHeadingId()">{{ heading() }}</h3>
      } @else {
        <h2 #title class="heading" tabindex="-1" [id]="resolvedHeadingId()">{{ heading() }}</h2>
      }
      <ng-content />
      <div class="actions"><ng-content select="[cpTopicCardActions]" /></div>
    </section>
  </cp-card>`,
  styles: [
    `
      :host {
        display: block;
      }
      section {
        display: grid;
        /* minmax(0, …) so a table or one long word scrolls or wraps inside the card instead of widening it. */
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-3);
        max-width: var(--cp-measure-prose);
      }
      :host(.cp-topic-card--wide) section {
        max-width: none;
      }
      .heading {
        margin: 0;
        font: 400 var(--cp-font-size-xl) / 1.2 var(--cp-font-display);
        color: var(--cp-text);
        overflow-wrap: anywhere;
      }
      /* A destination, never a tab stop. The ring shows on :focus, not only :focus-visible: a flow moves focus
         here by script after a click on Continue, and a browser does not count that as keyboard focus. */
      .heading:focus {
        outline: 2px solid var(--cp-focus);
        outline-offset: 4px;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--cp-space-3);
      }
      .actions:not(:has(> *)) {
        display: none;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpTopicCardComponent {
  /** What this card is about. It heads the card and names its region. */
  readonly heading = input.required<string>();
  /** 2 under a page head; 3 when the card sits inside something that already has a heading. */
  readonly headingLevel = input<CpTopicCardHeadingLevel>(2);
  /** Lifts the reading measure, for a table or a preview. A bare `wide` attribute is enough. */
  readonly wide = input(false, { transform: booleanAttribute });
  /** The heading's id, for a page that labels something else by it. Generated when empty. */
  readonly headingId = input('');

  private readonly generatedId = `cp-topic-card-${nextId++}`;
  protected readonly resolvedHeadingId = computed(() => this.headingId() || this.generatedId);

  private readonly title = viewChild.required<ElementRef<HTMLElement>>('title');

  /**
   * Moves focus to the heading, for a card that has just replaced the one the creator was working in — the
   * next step of a flow. The heading is focusable by script and is never a tab stop.
   */
  focusHeading(): void {
    this.title().nativeElement.focus();
  }
}
