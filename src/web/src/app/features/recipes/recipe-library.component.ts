import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, merge, switchMap, tap } from 'rxjs';
import {
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpListShellComponent,
  CpListShellState,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpToolbarComponent,
} from '@creator-pantry/ui';

import {
  DEFAULT_RECIPE_SEARCH_QUERY,
  RecipeSearchPage,
  RecipeSearchQuery,
  RecipeSearchSort,
  RecipeStatus,
  RecipeSummary,
} from '../../models/recipe.models';
import { RecipeSearchOutcome, RecipeService } from '../../services/recipe.service';

type LibraryState =
  | { readonly status: 'loading' }
  | { readonly status: 'error'; readonly message: string }
  | { readonly status: 'ready'; readonly page: RecipeSearchPage };

const STATUS_TONE: Record<RecipeStatus, CpStatusPillTone> = { Draft: 'neutral', Ready: 'success', Archived: 'stale' };

export const FILTERABLE_STATUSES: readonly RecipeStatus[] = ['Draft', 'Ready', 'Archived'];

/** Long enough that a typed word is one request, short enough that the list still feels live. */
const SEARCH_DEBOUNCE_MS = 300;

const COULD_NOT_LOAD = "Couldn't load your recipes. Check your connection and try again.";
const FILTER_REJECTED = 'That combination of filters could not be applied. Clearing them will start again.';

/**
 * The recipe-library route (`:workspaceSlug/recipes`), backed by the Phase 6 search endpoint.
 *
 * **Every filter is applied by the server.** A page is a page of a filtered, ordered set — narrowing it here
 * would silently drop matches that live on pages this screen has not fetched, which is the one mistake a
 * partial-page list makes that a user cannot see.
 *
 * **Filters and sort live in the URL; the cursor does not.** The query string is what makes a search
 * shareable and restorable, and a keyset cursor is neither: it is a position in one ordered set, meaningless
 * once the filters it was issued under change, and the server refuses it when they do. Reloading therefore
 * returns to the first page of the same search rather than to a position that may no longer exist.
 */
@Component({
  selector: 'cp-recipe-library',
  standalone: true,
  imports: [
    RouterLink,
    DatePipe,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpListShellComponent,
    CpStatusPillComponent,
    CpToolbarComponent,
  ],
  templateUrl: './recipe-library.component.html',
  styleUrl: './recipe-library.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeLibraryComponent {
  private readonly recipeService = inject(RecipeService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly workspaceSlug = this.resolveWorkspaceSlug();

  private readonly stateSignal = signal<LibraryState>({ status: 'loading' });
  readonly state = this.stateSignal.asReadonly();

  readonly searchText = signal('');
  readonly statuses = signal<readonly RecipeStatus[]>([]);
  readonly mine = signal(false);
  readonly sort = signal<RecipeSearchSort>('RecentlyUpdated');

  private readonly cursor = signal<string | null>(null);

  /**
   * The cursors of the pages already visited, newest last. A keyset is forward-only, so "previous" is not a
   * query the server can answer — it is a position this screen remembers having been at.
   */
  private readonly history = signal<readonly (string | null)[]>([]);

  /** Carried across pages: the total is asked for once, because turning a page cannot change it. */
  private readonly total = signal<number | null>(null);

  readonly pageNumber = computed(() => this.history().length + 1);
  readonly totalCount = this.total.asReadonly();

  readonly statusOptions = FILTERABLE_STATUSES;

  private readonly typed$ = new Subject<void>();
  private readonly immediate$ = new Subject<void>();

  constructor() {
    this.restoreFromUrl();

    merge(this.typed$.pipe(debounceTime(SEARCH_DEBOUNCE_MS)), this.immediate$)
      .pipe(
        tap(() => this.stateSignal.set({ status: 'loading' })),
        // switchMap, not mergeMap: unsubscribing aborts the superseded request, so a slow early response can
        // never arrive after a fast later one and overwrite what the creator is looking at.
        switchMap(() => this.recipeService.searchRecipes(this.workspaceSlug, this.currentQuery())),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => this.apply(outcome));

    this.immediate$.next();
  }

  // ---- Filters ----

  onSearchInput(event: Event): void {
    // Narrowed rather than cast through the template's `$any`: frontend.md rules out `any`, and the DOM event's
    // target is exactly the kind of unknown that should be decoded at the boundary it arrives on.
    this.searchText.set(event.target instanceof HTMLInputElement ? event.target.value : '');
    this.onFilterChanged({ debounced: true });
  }

  toggleStatus(status: RecipeStatus): void {
    const current = this.statuses();
    this.statuses.set(current.includes(status) ? current.filter((each) => each !== status) : [...current, status]);
    this.onFilterChanged();
  }

  isStatusSelected(status: RecipeStatus): boolean {
    return this.statuses().includes(status);
  }

  toggleMine(): void {
    this.mine.update((mine) => !mine);
    this.onFilterChanged();
  }

  setSort(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';

    // An unrecognised value falls back to the default rather than being sent on: `sort` is an allow-list the
    // server enforces too, and guessing here would turn a broken control into a 400 the creator cannot read.
    this.sort.set(value === 'Title' ? 'Title' : 'RecentlyUpdated');
    this.onFilterChanged();
  }

  hasActiveFilters(): boolean {
    return this.searchText().trim().length > 0 || this.statuses().length > 0 || this.mine();
  }

  clearFilters(): void {
    this.searchText.set('');
    this.statuses.set([]);
    this.mine.set(false);
    this.onFilterChanged();
  }

  retry(): void {
    this.immediate$.next();
  }

  // ---- Paging ----

  hasNextPage(): boolean {
    const state = this.stateSignal();
    return state.status === 'ready' && state.page.nextCursor !== null;
  }

  hasPreviousPage(): boolean {
    return this.history().length > 0;
  }

  nextPage(): void {
    const state = this.stateSignal();
    if (state.status !== 'ready' || state.page.nextCursor === null) return;

    this.history.update((visited) => [...visited, this.cursor()]);
    this.cursor.set(state.page.nextCursor);
    this.immediate$.next();
  }

  previousPage(): void {
    const visited = this.history();
    if (visited.length === 0) return;

    this.cursor.set(visited[visited.length - 1]);
    this.history.set(visited.slice(0, -1));
    this.immediate$.next();
  }

  // ---- Rendering ----

  listShellState(): CpListShellState {
    const state = this.stateSignal();
    if (state.status === 'ready') return state.page.items.length === 0 ? 'empty' : 'ready';
    return state.status;
  }

  errorMessage(): string {
    const state = this.stateSignal();
    return state.status === 'error' ? state.message : COULD_NOT_LOAD;
  }

  recipes(): readonly RecipeSummary[] {
    const state = this.stateSignal();
    return state.status === 'ready' ? state.page.items : [];
  }

  statusTone(status: RecipeStatus): CpStatusPillTone {
    return STATUS_TONE[status];
  }

  /** What the live region announces after each search, so a result count is not sight-only. */
  resultSummary(): string {
    const state = this.stateSignal();
    if (state.status !== 'ready') return '';

    const shown = state.page.items.length;
    const total = this.total();
    if (shown === 0) return 'No recipes match these filters.';

    const counted = total === null ? `${shown} recipes` : `${total} ${total === 1 ? 'recipe' : 'recipes'}`;
    return `Showing ${shown} of ${counted}, page ${this.pageNumber()}.`;
  }

  // ---- Internals ----

  private currentQuery(): RecipeSearchQuery {
    return {
      ...DEFAULT_RECIPE_SEARCH_QUERY,
      search: this.searchText(),
      statuses: this.statuses(),
      mine: this.mine(),
      sort: this.sort(),
      cursor: this.cursor(),
    };
  }

  /**
   * Any change to what is being searched for restarts paging. The cursor names a position in the old ordered
   * set, so carrying it into a new one would either be refused by the server or, worse, answer from the wrong
   * set — and the total belongs to the old filters too.
   */
  private onFilterChanged(options: { debounced?: boolean } = {}): void {
    this.cursor.set(null);
    this.history.set([]);
    this.total.set(null);
    this.writeUrl();

    if (options.debounced) {
      this.typed$.next();
    } else {
      this.immediate$.next();
    }
  }

  private apply(outcome: RecipeSearchOutcome): void {
    switch (outcome.status) {
      case 'found':
        if (outcome.page.totalCount !== null) this.total.set(outcome.page.totalCount);
        this.stateSignal.set({ status: 'ready', page: outcome.page });
        return;

      case 'cursor_expired':
        // The position is gone, but the search is not. Dropping back to the first page of the same filters is
        // what the creator asked for; showing them an error for a cursor they never saw is not.
        this.cursor.set(null);
        this.history.set([]);
        this.immediate$.next();
        return;

      case 'invalid_request':
        this.stateSignal.set({ status: 'error', message: FILTER_REJECTED });
        return;

      default:
        this.stateSignal.set({ status: 'error', message: COULD_NOT_LOAD });
    }
  }

  private restoreFromUrl(): void {
    const params = this.route.snapshot.queryParamMap;

    this.searchText.set(params.get('search') ?? '');
    this.mine.set(params.get('mine') === 'true');
    this.sort.set(params.get('sort') === 'Title' ? 'Title' : 'RecentlyUpdated');

    const statuses = (params.get('status') ?? '')
      .split(',')
      .map((value) => value.trim())
      .filter((value): value is RecipeStatus => (FILTERABLE_STATUSES as readonly string[]).includes(value));

    this.statuses.set(statuses);
  }

  /**
   * Written with `replaceUrl`, so a search is restorable without every keystroke becoming a history entry the
   * creator has to press Back through to leave the screen.
   */
  private writeUrl(): void {
    const search = this.searchText().trim();
    const statuses = this.statuses();

    void this.router.navigate([], {
      relativeTo: this.route,
      replaceUrl: true,
      queryParams: {
        search: search.length > 0 ? search : null,
        status: statuses.length > 0 ? statuses.join(',') : null,
        mine: this.mine() ? 'true' : null,
        sort: this.sort() === 'Title' ? 'Title' : null,
      },
    });
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }

    throw new Error('RecipeLibraryComponent route is missing a workspaceSlug segment.');
  }
}
