import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { CpEnterDirective } from './enter.directive';

/** What a model made: words to read, a picture, or a reading of something the creator supplied. */
export type CpGeneratedKind = 'words' | 'picture' | 'reading';

/**
 * The label each kind carries (AU.3.1). Fixed here, not passed in, so the product says one thing for one fact: the
 * features had four ways of saying it — "Written for you", "Written by AI", "AI generated", "Generated" — before
 * this. "Drafted", because words a model wrote are a draft until the creator accepts them.
 */
export const CP_GENERATED_LABELS: Readonly<Record<CpGeneratedKind, string>> = {
  words: 'Drafted by AI',
  picture: 'Made by AI',
  reading: 'Read by AI',
};

let nextId = 0;

/**
 * The mark that says a model made what sits beside it: a glyph and the words, never a colour alone.
 *
 * **Why alone as well as in `cp-generated`.** Most generated output already sits in something with its own edge — a
 * post's card, a section of a long-form piece, the field holding alt text — and wrapping that in a second surface
 * would draw a box in a box. There, this goes where the old badge went.
 *
 * **Neutral, on purpose.** It takes no tone. Purple on every page would put a second tone on four toned tools and
 * make a tone mean "made by AI", which DESIGN-SYSTEM.md, "Warmth inside the app", says no tone does. The dashed edge
 * says "draft" by its shape, and the words say it outright.
 *
 * **Provenance is the feature's text.** What made it and when, composed by the feature from what the server
 * returned — "Venice, flux-dev", "gpt-4o · 25 Oct, 08:30" — and left out, not guessed, when the server returned
 * none. Never a prompt body: that is the creator's material and is not repeated here (.claude/rules/ai.md).
 *
 * **Origin, not quality.** The words say a model made it and nothing about whether it is right. Any caution — check
 * the picture shows the dish, check the alt text against the picture — is the feature's sentence beside it.
 *
 * **What it does not own.** When it shows. The feature takes it away the moment the creator's own words replace
 * the model's, or the output is accepted.
 */
@Component({
  selector: 'cp-generated-label',
  standalone: true,
  template: `<span class="mark"
      ><span class="glyph" aria-hidden="true">✦</span><span class="words" [id]="labelId()">{{ label() }}</span></span
    >
    @if (provenance()) {
      <!-- Heard as "Made by AI, Venice, flux-dev": the pill's edge is the pause a sighted reader sees. -->
      <span class="cp-sr-only">, </span><span class="provenance">{{ provenance() }}</span>
    }`,
  styles: [
    `
      :host {
        display: inline-flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: var(--cp-space-1) var(--cp-space-2);
        font-size: var(--cp-font-size-sm);
        line-height: var(--cp-leading-tight);
      }
      .mark {
        display: inline-flex;
        align-items: baseline;
        gap: var(--cp-space-1);
        padding: 0.125rem var(--cp-space-2);
        border: 1px dashed var(--cp-border-control);
        border-radius: var(--cp-radius-full);
        color: var(--cp-text);
        font-weight: 700;
        white-space: nowrap;
      }
      .provenance {
        color: var(--cp-text-muted);
        overflow-wrap: anywhere;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpGeneratedLabelComponent {
  /** What was made. Chooses the words; there is no free-text label. */
  readonly kind = input<CpGeneratedKind>('words');
  /** What made it and when, as the feature writes it. Nothing is shown when it is empty. */
  readonly provenance = input('');
  /** The id of the words, for a surface that is named by them. */
  readonly labelId = input(`cp-generated-label-${nextId++}`);

  protected readonly label = computed(() => CP_GENERATED_LABELS[this.kind()]);
}

/**
 * One piece of generated output on a surface of its own: the label (and provenance) first, the output, then the
 * creator's decisions about it — accept, edit, discard — in `[cpGeneratedActions]`, which is always last and takes
 * no room when empty.
 *
 * **A group named by its label.** A screen reader entering it hears "Drafted by AI" before the content, so nobody
 * has to infer from styling whether what they are reading is already theirs.
 *
 * **A dashed, neutral edge.** `--cp-border-control`, the edge a creator already reads as "you can change this",
 * dashed because a draft is not finished. No tone and no fill of its own: it sits on any page, toned or not,
 * without saying anything a tone would.
 *
 * **What it does not own.** What the output is, whether it is shown, and what the actions do. It saves, accepts and
 * discards nothing.
 */
@Component({
  selector: 'cp-generated',
  standalone: true,
  imports: [CpGeneratedLabelComponent],
  // The result arrives once, as it is made (AU.3.4). A regeneration is a new surface, so it arrives again.
  hostDirectives: [CpEnterDirective],
  host: {
    role: 'group',
    '[attr.aria-labelledby]': 'labelId',
  },
  template: `<cp-generated-label [kind]="kind()" [provenance]="provenance()" [labelId]="labelId" />
    <div class="content"><ng-content /></div>
    <div class="actions"><ng-content select="[cpGeneratedActions]" /></div>`,
  styles: [
    `
      :host {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-3);
        padding: var(--cp-space-4);
        border: 1px dashed var(--cp-border-control);
        border-radius: var(--cp-radius-lg);
      }
      cp-generated-label {
        justify-self: start;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
      }
      /* Last on purpose: :not(:has(> *)) adds no specificity, so only order puts it above .actions' display. */
      .actions:not(:has(> *)) {
        display: none;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpGeneratedComponent {
  /** What was made. Chooses the label's words. */
  readonly kind = input<CpGeneratedKind>('words');
  /** What made it and when, as the feature writes it. */
  readonly provenance = input('');

  protected readonly labelId = `cp-generated-${nextId++}`;
}
