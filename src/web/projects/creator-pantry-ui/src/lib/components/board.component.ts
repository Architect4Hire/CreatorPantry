import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

let nextId = 0;

/** The level of a column's heading: 2 directly under a page head, 3 inside something that already has one. */
export type CpBoardColumnHeadingLevel = 2 | 3;

/**
 * A library whose things are sorted into a few named, ordered columns: a board.
 *
 * An attribute on the consumer's own element, like `ul[cpCardGrid]`, and for the same reason: what the columns
 * are, how many there are and what goes in them belong to the feature. It lays its `cp-board-column`
 * children side by side in the order they are written, and gives each the same width.
 *
 * **Columns wrap; the page never scrolls sideways.** As many columns as fit sit in a row, and the rest move
 * below — four across on a wide page, two by two as it narrows, one above the other at a phone's width or at
 * 200% zoom (SC 1.4.10). `min(<floor>, 100%)` rather than the floor alone, as `CpCardGridComponent` explains.
 * `auto-fit`, unlike the card grid's `auto-fill`: a board's columns are a fixed, small set and should share the
 * whole width rather than leave an empty track beside them.
 *
 * **What it does not own.** Moving a thing between columns, by pointer or by keyboard: that is the feature's,
 * with whatever it uses to do it. Nothing here listens for a drag.
 */
@Component({
  selector: '[cpBoard]',
  standalone: true,
  template: '<ng-content />',
  host: { '[style.--cp-board-column-min]': 'minColumn()' },
  styles: [
    `
      :host {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(min(var(--cp-board-column-min, 15rem), 100%), 1fr));
        gap: var(--cp-space-4);
        align-items: start;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpBoardComponent {
  /** The narrowest a column may get before the board moves one below. */
  readonly minColumn = input('15rem');
}

/**
 * One column of a board: its name, how many things are in it, the things, and a footer.
 *
 * **It names its own region.** The section is labelled by its heading, so a screen reader's region list reads
 * as the board's columns in order, and "3" beside "To do" is announced as part of that name's description
 * rather than as a stray number: the count carries its own words (`countLabel`), which a sighted reader does
 * not need and a listener does.
 *
 * **The list is the consumer's.** The default slot takes the feature's own `<ul>` — it is what a drag library
 * attaches to, and what decides the order — and this draws no item. Direct `ul` and `ol` children lose their
 * list chrome and are spaced; each keeps `role="list"`, which `list-style: none` takes away in Safari, only if
 * the consumer sets it.
 *
 * **Empty is said by the consumer too.** A column with nothing in it still shows its heading and a count of
 * zero; the sentence that says why — nothing here yet, or nothing matches the filters — is the feature's to
 * project, because only it knows which.
 *
 * `[cpBoardColumnFooter]` holds what acts on the column as a whole — "Load more" — and takes no room when
 * empty.
 */
@Component({
  selector: 'cp-board-column',
  standalone: true,
  template: `<section [attr.aria-labelledby]="headingId" [attr.aria-busy]="busy() ? 'true' : null">
    <div class="head">
      @if (headingLevel() === 3) {
        <h3 class="heading" [id]="headingId">{{ heading() }}</h3>
      } @else {
        <h2 class="heading" [id]="headingId">{{ heading() }}</h2>
      }
      @if (count() !== null) {
        <span class="count">
          <span aria-hidden="true">{{ count() }}</span>
          <span class="sr-only">{{ spokenCount() }}</span>
        </span>
      }
    </div>

    <div class="body"><ng-content /></div>

    <div class="footer"><ng-content select="[cpBoardColumnFooter]" /></div>
  </section>`,
  styles: [
    `
      :host {
        display: block;
        /* A grid item's automatic minimum is its content's width; one long title must not widen the column. */
        min-width: 0;
      }
      section {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-3);
        padding: var(--cp-space-3);
        border: 1px solid var(--cp-border);
        border-radius: var(--cp-radius-lg);
        background: var(--cp-surface-subtle);
      }
      .head {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: var(--cp-space-2);
      }
      .heading {
        margin: 0;
        font: 600 var(--cp-font-size-lg) / var(--cp-leading-tight) var(--cp-font-display);
        color: var(--cp-text);
        overflow-wrap: anywhere;
      }
      .count {
        flex: none;
        min-width: 1.75rem;
        padding: 0 var(--cp-space-2);
        border: 1px solid var(--cp-border);
        border-radius: var(--cp-radius-full);
        background: var(--cp-surface);
        color: var(--cp-text-muted);
        font: 600 var(--cp-font-size-sm) / 1.6 var(--cp-font-sans);
        text-align: center;
      }
      /* Hidden here rather than with a global class, so the part does not depend on a consumer's stylesheet. */
      .sr-only {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip-path: inset(50%);
        white-space: nowrap;
      }
      .body {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-3);
        /* Room to drop into a column that holds nothing. */
        min-height: var(--cp-space-8);
        /* A word longer than the column breaks rather than pushing the board wider than the page. */
        overflow-wrap: anywhere;
      }
      :host ::ng-deep .body > ul,
      :host ::ng-deep .body > ol {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-3);
        margin: 0;
        padding: 0;
        list-style: none;
        min-height: var(--cp-space-8);
      }
      :host ::ng-deep .body > ul > li,
      :host ::ng-deep .body > ol > li {
        min-width: 0;
      }
      .footer {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--cp-space-2);
      }
      .footer:not(:has(> *)) {
        display: none;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpBoardColumnComponent {
  /** The column's name. Names the region. */
  readonly heading = input.required<string>();
  /** How many things the column holds, across everything not yet loaded too. Null shows no count. */
  readonly count = input<number | null>(null);
  /** The noun for one thing and for several, so the count is read as "3 cards" and not "3". */
  readonly countLabel = input<readonly [string, string]>(['item', 'items']);
  /** True while the column's contents are being read or changed, so assistive technology can wait. */
  readonly busy = input(false);
  readonly headingLevel = input<CpBoardColumnHeadingLevel>(2);

  protected readonly headingId = `cp-board-column-${nextId++}`;

  protected readonly spokenCount = computed(() => {
    const count = this.count() ?? 0;
    const [one, many] = this.countLabel();

    return `${count} ${count === 1 ? one : many}`;
  });
}
