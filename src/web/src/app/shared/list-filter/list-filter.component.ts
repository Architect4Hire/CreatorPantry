import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  Injector,
  input,
  model,
  output,
  viewChild,
} from '@angular/core';
import { CpButtonComponent, CpEmptyStateComponent, CpSearchFieldComponent } from '@creator-pantry/ui';

import { LIST_BATCH_SIZE } from './list-filter';

/**
 * A list the page already holds, framed: a filter box over it, the one sentence saying what the filter found, the
 * list itself in a region that scrolls with a "Load more" at its end, and a line saying how much of it is showing —
 * or, when nothing matched, a "no results" state that clears the filter.
 *
 * Presentation only. The page owns the rows, so it owns what "matches" means and which of them are on show
 * (`matches.slice(0, shown())`). This control draws the frame, grows the count and says where the creator is.
 *
 * - `query` is two-way, so the page filters from the same signal the box types into. Typing, or clearing, goes back
 *   to the first batch.
 * - `total` is how many rows the filter leaves, and `shown` is two-way: how many of them are on show. It starts at
 *   {@link LIST_BATCH_SIZE} and each "Load more" adds `batch` — with a search or without one, because the list
 *   grows the same way whatever it is made of.
 * - The list is projected, and sits in a region of its own that scrolls past a height, so rows never push
 *   everything under them off the screen. "Load more" is the last thing in it, so the rows it adds appear where it
 *   was.
 * - `hasMore` says the server holds rows the page has not read. Asking for more when the rows in hand will not
 *   fill the next batch emits `loadMore`, so the page reads them while the rows it has are shown; they arrive in
 *   `total`. `loadingMore` says they are being read.
 * - `status` is a live region that is always present, so a result is announced when it changes, including nothing.
 * - `noMatch` draws the empty state in the list's place; the page leaves its own list out while it is true.
 *
 * It sits where the list would begin and takes the field measure, so it is as wide as a field is anywhere else.
 */
@Component({
  selector: 'cp-list-filter',
  standalone: true,
  imports: [CpButtonComponent, CpEmptyStateComponent, CpSearchFieldComponent],
  templateUrl: './list-filter.component.html',
  styleUrl: './list-filter.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ListFilterComponent {
  /** What the box searches, announced but not shown: "Search your recipes". */
  readonly label = input.required<string>();
  readonly placeholder = input('');
  /** The input's `name`, for a page with two filters. */
  readonly name = input('search');
  /** What has been typed. */
  readonly query = model('');
  /** One sentence about what the filter found — "4 of 8 match." — or empty while nothing is typed. */
  readonly status = input('');
  /** Nothing matched what was typed. */
  readonly noMatch = input(false);
  readonly noMatchTitle = input('Nothing matches that');
  readonly noMatchDescription = input('Try another word, or clear the search to see everything.');

  /** How many rows the list has after filtering. */
  readonly total = input(0);
  /** How many of them are on show. It may run ahead of `total` while rows are being read; only what exists shows. */
  readonly shown = model(LIST_BATCH_SIZE);
  /** How many rows each "Load more" adds. */
  readonly batch = input(LIST_BATCH_SIZE);
  /** What the rows are, in the footer's words: "Showing 10 of 23 recipes". */
  readonly noun = input('results');

  /** The server has rows this list has not read, so "Load more" reads them when the rows in hand run out. */
  readonly hasMore = input(false);
  readonly loadingMore = input(false);
  readonly loadMoreLabel = input('Load more');
  /** The creator asked for more than the rows in hand. The page reads them; they arrive in `total`. */
  readonly loadMore = output<void>();

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly injector = inject(Injector);

  /** How many rows are really on show: what was asked for, up to what exists. */
  protected readonly count = computed(() => Math.min(this.shown(), this.total()));

  /** Whether there is anything more to ask for: rows in hand not yet shown, or rows the server still holds. */
  protected readonly more = computed(() => this.count() < this.total() || this.hasMore());

  /** Where the creator is, said in words: how many, of how many, and whether that is all of them. */
  protected readonly summary = computed(() => {
    const total = this.total();
    const noun = this.noun();
    return this.hasMore()
      ? `Showing ${this.count()} of the ${total} ${noun} loaded so far`
      : `Showing ${this.count()} of ${total} ${noun}`;
  });

  protected onInput(event: Event): void {
    const target = event.target;
    this.query.set(target instanceof HTMLInputElement ? target.value : '');
    this.shown.set(this.batch());
  }

  protected clear(): void {
    this.query.set('');
    this.shown.set(this.batch());
  }

  protected askForMore(): void {
    if (this.loadingMore()) return;

    const target = this.count() + this.batch();
    this.shown.set(target);

    // The rows in hand will not fill the batch: read the next of the server's while these are shown.
    const wanted = target > this.total() && this.hasMore();
    if (wanted) this.loadMore.emit();

    // The button goes when there is nothing left to ask for, and focus with it. Leave it in the list instead.
    if (!wanted && target >= this.total()) this.keepFocusInList();
  }

  private keepFocusInList(): void {
    afterNextRender(() => this.scroller()?.nativeElement.focus({ preventScroll: true }), { injector: this.injector });
  }
}
