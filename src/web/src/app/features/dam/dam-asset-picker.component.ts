import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, debounceTime, of, switchMap, tap } from 'rxjs';
import { CpButtonComponent, CpEmptyStateComponent, CpFieldComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { DamAssetSearchQuery, DamAssetSummary } from '../../models/dam-asset.models';
import { DamAssetSearchOutcome, DamAssetService } from '../../services/dam-asset.service';
import { DamAssetThumbnailComponent } from './dam-asset-thumbnail.component';

type BrowseState = 'loading' | 'ready' | 'empty' | 'error';

interface PageRequest {
  readonly cursor: string | null;
  readonly generation: number;
}

const SEARCH_DEBOUNCE_MS = 300;
const SEARCH_ID = 'cp-dam-picker-search';

/**
 * Choosing one picture that is already in the workspace's library: a search, a grid, and a Choose button on
 * each card.
 *
 * **It chooses and does nothing else.** There is no upload, no edit and no removal here, and it writes
 * nothing: what choosing a picture *means* — a recipe's step picture, a brand's logo — belongs to whoever
 * hosts this, which is handed the asset and decides.
 *
 * **Shared by every place a library picture is picked** (the recipe editor's Media tab, brand settings), so
 * they search, page and fail the same way. It draws no dialog of its own: the host supplies the frame.
 *
 * **Every time it becomes `active` it starts from the library as it now is.** A picture added or removed since
 * the last time should be there or gone. While inactive it keeps what it found, so a host that steps away and
 * comes back — a second step in the same dialog — returns to the same grid.
 */
@Component({
  selector: 'cp-dam-asset-picker',
  standalone: true,
  imports: [CpButtonComponent, CpEmptyStateComponent, CpFieldComponent, CpNoticeComponent, DamAssetThumbnailComponent],
  template: `
    <div class="picker">
      <!-- A search landmark and not a form: a host may itself sit inside a form (brand settings does), and a
           form nested in a form would hand its Enter to the outer one's Save. -->
      <div class="search" role="search">
        <cp-field label="Search the library" [forId]="searchId" hint="By title, description or alt text. Press Enter to search.">
          <input
            type="search"
            [id]="searchId"
            autocomplete="off"
            [value]="searchText()"
            (input)="onSearchInput($event)"
            (keydown.enter)="onSearchSubmit($event)"
          />
        </cp-field>
      </div>

      <!-- Exists before it has anything to say, so the count is announced when a search settles. -->
      <p class="cp-sr-only" role="status">{{ announcement() }}</p>

      @switch (state()) {
        @case ('loading') {
          <p class="placeholder" role="status">Loading the library…</p>
        }
        @case ('error') {
          <cp-notice tone="error" role="alert">
            The library could not be loaded.
            <button cpButton type="button" variant="secondary" size="sm" (click)="search()">Try again</button>
          </cp-notice>
        }
        @case ('empty') {
          @if (searchText().trim().length > 0) {
            <cp-empty-state
              variant="no-results"
              title="No pictures match that search"
              description="Try fewer words, or clear the search to see the whole library."
              icon="▧"
            >
              <button cpEmptyStateActions cpButton type="button" variant="secondary" (click)="clearSearch()">Clear the search</button>
            </cp-empty-state>
          } @else {
            <cp-empty-state
              title="The library has no pictures yet"
              description="Pictures are added on the DAM page. Once one is there, it can be chosen from here."
              icon="▧"
            />
          }
        }
        @default {
          <ul class="grid" aria-label="Library pictures">
            @for (asset of items(); track asset.id) {
              <li class="tile">
                <cp-dam-asset-thumbnail [workspaceSlug]="workspaceSlug()" [asset]="asset" />
                <p class="tile-title">{{ asset.title }}</p>
                <p class="tile-facts">{{ versionCountText(asset) }}</p>
                <button
                  cpButton
                  type="button"
                  variant="secondary"
                  size="sm"
                  [fullWidth]="true"
                  [attr.data-choose]="asset.id"
                  [attr.aria-label]="'Choose ' + asset.title"
                  (click)="chosen.emit(asset)"
                >
                  Choose
                </button>
              </li>
            }
          </ul>

          @if (loadMoreFailed()) {
            <cp-notice tone="error" role="alert">More pictures could not be loaded. What is shown is still here.</cp-notice>
          }

          @if (nextCursor() !== null) {
            <div class="more">
              <button cpButton type="button" variant="secondary" [disabled]="loadingMore()" (click)="loadMore()">
                {{ loadingMore() ? 'Loading…' : 'Load more' }}
              </button>
            </div>
          }
        }
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .picker {
        display: grid;
        gap: var(--cp-space-5);
      }
      /* As many columns as fit the host, and one at a narrow width or high zoom: a tile never shrinks below a
         width its title can be read at. */
      .grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(9.5rem, 1fr));
        gap: var(--cp-space-4);
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .tile {
        display: grid;
        gap: var(--cp-space-2);
        align-content: start;
        min-width: 0;
      }
      .tile-title {
        margin: 0;
        font-weight: 600;
        overflow-wrap: anywhere;
      }
      .tile-facts,
      .placeholder {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
      .more {
        display: flex;
        justify-content: center;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DamAssetPickerComponent {
  private readonly assets = inject(DamAssetService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = input.required<string>();

  /** Whether the picker is in use. Each change to true starts a fresh search of the library. */
  readonly active = input(true);

  readonly chosen = output<DamAssetSummary>();

  protected readonly searchId = SEARCH_ID;
  protected readonly searchText = signal('');
  protected readonly state = signal<BrowseState>('loading');
  protected readonly items = signal<readonly DamAssetSummary[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly totalCount = signal<number | null>(null);
  protected readonly loadingMore = signal(false);
  protected readonly loadMoreFailed = signal(false);

  /** Said to a screen reader when a search settles; the grid itself is not a live region. */
  protected readonly announcement = computed(() => {
    if (this.state() !== 'ready') return '';

    const total = this.totalCount();
    const shown = this.items().length;

    return total === null ? `${shown} pictures shown.` : `${shown} of ${total} pictures shown.`;
  });

  private readonly requests$ = new Subject<PageRequest>();
  private readonly searchChanged$ = new Subject<void>();

  /** Raised on every new search, so a page that arrives for an earlier one is dropped. */
  private generation = 0;

  constructor() {
    this.requests$
      .pipe(
        switchMap((request) => {
          const slug = this.workspaceSlug();

          return (slug ? this.assets.search(slug, this.queryFor(request.cursor)) : of<DamAssetSearchOutcome>({ status: 'unavailable' })).pipe(
            tap((outcome) => this.applyPage(request, outcome)),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe();

    this.searchChanged$.pipe(debounceTime(SEARCH_DEBOUNCE_MS), takeUntilDestroyed()).subscribe(() => this.search());

    effect(() => {
      if (!this.active()) return;

      untracked(() => {
        this.searchText.set('');
        this.search();
      });
    });
  }

  /** Puts focus back on the card of a picture chosen earlier, or on the search when it is no longer shown. */
  focusChoice(assetId: string | null): void {
    afterNextRender(
      () => {
        const root = this.host.nativeElement;
        const card = assetId === null ? null : root.querySelector<HTMLElement>(`[data-choose="${assetId}"]`);

        (card ?? root.querySelector<HTMLElement>(`#${SEARCH_ID}`))?.focus();
      },
      { injector: this.injector },
    );
  }

  protected onSearchInput(event: Event): void {
    this.searchText.set((event.target as HTMLInputElement).value);
    this.searchChanged$.next();
  }

  /** Enter searches now rather than after the pause — and never reaches a form the host may be inside. */
  protected onSearchSubmit(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.search();
  }

  protected clearSearch(): void {
    this.searchText.set('');
    this.search();
  }

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (cursor === null || this.loadingMore()) return;

    this.loadingMore.set(true);
    this.loadMoreFailed.set(false);
    this.requests$.next({ cursor, generation: this.generation });
  }

  protected versionCountText(asset: DamAssetSummary): string {
    return asset.currentVersionNumber === 1 ? '1 version' : `${asset.currentVersionNumber} versions`;
  }

  protected search(): void {
    this.generation += 1;
    this.items.set([]);
    this.nextCursor.set(null);
    this.totalCount.set(null);
    this.loadingMore.set(false);
    this.loadMoreFailed.set(false);
    this.state.set('loading');
    this.requests$.next({ cursor: null, generation: this.generation });
  }

  private queryFor(cursor: string | null): DamAssetSearchQuery {
    return { search: this.searchText(), channel: null, day: null, sort: 'RecentlyAdded', cursor };
  }

  private applyPage(request: PageRequest, outcome: DamAssetSearchOutcome): void {
    if (request.generation !== this.generation) return;

    const followsOn = request.cursor !== null;
    this.loadingMore.set(false);

    if (outcome.status === 'found') {
      const known = new Set(this.items().map((asset) => asset.id));

      // A picture added while paging can arrive on two pages; it is shown once.
      this.items.update((items) => [...items, ...outcome.page.items.filter((asset) => !known.has(asset.id))]);
      this.nextCursor.set(outcome.page.nextCursor);
      if (outcome.page.totalCount !== null) this.totalCount.set(outcome.page.totalCount);
      this.state.set(this.items().length === 0 ? 'empty' : 'ready');
      return;
    }

    // A cursor the server no longer honours means the list moved under it; starting again is the remedy and
    // needs nothing from the creator.
    if (outcome.status === 'cursor_expired') {
      this.search();
      return;
    }

    // A failed further page leaves what is already shown where it is.
    if (followsOn) {
      this.loadMoreFailed.set(true);
      return;
    }

    this.state.set('error');
  }
}
