import { DatePipe } from '@angular/common';
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
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, combineLatest, debounceTime, map, merge, switchMap, tap } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpListShellComponent,
  CpListShellState,
  CpNoticeComponent,
  CpToolbarComponent,
} from '@creator-pantry/ui';

import { ContentChannel } from '../../models/brand-profile.models';
import { DAY_OF_WEEK_VALUES, DayOfWeek } from '../../models/content-seed.models';
import {
  DAM_ASSET_DAYS,
  DAM_ASSET_KIND_LABELS,
  DamAssetSort,
  DamAssetSummary,
  damUsageText,
  damVersionCountText,
  DamCreatedAsset,
} from '../../models/dam-asset.models';
import { decodeEnum } from '../../models/recipe.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { DamAssetSearchOutcome, DamAssetService } from '../../services/dam-asset.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { fileSizeText, imageFormatText } from '../content-pipeline/content-pipeline-image-presentation';
import { DamAssetThumbnailComponent } from './dam-asset-thumbnail.component';
import { DamAssetUploadComponent } from './dam-asset-upload.component';

/** How the first page of a search can stand. Later pages have their own two flags, so a failure there keeps the cards. */
type Phase = 'loading' | 'error' | 'ready';

/** `unavailable` is the degraded case: the grid still works, and the one thing lost is the channel filter. */
type ChannelPhase = 'loading' | 'ready' | 'unavailable';

interface ChannelOption {
  readonly key: string;
  readonly label: string;
}

/** One page asked for: where it resumes, and which search it belongs to. */
interface PageRequest {
  readonly cursor: string | null;
  readonly generation: number;
}

/** Long enough that a typed word is one request, short enough that the grid still feels live. */
const SEARCH_DEBOUNCE_MS = 300;

const COULD_NOT_LOAD = "Couldn't load your library. Check your connection and try again.";
const FILTER_REJECTED = 'That combination of filters could not be applied. Clearing them will start again.';

/**
 * The DAM library (`:workspaceSlug/dam`): every finished picture this workspace has kept, as cards
 * (DAM-UI-001/002).
 *
 * **Browse only.** A card's title opens the asset's own page and that is all a card does: editing it, adding a
 * version, logging a use and removing it are each another screen's work. What is here is finding one.
 *
 * **Search, filters and ordering are the server's.** A page is a page of a filtered, ordered set — narrowing or
 * re-sorting the cards held here would silently leave out matches on pages this screen has not fetched. Both
 * orderings on offer are ones the route has an index for.
 *
 * **Pages are appended, never replaced.** "Load more" follows the server's cursor, so what is on screen is
 * always a true prefix of the whole set. The total is asked for once per search and carried.
 *
 * **Filters and sort live in the URL; the cursor does not.** A search is shareable and restorable; a cursor is
 * a position in one ordered set, refused by the server once the filters, ordering or workspace change.
 *
 * **A picture is asked for by id through the authorized render route**, by the thumbnail, when its card nears
 * the screen. Nothing on this page is, or is built from, a storage address (.claude/rules/media.md).
 *
 * **Nothing of one workspace survives a change to another.** The cards — and with them every picture held —
 * and the cursor all go before the next workspace's first page is asked for (.claude/rules/tenancy.md).
 */
@Component({
  selector: 'cp-dam-library',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpListShellComponent,
    CpNoticeComponent,
    CpToolbarComponent,
    DamAssetThumbnailComponent,
    DamAssetUploadComponent,
  ],
  templateUrl: './dam-library.component.html',
  styleUrl: './dam-library.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DamLibraryComponent {
  private readonly assets = inject(DamAssetService);
  private readonly brand = inject(BrandProfileService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = signal('');

  /** Whether the upload dialog is showing. */
  protected readonly uploadOpen = signal(false);

  /**
   * Whether this creator may add a picture: Contributor and above, the role the upload route requires. False
   * while their memberships are still being read, so the button is never offered and then taken away.
   */
  protected readonly canUpload = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug())?.role;

    return role !== undefined && role !== 'Viewer';
  });

  protected readonly phase = signal<Phase>('loading');
  protected readonly errorMessage = signal(COULD_NOT_LOAD);
  protected readonly items = signal<readonly DamAssetSummary[]>([]);
  private readonly nextCursor = signal<string | null>(null);
  /** Carried across pages: the total is asked for once, because following a cursor cannot change it. */
  private readonly total = signal<number | null>(null);
  protected readonly loadingMore = signal(false);
  protected readonly moreFailed = signal(false);

  readonly searchText = signal('');
  readonly channel = signal<string | null>(null);
  readonly day = signal<DayOfWeek | null>(null);
  readonly sort = signal<DamAssetSort>('RecentlyAdded');

  protected readonly channelPhase = signal<ChannelPhase>('loading');
  private readonly channels = signal<readonly ContentChannel[]>([]);

  protected readonly dayOptions = DAM_ASSET_DAYS;

  /**
   * Which search the cards on screen belong to. Raised whenever the filters, ordering or workspace change, so
   * an answer to an earlier search — still in flight behind the debounce — is dropped rather than appended.
   */
  private generation = 0;

  private readonly typed$ = new Subject<void>();
  private readonly requests$ = new Subject<PageRequest>();

  /**
   * Every channel, retired ones included: a filter is a read, and an asset filed under a retired channel is
   * still in the library. A channel named in the URL that the catalogue does not hold is offered as its key,
   * so the control never shows "All channels" while a filter is in force.
   */
  protected readonly channelOptions = computed<readonly ChannelOption[]>(() => {
    const options: ChannelOption[] = this.channels().map((channel) => ({
      key: channel.key,
      label: channel.isActive ? channel.displayName : `${channel.displayName} (retired)`,
    }));

    const current = this.channel();
    if (current !== null && !options.some((option) => option.key === current)) {
      options.push({ key: current, label: current });
    }

    return options;
  });

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
    if (shown === 0) return this.hasActiveFilters() ? 'No pictures match these filters.' : 'No pictures in the library yet.';

    const total = this.total();
    const counted = total === null ? `${shown} ${shown === 1 ? 'picture' : 'pictures'}` : `${shown} of ${total}`;
    const order = this.sort() === 'Title' ? 'by title' : 'newest first';

    return `Showing ${counted}, ${order}.`;
  });

  constructor() {
    void this.memberships.ensureLoaded();

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
          this.assets
            .search(this.workspaceSlug(), {
              search: this.searchText(),
              channel: this.channel(),
              day: this.day(),
              sort: this.sort(),
              cursor: request.cursor,
            })
            .pipe(map((outcome) => ({ request, outcome }))),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ request, outcome }) => this.apply(request, outcome));

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          for (const params of maps) {
            const value = params.get('workspaceSlug');
            if (value) return value;
          }
          return null;
        }),
        takeUntilDestroyed(),
      )
      .subscribe((slug) => {
        if (slug === null) throw new Error('DamLibraryComponent route is missing a workspaceSlug segment.');
        if (slug !== this.workspaceSlug()) this.openWorkspace(slug);
      });

    void this.loadChannels();
  }

  // ---- Filters and ordering ----

  protected onSearchInput(event: Event): void {
    this.searchText.set(event.target instanceof HTMLInputElement ? event.target.value : '');
    this.onFilterChanged({ debounced: true });
  }

  protected setChannel(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';

    this.channel.set(value === '' ? null : value);
    this.onFilterChanged();
  }

  protected setDay(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';

    // An unrecognised value means no day filter rather than being sent on: the server would refuse it, and a
    // broken control should not become a 400 the creator cannot read.
    this.day.set(decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, value));
    this.onFilterChanged();
  }

  protected setSort(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';

    // `sort` is an allow-list the server enforces too; anything else falls back to the default.
    this.sort.set(value === 'Title' ? 'Title' : 'RecentlyAdded');
    this.onFilterChanged();
  }

  /** The ordering is not a filter: it changes how the set is read, not what is in it. */
  protected hasActiveFilters(): boolean {
    return this.searchText().trim().length > 0 || this.channel() !== null || this.day() !== null;
  }

  protected clearFilters(): void {
    this.searchText.set('');
    this.channel.set(null);
    this.day.set(null);
    this.onFilterChanged();
  }

  protected retry(): void {
    this.requests$.next(this.firstPage());
  }

  protected retryChannels(): void {
    void this.loadChannels();
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

  protected kindLabel(asset: DamAssetSummary): string {
    return DAM_ASSET_KIND_LABELS[asset.kind];
  }

  /** The channel's name, or its key when the catalogue does not hold it. Empty for an asset filed under none. */
  protected channelName(asset: DamAssetSummary): string {
    const key = asset.channelKey?.trim() ?? '';
    if (key === '') return '';

    return this.channels().find((channel) => channel.key === key)?.displayName ?? key;
  }

  /** "JPEG · 1600 × 1200 · 205 KB", or empty for an asset whose current version could not be read. */
  protected facts(asset: DamAssetSummary): string {
    if (asset.mediaType === null) return '';

    return `${imageFormatText(asset.mediaType)} · ${asset.width} × ${asset.height} · ${fileSizeText(asset.sizeBytes)}`;
  }

  protected versions(asset: DamAssetSummary): string {
    return damVersionCountText(asset);
  }

  protected usage(asset: DamAssetSummary): string {
    return damUsageText(asset);
  }

  // ---- Internals ----

  private firstPage(): PageRequest {
    return { cursor: null, generation: this.generation };
  }

  /**
   * Forget the workspace on screen, because the route now names another one — and read the new one's first page.
   *
   * Everything read for the previous one goes first. Emptying the cards destroys their thumbnails, which is
   * what revokes every picture held for the previous workspace.
   */
  /**
   * A picture was added. Its own page is where the rest of its details, its recipe links and its uses are
   * edited — the same page a kept generated picture is finished on — so that is where the creator is taken.
   */
  protected onUploaded(asset: DamCreatedAsset): void {
    void this.router.navigate(['/', this.workspaceSlug(), 'dam', asset.id]);
  }

  /** A cancelled upload may still have landed, so the library is read again rather than assumed unchanged. */
  protected onUploadCancelled(): void {
    this.openWorkspace(this.workspaceSlug());
  }

  private openWorkspace(slug: string): void {
    this.workspaceSlug.set(slug);
    this.resetPaging();
    this.items.set([]);
    this.phase.set('loading');
    this.restoreFromUrl();

    this.requests$.next(this.firstPage());
  }

  /**
   * Any change to what is being searched for, or how it is ordered, restarts paging. The cursor names a
   * position in the old ordered set, so carrying it into a new one would be refused by the server — and the
   * total belongs to the old filters too.
   */
  private onFilterChanged(options: { debounced?: boolean } = {}): void {
    this.resetPaging();
    this.writeUrl();

    if (options.debounced) {
      this.typed$.next();
    } else {
      this.requests$.next(this.firstPage());
    }
  }

  private resetPaging(): void {
    this.generation += 1;
    this.nextCursor.set(null);
    this.total.set(null);
    this.loadingMore.set(false);
    this.moreFailed.set(false);
  }

  private apply(request: PageRequest, outcome: DamAssetSearchOutcome): void {
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
   * element falls back to the top of the document. The new card's title link takes focus:
   * that is where reading resumes.
   */
  private focusCard(assetId: string): void {
    afterNextRender(
      () => {
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-asset-id="${CSS.escape(assetId)}"] .title a`)
          ?.focus();
      },
      { injector: this.injector },
    );
  }

  private async loadChannels(): Promise<void> {
    this.channelPhase.set('loading');

    const outcome = await this.brand.listContentChannels();
    if (outcome.status === 'found') {
      this.channels.set(outcome.channels);
      this.channelPhase.set('ready');
    } else {
      this.channelPhase.set('unavailable');
    }
  }

  private restoreFromUrl(): void {
    const params = this.route.snapshot.queryParamMap;
    const channel = (params.get('channel') ?? '').trim();

    this.searchText.set(params.get('search') ?? '');
    this.channel.set(channel === '' ? null : channel);
    this.day.set(decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, params.get('day')));
    this.sort.set(params.get('sort') === 'Title' ? 'Title' : 'RecentlyAdded');
  }

  /**
   * Written with `replaceUrl`, so a search is restorable without every keystroke becoming a history entry the
   * creator has to press Back through to leave the screen.
   */
  private writeUrl(): void {
    const search = this.searchText().trim();

    void this.router.navigate([], {
      relativeTo: this.route,
      replaceUrl: true,
      queryParams: {
        search: search.length > 0 ? search : null,
        channel: this.channel(),
        day: this.day(),
        sort: this.sort() === 'Title' ? 'Title' : null,
      },
    });
  }

  /** Every route node from this one up to the root, so the slug can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}
