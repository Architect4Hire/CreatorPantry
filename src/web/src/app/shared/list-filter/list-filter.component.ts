import { ChangeDetectionStrategy, Component, input, model } from '@angular/core';
import { CpButtonComponent, CpEmptyStateComponent, CpSearchFieldComponent } from '@creator-pantry/ui';

/**
 * The filter box over a list the page already holds: a search field, the one sentence saying what it found, and —
 * when it found nothing — a "no results" state that clears it.
 *
 * Presentation only. The page owns the rows, so it owns what "matches" means and how many to show: by convention
 * the first {@link LIST_PREVIEW_ROWS} until the creator types, then every match (see `list-filter.ts`). This
 * control draws the box, says the result and puts the list back on asking.
 *
 * - `query` is two-way, so the page filters from the same signal the box types into.
 * - `status` is a live region that is always present, so a result is announced when it changes, including nothing.
 * - `noMatch` draws the empty state; the page hides its own list while it is true.
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
  /** One sentence about what the filter found — "4 of 8 shown." — or empty while nothing is typed. */
  readonly status = input('');
  /** Nothing matched what was typed. */
  readonly noMatch = input(false);
  readonly noMatchTitle = input('Nothing matches that');
  readonly noMatchDescription = input('Try another word, or clear the search to see everything.');

  protected onInput(event: Event): void {
    const target = event.target;
    this.query.set(target instanceof HTMLInputElement ? target.value : '');
  }

  protected clear(): void {
    this.query.set('');
  }
}
