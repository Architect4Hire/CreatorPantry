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
 */
@Component({
  selector: 'ul[cpCardGrid]',
  standalone: true,
  template: '<ng-content />',
  host: {
    // `list-style: none` takes the list role away in Safari; a grid of cards is still a list of them.
    role: 'list',
    '[style.--cp-card-grid-min]': 'minColumn()',
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
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpCardGridComponent {
  /** The narrowest a card may get before the grid drops a column. */
  readonly minColumn = input('16rem');
}
