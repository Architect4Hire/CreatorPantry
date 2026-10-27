import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { CpButtonComponent } from './button.component';
import { CpCardComponent } from './card.component';
import { CpEnterDirective } from './enter.directive';

/** One key fact about an option: a term and its value, read as a pair ("Time: 45 min"). */
export interface CpOptionFact {
  readonly label: string;
  readonly value: string;
}

/** The heading level an option's name sits at: one below whatever heads the group it is in. */
export type CpOptionCardHeadingLevel = 2 | 3 | 4;

/** How many facts a card shows as tags before the rest move into its disclosure. */
export const CP_OPTION_CARD_TAG_LIMIT = 3;

let nextId = 0;

/**
 * One choice among several, compared side by side with the others (AU.3.2, APP-007): what it is called, up to three
 * key facts as tags, a sentence about it, the rest on request, and the one button that chooses it.
 *
 * **Key facts first, detail on request.** A model's concepts and variants arrived as a column of labelled
 * paragraphs, read top to bottom one card at a time. Here the facts the feature picks sit under the name as tags,
 * so three cards in a `ul[cpCardGrid]` can be compared across, and everything else is behind a native
 * `<details>`. A fact past the third is never dropped: it moves into the disclosure, so nothing is cut without a
 * way to read it. Content projected into the default slot is always shown — a caution about the option is never
 * put behind the disclosure.
 *
 * **Choosing is an action, not a value.** This is not a form control: no value accessor, no radio, no arrow keys
 * between cards. The button says what choosing does and emits `chosen`; the feature decides what happens, and
 * which card is `selected`. A value a Save submits is `CpChoiceGroupComponent`'s, not this.
 *
 * **Selected, three ways.** A 2px edge in the action colour — the colour of a picked `cp-choice-group` tile's — a ✓, and the
 * word "Chosen" beside the name, never colour alone, and no tone: selection is a state, not the tool. The button
 * then reads "Chosen", stays focusable with `aria-disabled`, and emits nothing, so focus is not thrown somewhere
 * else at the moment the creator acted.
 *
 * **Named for its card.** Every card has a button with the same words, so each is named by its words and the
 * option's name together ("Choose this concept Smoky miso corn"): a screen reader listing the buttons never meets
 * three identical ones.
 */
@Component({
  selector: 'cp-option-card',
  standalone: true,
  imports: [CpButtonComponent, CpCardComponent],
  // The cards of a group land one stagger step apart, in the order they were created (AU.3.4). Choosing one
  // changes `selected` and not the element, so nothing arrives a second time.
  hostDirectives: [CpEnterDirective],
  template: `<cp-card [class.selected]="selected()">
    <article [attr.aria-labelledby]="headingId">
      @switch (headingLevel()) {
        @case (2) {
          <h2 class="heading" [id]="headingId">{{ heading() }}</h2>
        }
        @case (4) {
          <h4 class="heading" [id]="headingId">{{ heading() }}</h4>
        }
        @default {
          <h3 class="heading" [id]="headingId">{{ heading() }}</h3>
        }
      }

      @if (selected()) {
        <p class="chosen"><span class="glyph" aria-hidden="true">✓</span> Chosen</p>
      }

      @if (tags().length > 0) {
        <dl class="tags">
          @for (fact of tags(); track $index) {
            <div class="tag">
              <dt>{{ fact.label }}</dt>
              <dd>{{ fact.value }}</dd>
            </div>
          }
        </dl>
      }

      @if (summary()) {
        <p class="summary">{{ summary() }}</p>
      }

      <div class="always"><ng-content /></div>

      <details class="cp-disclosure detail" [class.empty]="overflow().length === 0">
        <!-- Named with the option too, so a grid of them never offers three identical disclosures. -->
        <summary [attr.aria-labelledby]="detailTextId + ' ' + headingId"
          ><span [id]="detailTextId">{{ detailLabel() }}</span></summary
        >
        @if (overflow().length > 0) {
          <dl class="more">
            @for (fact of overflow(); track $index) {
              <div>
                <dt>{{ fact.label }}</dt>
                <dd>{{ fact.value }}</dd>
              </div>
            }
          </dl>
        }
        <div class="projected"><ng-content select="[cpOptionCardDetail]" /></div>
      </details>

      <div class="choose">
        <button
          cpButton
          variant="secondary"
          size="sm"
          type="button"
          [attr.aria-labelledby]="buttonTextId + ' ' + headingId"
          [attr.aria-disabled]="selected() ? 'true' : null"
          (click)="choose()"
        >
          <!-- The glyph outside the words the name is built from, so the name is only ever words. -->
          @if (selected()) {
            <span aria-hidden="true">✓&nbsp;</span>
          }
          <span [id]="buttonTextId">{{ selected() ? 'Chosen' : chooseLabel() }}</span>
        </button>
      </div>
    </article>
  </cp-card>`,
  styles: [
    `
      /* Full height, so every card in a grid row is as tall as the tallest and the buttons line up. */
      :host {
        display: block;
        height: 100%;
      }
      cp-card {
        height: 100%;
      }
      /* The border and an outline over it make a 2px edge that follows the card's corners. An outline, not a
         shadow, so forced colours keep the difference. */
      cp-card.selected {
        border-color: var(--cp-primary-text);
        outline: 1px solid var(--cp-primary-text);
      }
      article {
        display: flex;
        flex-direction: column;
        gap: var(--cp-space-3);
        height: 100%;
      }
      .heading {
        margin: 0;
        font: 600 var(--cp-font-size-lg) / var(--cp-leading-tight) var(--cp-font-sans);
        color: var(--cp-text);
        overflow-wrap: anywhere;
      }
      .chosen {
        margin: 0;
        color: var(--cp-primary-text);
        font-size: var(--cp-font-size-sm);
        font-weight: 700;
      }
      dl,
      dd {
        margin: 0;
      }
      .tags {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
      }
      .tag {
        display: inline-flex;
        flex-wrap: wrap;
        gap: var(--cp-space-1);
        max-width: 100%;
        padding: var(--cp-space-1) var(--cp-space-2);
        border-radius: var(--cp-radius-sm);
        background: var(--cp-surface-subtle);
        font-size: var(--cp-font-size-sm);
        line-height: var(--cp-leading-tight);
      }
      .tag dt {
        color: var(--cp-text-muted);
        overflow-wrap: anywhere;
      }
      .tag dd {
        color: var(--cp-text);
        font-weight: 600;
        overflow-wrap: anywhere;
      }
      .summary {
        margin: 0;
        color: var(--cp-text);
        overflow-wrap: anywhere;
      }
      .detail {
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
      .more {
        display: grid;
        gap: var(--cp-space-2);
        margin-block: var(--cp-space-2);
      }
      .more dt {
        font-weight: 700;
        color: var(--cp-text);
      }
      .more dt,
      .more dd {
        overflow-wrap: anywhere;
      }
      /* Pushed to the foot of the card, so the buttons of a row of cards line up. */
      .choose {
        margin-top: auto;
      }
      /* The chosen button says so at full strength. cpButton fades an aria-disabled button to half, which would
         fade "Chosen" below 4.5:1 and its focus ring with it, on the one control left focusable for this. */
      .choose button[aria-disabled='true'] {
        opacity: 1;
        cursor: default;
      }
      /* :empty, not :has(> *), for what must always be seen: a caution projected as bare text has no element to
         find, and :empty still ignores the comments an @if leaves behind. The disclosure goes only when it would
         open on nothing at all. */
      .always:empty,
      .detail.empty:not(:has(.projected > *)) {
        display: none;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpOptionCardComponent {
  /** The option's own name. */
  readonly heading = input.required<string>();
  /** The level of the name's heading: one below the heading of the group this card is in. */
  readonly headingLevel = input<CpOptionCardHeadingLevel>(3);
  /** Key facts, in the order the feature wants them compared. The first three are tags; the rest are in the disclosure. */
  readonly facts = input<readonly CpOptionFact[]>([]);
  /** One sentence about the option. Plain text; nothing is shown when it is empty. */
  readonly summary = input('');
  /** The disclosure's summary line. */
  readonly detailLabel = input('More about this option');
  /** The button's words: what choosing does. */
  readonly chooseLabel = input('Choose this');
  /** Whether this is the option chosen. One at a time is the feature's to keep. */
  readonly selected = input(false);

  /** The creator chose this option. Not emitted while it is already the one chosen. */
  readonly chosen = output<void>();

  protected readonly headingId = `cp-option-card-${nextId++}`;
  protected readonly buttonTextId = `${this.headingId}-choose`;
  protected readonly detailTextId = `${this.headingId}-detail`;
  protected readonly tags = computed(() => this.facts().slice(0, CP_OPTION_CARD_TAG_LIMIT));
  protected readonly overflow = computed(() => this.facts().slice(CP_OPTION_CARD_TAG_LIMIT));

  protected choose(): void {
    if (!this.selected()) this.chosen.emit();
  }
}
