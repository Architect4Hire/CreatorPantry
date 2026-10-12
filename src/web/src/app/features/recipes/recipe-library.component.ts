import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, map, merge, switchMap, tap } from 'rxjs';
import {
  CpButtonComponent,
  CpCardGridComponent,
  CpChipComponent,
  CpChipGroupComponent,
  CpEmptyStateComponent,
  CpFactComponent,
  CpFactsComponent,
  CpListShellComponent,
  CpListShellState,
  CpMediaCardComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpSearchFieldComponent,
  CpSelectFieldComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpToolbarComponent,
} from '@creator-pantry/ui';

import { injectRouteTitle } from '../../core/route-title';

import {
  DEFAULT_RECIPE_SEARCH_QUERY,
  RecipeSearchQuery,
  RecipeSearchSort,
  RecipeStatus,
  RecipeSummary,
} from '../../models/recipe.models';
import { RecipeSearchOutcome, RecipeService } from '../../services/recipe.service';

/** How the first page of a search can stand. Later pages have their own two flags, so a failure there keeps the cards. */
type Phase = 'loading' | 'error' | 'ready';

/** One page asked for: where it resumes, and which search it belongs to. */
interface PageRequest {
  readonly cursor: string | null;
  readonly generation: number;
}

/**
 * A tone per editorial state. Exhaustive by type, so a state added to the machine has to be given one here
 * rather than falling through to a default that would render it as a neutral chip nobody chose.
 *
 * The three middle states are `progress` because they are all "somebody is working on this" — the distinction
 * between them is the label, which the pill always shows beside the glyph.
 */
const STATUS_TONE: Record<RecipeStatus, CpStatusPillTone> = {
  Draft: 'neutral',
  InDevelopment: 'progress',
  Testing: 'progress',
  ReadyForReview: 'progress',
  Approved: 'success',
  Archived: 'stale',
};

/**
 * Every state, in workflow order, so the library can filter on the whole machine (TESTRUN-005).
 *
 * Order is the workflow rather than the enum's numbering, because this is a row of chips a creator reads.
 */
export const FILTERABLE_STATUSES: readonly RecipeStatus[] = [
  'Draft',
  'InDevelopment',
  'Testing',
  'ReadyForReview',
  'Approved',
  'Archived',
];

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
 * **Pages are appended, never replaced.** "Load more" follows the server's cursor, so what is on screen is
 * always a true prefix of the whole set. The total is asked for once per search and carried, because following
 * a cursor cannot change it.
 *
 * **Filters and sort live in the URL; the cursor does not.** The query string is what makes a search
 * shareable and restorable, and a keyset cursor is neither: it is a position in one ordered set, meaningless
 * once the filters it was issued under change, and the server refuses it when they do. Reloading therefore
 * returns to the first page of the same search rather than to a position that may no longer exist.
 *
 * **A card has no picture.** The search response carries none, so every card shows the panel in its place.
 */
@Component({
  selector: 'cp-recipe-library',
  standalone: true,
  imports: [
    RouterLink,
    DatePipe,
    CpButtonComponent,
    CpCardGridComponent,
    CpChipComponent,
    CpChipGroupComponent,
    CpEmptyStateComponent,
    CpFactComponent,
    CpFactsComponent,
    CpListShellComponent,
    CpMediaCardComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpSearchFieldComponent,
    CpSelectFieldComponent,
    CpStatusPillComponent,
    CpToolbarComponent,
  ],
  templateUrl: './recipe-library.component.html',
  styleUrl: './recipe-library.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeLibraryComponent {
  /** The page's name, from its own route: what `cp-page-head` prints as the one `h1`. */
  protected readonly pageTitle = injectRouteTitle();
  private readonly recipeService = inject(RecipeService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = this.resolveWorkspaceSlug();

  protected readonly phase = signal<Phase>('loading');
  protected readonly errorMessage = signal(COULD_NOT_LOAD);
  protected readonly items = signal<readonly RecipeSummary[]>([]);
  private readonly nextCursor = signal<string | null>(null);
  /** Carried across pages: the total is asked for once, because following a cursor cannot change it. */
  private readonly total = signal<number | null>(null);
  protected readonly loadingMore = signal(false);
  protected readonly moreFailed = signal(false);

  readonly searchText = signal('');
  readonly statuses = signal<readonly RecipeStatus[]>([]);
  readonly mine = signal(false);
  readonly sort = signal<RecipeSearchSort>('RecentlyUpdated');

  readonly statusOptions = FILTERABLE_STATUSES;

  /**
   * Which search the cards on screen belong to. Raised whenever the filters or ordering change, so an answer
   * to an earlier search — still in flight behind the debounce — is dropped rather than appended.
   */
  private generation = 0;

  private readonly typed$ = new Subject<void>();
  private readonly requests$ = new Subject<PageRequest>();

  protected readonly hasMore = computed(() => this.phase() === 'ready' && this.nextCursor() !== null);

  protected readonly listShellState = computed<CpListShellState>(() => {
    const phase = this.phase();
    if (phase !== 'ready') return phase;

    return this.items().length === 0 ? 'empty' : 'ready';
  });

  /** What the live region announces after each page, so a result count is not sight-only. */
  protected readonly resultSummary = computed(() => {
    if (this.phase() !== 'ready') return '';

    const shown = this.items().length;
    if (shown === 0) return this.hasActiveFilters() ? 'No recipes match these filters.' : 'No recipes yet.';

    const total = this.total();
    const counted = total === null ? `${shown} ${shown === 1 ? 'recipe' : 'recipes'}` : `${shown} of ${total}`;
    const order = this.sort() === 'Title' ? 'by title' : 'most recently updated first';

    return `Showing ${counted}, ${order}.`;
  });

  constructor() {
    this.restoreFromUrl();

    merge(
      this.typed$.pipe(
        debounceTime(SEARCH_DEBOUNCE_MS),
        map((): PageRequest => this.firstPage()),
      ),
      this.requests$,
    )
      .pipe(
        tap((request) => {
          if (request.cursor === null) this.phase.set('loading');
        }),
        // switchMap, not mergeMap: unsubscribing aborts the superseded request, so a slow early response can
        // never arrive after a fast later one and overwrite what the creator is looking at.
        switchMap((request) =>
          this.recipeService
            .searchRecipes(this.workspaceSlug, this.queryFor(request))
            .pipe(map((outcome) => ({ request, outcome }))),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ request, outcome }) => this.apply(request, outcome));

    this.requests$.next(this.firstPage());
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
    this.requests$.next(this.firstPage());
  }

  // ---- Paging ----

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (this.phase() !== 'ready' || cursor === null || this.loadingMore()) return;

    this.loadingMore.set(true);
    this.moreFailed.set(false);
    this.requests$.next({ cursor, generation: this.generation });
  }

  // ---- Cards ----

  statusTone(status: RecipeStatus): CpStatusPillTone {
    return STATUS_TONE[status];
  }

  // ---- Internals ----

  private firstPage(): PageRequest {
    return { cursor: null, generation: this.generation };
  }

  private queryFor(request: PageRequest): RecipeSearchQuery {
    return {
      ...DEFAULT_RECIPE_SEARCH_QUERY,
      search: this.searchText(),
      statuses: this.statuses(),
      mine: this.mine(),
      sort: this.sort(),
      cursor: request.cursor,
    };
  }

  /**
   * Any change to what is being searched for, or how it is ordered, restarts paging. The cursor names a
   * position in the old ordered set, so carrying it into a new one would either be refused by the server or,
   * worse, answer from the wrong set — and the total belongs to the old filters too.
   */
  private onFilterChanged(options: { debounced?: boolean } = {}): void {
    this.generation += 1;
    this.nextCursor.set(null);
    this.total.set(null);
    this.loadingMore.set(false);
    this.moreFailed.set(false);
    this.writeUrl();

    if (options.debounced) {
      this.typed$.next();
    } else {
      this.requests$.next(this.firstPage());
    }
  }

  private apply(request: PageRequest, outcome: RecipeSearchOutcome): void {
    // An answer to a search the creator has since changed. Its cards belong to a set no longer on screen.
    if (request.generation !== this.generation) return;

    const first = request.cursor === null;

    switch (outcome.status) {
      case 'found': {
        this.nextCursor.set(outcome.page.nextCursor);

        if (first) {
          this.total.set(outcome.page.totalCount);
          this.items.set(outcome.page.items);
          this.phase.set('ready');
          return;
        }

        // A keyset page cannot repeat a row, but a card is tracked by id, so a repeat would break the grid
        // rather than merely look odd. Dropped rather than trusted.
        const held = new Set(this.items().map((item) => item.id));
        const added = outcome.page.items.filter((item) => !held.has(item.id));

        this.items.update((items) => [...items, ...added]);
        this.loadingMore.set(false);
        if (added.length > 0) this.focusCard(added[0].id);
        return;
      }

      case 'cursor_expired':
        // The position is gone, but the search is not. Starting the same search again is what the creator
        // asked for; showing them an error for a cursor they never saw is not.
        this.nextCursor.set(null);
        this.loadingMore.set(false);
        this.requests$.next(this.firstPage());
        return;

      default: {
        const message = outcome.status === 'invalid_request' ? FILTER_REJECTED : COULD_NOT_LOAD;

        if (first) {
          this.errorMessage.set(message);
          this.phase.set('error');
        } else {
          // The cards already read are still true, so they stay; only the next page is missing.
          this.loadingMore.set(false);
          this.moreFailed.set(true);
        }
      }
    }
  }

  /**
   * Put focus on the first card a "Load more" added.
   *
   * The button that was pressed may have just gone — the last page has none — and focus left on a removed
   * element falls back to the top of the document. The new card's title link takes focus: that is where
   * reading resumes.
   */
  private focusCard(recipeId: string): void {
    afterNextRender(
      () => {
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-recipe-id="${CSS.escape(recipeId)}"] .title a`)
          ?.focus();
      },
      { injector: this.injector },
    );
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
