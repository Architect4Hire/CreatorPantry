import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import { CpCardComponent } from './card.component';

/** The heading level a card's title sits at: one below whatever heads the list the card is in. */
export type CpMediaCardHeadingLevel = 2 | 3 | 4;

/**
 * One thing in a library, shown by its picture: the picture, a linked title, a line of description, badges,
 * and whatever facts the page hangs under them.
 *
 * **Why a component.** DAM draws a library as a grid of these and the other three libraries drew theirs as
 * text rows. This is DAM's `.asset` as built, so a recipe, a prompt and a brand example can be shown the same
 * way without each stylesheet redrawing the card.
 *
 * **The picture is projected.** A picture in this product is authorised and loaded by the feature that owns
 * it, and that must not move into the library — so this never takes a URL. When nothing is projected it shows
 * a tinted panel with a glyph at the same shape, so a grid of cards with and without pictures keeps even
 * rows. The glyph is decoration and is hidden from a screen reader; the title already says what the card is.
 *
 * **The title is the link, not the card.** The consumer projects its own `<a>`, which keeps `routerLink` in
 * the feature and gives a screen reader one link named by the title rather than a card-sized one named by
 * everything in it. Its ring shows on `:focus`, not only `:focus-visible`: a list that grows under "Load more"
 * moves focus to the first new title by script, and a browser does not count that as keyboard focus when the
 * button was clicked with a pointer.
 *
 * **Actions come last, and are few.** `[cpMediaCardActions]` holds what can be done to the thing from the list
 * without opening it — a preview — under everything that says what it is. It takes no room when empty, which
 * is the usual case: a card whose only action is opening the thing has its title for that.
 *
 * **What it does not own.** What the thing is, where the link goes, which facts matter, or what an action does.
 */
@Component({
  selector: 'cp-media-card',
  standalone: true,
  imports: [NgTemplateOutlet, CpCardComponent],
  template: `<cp-card>
    <article>
      <div class="picture"><ng-content select="[cpMediaCardPicture]" /></div>
      <div class="panel" aria-hidden="true">{{ glyph() }}</div>

      <!-- One slot, three possible elements: a template, because a slot repeated per branch only fills one. -->
      <ng-template #title><ng-content select="[cpMediaCardTitle]" /></ng-template>
      @switch (headingLevel()) {
        @case (2) {
          <h2 class="title"><ng-container [ngTemplateOutlet]="title" /></h2>
        }
        @case (4) {
          <h4 class="title"><ng-container [ngTemplateOutlet]="title" /></h4>
        }
        @default {
          <h3 class="title"><ng-container [ngTemplateOutlet]="title" /></h3>
        }
      }

      @if (description()) {
        <p class="description">{{ description() }}</p>
      }

      <div class="badges"><ng-content select="[cpMediaCardBadges]" /></div>

      <ng-content />

      <div class="actions"><ng-content select="[cpMediaCardActions]" /></div>
    </article>
  </cp-card>`,
  styles: [
    `
      /* Full height, so every card in a grid row is as tall as the tallest. */
      :host {
        display: block;
        height: 100%;
      }
      cp-card {
        height: 100%;
      }
      article {
        display: grid;
        /* minmax(0, …) so one long value inside the card cannot hold the column wider than the card. */
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-2);
      }
      .panel {
        display: grid;
        place-items: center;
        aspect-ratio: 4 / 3;
        border-radius: var(--cp-radius-md);
        background: var(--cp-surface-subtle);
        color: var(--cp-text-faint);
        font-size: var(--cp-font-size-2xl);
        line-height: 1;
      }
      .title {
        margin: 0;
        font: 600 var(--cp-font-size-lg) / var(--cp-leading-tight) var(--cp-font-sans);
        color: var(--cp-text);
        overflow-wrap: anywhere;
      }
      /* The title holds the consumer's link and nothing else, so a descendant selector costs nothing here. */
      :host ::ng-deep .title a {
        color: inherit;
        text-decoration: none;
      }
      :host ::ng-deep .title a:hover {
        color: var(--cp-primary);
        text-decoration: underline;
      }
      :host ::ng-deep .title a:focus {
        outline: 2px solid var(--cp-focus);
        outline-offset: 2px;
      }
      .description {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
        overflow-wrap: anywhere;
      }
      .badges {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
        margin-top: var(--cp-space-1);
      }
      /* The panel stands in for a picture; with one projected it goes, and with none the empty slot does.
         Last on purpose: :not(:has(> *)) adds no specificity, so only order puts these above the rules that
         give each element its display. */
      .picture:has(> *) + .panel,
      .picture:not(:has(> *)),
      .badges:not(:has(> *)),
      .actions:not(:has(> *)) {
        display: none;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpMediaCardComponent {
  /** A line or two under the title. Plain text; nothing is shown when it is empty. */
  readonly description = input('');
  /** What the tinted panel shows when no picture is projected. Decoration, never announced. */
  readonly glyph = input('▣');
  /** The level of the title's heading: one below the heading of the list this card is in. */
  readonly headingLevel = input<CpMediaCardHeadingLevel>(3);
}
