import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  model,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import { CpButtonComponent, CpEmptyStateComponent, CpSearchFieldComponent } from '@creator-pantry/ui';

import { LIST_PAGE_SIZE, pageCount } from './list-filter';

/**
 * A list the page already holds, framed: a filter box over it, the one sentence saying what the filter found, the
 * list itself in a region that scrolls, and a footer that pages through it — or, when nothing matched, a "no
 * results" state that clears the filter.
 *
 * Presentation only. The page owns the rows, so it owns what "matches" means and which rows are on the current page
 * (`pageSlice` in `list-filter.ts`). This control draws the frame, keeps the page number honest and says where the
 * creator is.
 *
 * - `query` is two-way, so the page filters from the same signal the box types into. Typing, or clearing, goes back
 *   to page one.
 * - `total` is how many rows the filter leaves, and `page` is two-way, counting from 1. `pageSize` is
 *   {@link LIST_PAGE_SIZE} unless a page says otherwise. The footer names the rows on the page, and offers Previous
 *   and Next only when there is more than one page.
 * - The list is projected, and sits in a region of its own that scrolls past a height, so ten rows never push
 *   everything under them off the screen. It returns to the top on a new page.
 * - `hasMore` says the server holds rows the page has not read, and offers `loadMore`; `loadingMore` says it is
 *   being read. What it reads is the page's: the rows arrive in `total` and the pager grows.
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

  /** How many rows the list has after filtering: what the pages are made of. */
  readonly total = input(0);
  readonly pageSize = input(LIST_PAGE_SIZE);
  /** The page on show, counting from 1. A page past the end shows, and becomes, the last. */
  readonly page = model(1);
  /** What the rows are, in the footer's words: "Showing 1–10 of 23 recipes". */
  readonly noun = input('results');

  /** The server has rows this list has not read, so the footer offers to read them. */
  readonly hasMore = input(false);
  readonly loadingMore = input(false);
  readonly loadMoreLabel = input('Load more');
  /** The creator asked for the next rows. The page reads them; they arrive in `total`. */
  readonly loadMore = output<void>();

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');

  protected readonly pages = computed(() => pageCount(this.total(), this.pageSize()));
  protected readonly current = computed(() => Math.min(Math.max(1, Math.trunc(this.page()) || 1), this.pages()));
  protected readonly first = computed(() => (this.total() === 0 ? 0 : (this.current() - 1) * this.pageSize() + 1));
  protected readonly last = computed(() => Math.min(this.total(), this.current() * this.pageSize()));

  /** Where the creator is, said in words: which rows, of how many, and whether that is all of them. */
  protected readonly summary = computed(() => {
    const total = this.total();
    const noun = this.noun();
    const range = this.first() === this.last() ? `${this.first()}` : `${this.first()}–${this.last()}`;
    return this.hasMore()
      ? `Showing ${range} of the ${total} ${noun} loaded so far`
      : `Showing ${range} of ${total} ${noun}`;
  });

  constructor() {
    // A page that shrank under the creator — a filter typed, a row removed — leaves the model on the page it can
    // still show, so it does not spring back to the old number when the list grows again.
    effect(() => {
      const current = this.current();
      if (untracked(() => this.page()) !== current) this.page.set(current);
    });
  }

  protected onInput(event: Event): void {
    const target = event.target;
    this.query.set(target instanceof HTMLInputElement ? target.value : '');
    this.page.set(1);
  }

  protected clear(): void {
    this.query.set('');
    this.page.set(1);
  }

  protected previous(): void {
    this.go(this.current() - 1);
  }

  protected next(): void {
    this.go(this.current() + 1);
  }

  protected askForMore(): void {
    if (!this.loadingMore()) this.loadMore.emit();
  }

  private go(page: number): void {
    if (page < 1 || page > this.pages()) return;
    this.page.set(page);
    // The rows of the new page replace these on the next render; the next page starts at its top.
    const scroller = this.scroller()?.nativeElement;
    if (scroller) scroller.scrollTop = 0;
  }
}
