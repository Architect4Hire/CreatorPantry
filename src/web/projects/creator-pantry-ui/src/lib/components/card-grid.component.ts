import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * A list of cards laid out as a grid: as many columns as fit, and one at a narrow width.
 *
 * An attribute on the consumer's own `<ul>`, so the list stays a list and each `<li>` stays the consumer's —
 * the cards, what they hold and how many there are belong to the feature. This is DAM's `.grid` as built.
 *
 * `min(<floor>, 100%)` rather than the floor alone, for the reason `CpFieldRowComponent` gives: a track whose
 * floor is a fixed length cannot shrink below it, so at 320px — or at 200% zoom, which narrows the container
 * the same way — the grid would hold itself wider than the page and push it sideways (SC 1.4.10).
 *
 * `auto-fill`, not `auto-fit`: a library with two cards in it keeps them card-sized instead of stretching each
 * across half the page.
 *
 * **`flow="masonry"` lets cards keep their own heights.** A grid's rows are as tall as the tallest card in
 * them, which is right for a library of like things — a row of pictures, a row of figures — and wrong for a
 * set of topics that are each as long as they need to be, where it leaves a short card with a gap under it.
 * Masonry lays the same list out in as many columns as fit and lets each card end where it ends, so the
 * columns pack. It is columns, so the eye reads down a column and then across; the DOM order is unchanged,
 * which is what a screen reader and the Tab key follow, so the order of the list is still the order it was
 * written in. Use it where the cards are independent of one another and their order is a ranking rather than
 * a sequence to be read in turn — the dashboard's topics. A library of cards stays a grid.
 */
@Component({
  selector: 'ul[cpCardGrid]',
  standalone: true,
  template: '<ng-content />',
  host: {
    // `list-style: none` takes the list role away in Safari; a grid of cards is still a list of them.
    role: 'list',
    '[style.--cp-card-grid-min]': 'minColumn()',
    '[attr.data-flow]': 'flow() === "masonry" ? "masonry" : null',
  },
  styles: [
    `
      :host {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(min(var(--cp-card-grid-min, 16rem), 100%), 1fr));
        gap: var(--cp-space-4);
        margin: 0;
        padding: 0;
        list-style: none;
      }

      /* A grid item's automatic minimum is its content's width, so one long unbroken word in a card would
         hold its column wider than the track. The grid holds list items and nothing else, so a child selector
         costs nothing here. */
      :host ::ng-deep > li {
        min-width: 0;
      }

      /* Masonry: columns rather than tracks, so a card ends where it ends and the next one starts there.
         The floor is read as a column width, which fits as many columns as it can — the same answer
         auto-fill gives — and falls to one column in a container narrower than the floor, without
         overflowing it. */
      :host([data-flow='masonry']) {
        display: block;
        columns: var(--cp-card-grid-min, 16rem);
        column-gap: var(--cp-space-4);
      }

      /* A card is never split down the middle across a column break, and carries the row gap itself,
         because a column layout has no row gap of its own. */
      :host([data-flow='masonry']) ::ng-deep > li {
        margin-bottom: var(--cp-space-4);
        break-inside: avoid;
      }

      /* The last card in each column would otherwise hang the gap below the list. */
      :host([data-flow='masonry']) ::ng-deep > li:last-child {
        margin-bottom: 0;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpCardGridComponent {
  /** The narrowest a card may get before the grid drops a column. */
  readonly minColumn = input('16rem');

  /**
   * `grid` (the default) gives every card in a row the height of the tallest; `masonry` lets each keep its
   * own and packs the columns. Masonry reads down a column before it reads across.
   */
  readonly flow = input<'grid' | 'masonry'>('grid');
}
