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
import { Subject, debounceTime, map, merge, switchMap, tap } from 'rxjs';
import {
  CpBadgeComponent,
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

import { ContentChannel } from '../../models/brand-profile.models';
import {
  BRAND_LIBRARY_STATUSES,
  BRAND_SOURCE_DOCUMENT_TYPES,
  BRAND_SOURCE_PURPOSES,
  BrandLibraryQuery,
  BrandLibraryRow,
  BrandLibraryStatus,
  BrandSourceDocumentType,
  BrandSourcePurpose,
  DEFAULT_BRAND_LIBRARY_QUERY,
  decodeBrandLibraryStatus,
} from '../../models/brand-source-document.models';
import {
  BRAND_SOURCE_EXTRACTION_STATES,
  BrandSourceExtractionState,
} from '../../models/brand-source-extraction.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { BrandLibraryOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { DOCUMENT_TYPE_LABELS, EXTRACTION_STATE_LABELS, PURPOSE_LABELS } from './brand-library-labels';
import { BrandLibraryUploadComponent } from './brand-library-upload.component';

/** How the first page of a search can stand. Later pages have their own two flags, so a failure there keeps the cards. */
type Phase = 'loading' | 'error' | 'ready';

/** One page asked for: where it resumes, and which search it belongs to. */
interface PageRequest {
  readonly cursor: string | null;
  readonly generation: number;
}

/**
 * A tone per extraction state. Exhaustive by type, so a state added to the read model has to be given one
 * here rather than falling through to a default that would render it as a neutral chip nobody chose.
 *
 * `Unsupported` is `warning` rather than `error`: a scan or an image is a review state, not a fault, and the
 * document is perfectly good as a visual reference. `Failed` is the only one that went wrong.
 */
const EXTRACTION_TONE: Record<BrandSourceExtractionState, CpStatusPillTone> = {
  NotExtracted: 'neutral',
  Succeeded: 'success',
  Unsupported: 'warning',
  Failed: 'error',
};

/** Long enough that a typed word is one request, short enough that the list still feels live. */
const SEARCH_DEBOUNCE_MS = 300;

/**
 * A control's value as a filter: the empty option means "not filtered", which the query says with null and
 * the service then leaves out of the request altogether.
 */
function filtered<T extends string>(value: T | ''): T | null {
  return value === '' ? null : value;
}

const COULD_NOT_LOAD = "Couldn't load your brand library. Check your connection and try again.";
const FILTER_REJECTED = 'That combination of filters could not be applied. Clearing them will start again.';

/** The degraded message (audit 11A.24a): what failed, and that what is on screen is still good. */
const COULD_NOT_REFRESH =
  "Couldn't load the newest results. These are the ones from a moment ago, and they're still yours to open.";

/**
 * The Brand Library route (`:workspaceSlug/brand/library`): every example this workspace has given the brand,
 * searchable and filterable, with what each one is and whether its text has been read.
 *
 * **Every filter is applied by the server.** A page is a page of a filtered, ordered set — narrowing it here
 * would silently drop matches that live on pages this screen has not fetched, which is the one mistake a
 * partial-page list makes that a reader cannot see. That is also why `purpose` and `extractionState` exist as
 * query parameters at all rather than being sorted out in the browser.
 *
 * **Filters live in the URL; the cursor does not.** The query string is what makes a search shareable and
 * restorable, and a keyset cursor is neither: it is a position in one ordered set, meaningless once the
 * filters it was issued under change, and the server refuses it when they do. Reloading therefore returns to
 * the first page of the same search rather than to a position that may no longer exist.
 *
 * **Pages are appended, never replaced.** "Load more" follows the server's cursor, so what is on screen is
 * always a true prefix of the whole set. The route reports no total, so the summary counts what is shown.
 *
 * **A card has no picture.** An example is a document — and where it is an image, this list does not load it —
 * so every card shows the panel in a picture's place and says what the example is in badges and facts.
 */
@Component({
  selector: 'cp-brand-library',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    BrandLibraryUploadComponent,
    CpBadgeComponent,
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
  templateUrl: './brand-library.component.html',
  styleUrl: './brand-library.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandLibraryComponent {
  /** The page's name, from its own route: what `cp-page-head` prints as the one `h1`. */
  protected readonly pageTitle = injectRouteTitle();
  private readonly documents = inject(BrandSourceDocumentService);
  private readonly profiles = inject(BrandProfileService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = this.resolveWorkspaceSlug();

  protected readonly phase = signal<Phase>('loading');
  protected readonly errorMessage = signal(COULD_NOT_LOAD);
  protected readonly items = signal<readonly BrandLibraryRow[]>([]);
  private readonly nextCursor = signal<string | null>(null);
  protected readonly loadingMore = signal(false);
  protected readonly moreFailed = signal(false);

  /**
   * Set while the cards on screen are the last ones that loaded and a newer first-page read has failed
   * (audit 11A.24a). What the creator is looking at is real and still theirs to open; what failed is the
   * newest read, and only that is said.
   */
  protected readonly degradedMessage = signal('');

  /** Whether {@link items} holds a page that loaded, so a later failed read has something to keep. */
  private held = false;

  readonly searchText = signal('');
  readonly status = signal<BrandLibraryStatus>('Active');
  readonly documentType = signal<BrandSourceDocumentType | ''>('');
  readonly purpose = signal<BrandSourcePurpose | ''>('');
  readonly channelKey = signal('');
  readonly extractionState = signal<BrandSourceExtractionState | ''>('');

  /**
   * The channels this workspace may filter by, as the reference catalog lists them. Empty when it could not be
   * read, which hides the control rather than offering an empty one: a channel filter nobody can set is a dead
   * end, and the rest of the library still works without it.
   */
  readonly channels = signal<readonly ContentChannel[]>([]);

  readonly uploadOpen = signal(false);

  readonly statusOptions = BRAND_LIBRARY_STATUSES;
  readonly documentTypeOptions = BRAND_SOURCE_DOCUMENT_TYPES;
  readonly purposeOptions = BRAND_SOURCE_PURPOSES;
  readonly extractionStateOptions = BRAND_SOURCE_EXTRACTION_STATES;
  readonly typeLabels = DOCUMENT_TYPE_LABELS;
  readonly purposeLabels = PURPOSE_LABELS;
  readonly extractionLabels = EXTRACTION_STATE_LABELS;

  /**
   * Which search the cards on screen belong to. Raised whenever the filters change or an example is added, so
   * an answer to an earlier search — still in flight behind the debounce — is dropped rather than appended.
   */
  private generation = 0;

  private readonly typed$ = new Subject<void>();
  private readonly requests$ = new Subject<PageRequest>();

  /** Not while degraded: the cursor held belongs to the cards on screen, not to the search that just failed. */
  protected readonly hasMore = computed(
    () => this.phase() === 'ready' && this.degradedMessage() === '' && this.nextCursor() !== null,
  );

  protected readonly listShellState = computed<CpListShellState>(() => {
    const phase = this.phase();
    if (phase !== 'ready') return phase;

    return this.items().length === 0 ? 'empty' : 'ready';
  });

  /** What the live region announces after each page, so a result count is not sight-only. */
  protected readonly resultSummary = computed(() => {
    // Nothing while degraded: the cards are not the answer to the search in force, so a count of them would
    // read as one.
    if (this.phase() !== 'ready' || this.degradedMessage() !== '') return '';

    const shown = this.items().length;
    if (shown === 0) return this.hasActiveFilters() ? 'No examples match these filters.' : 'No examples yet.';

    const counted = `${shown} ${shown === 1 ? 'example' : 'examples'}`;

    return this.nextCursor() === null ? `Showing ${counted}.` : `Showing the first ${counted}.`;
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
          this.documents
            .searchLibrary(this.workspaceSlug, this.queryFor(request))
            .pipe(map((outcome) => ({ request, outcome }))),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ request, outcome }) => this.apply(request, outcome));

    this.requests$.next(this.firstPage());
    void this.loadChannels();
  }

  // ---- Filters ----

  onSearchInput(event: Event): void {
    // Narrowed rather than cast through the template's `$any`: frontend.md rules out `any`, and the DOM event's
    // target is exactly the kind of unknown that should be decoded at the boundary it arrives on.
    this.searchText.set(event.target instanceof HTMLInputElement ? event.target.value : '');
    this.onFilterChanged({ debounced: true });
  }

  setStatus(status: BrandLibraryStatus): void {
    if (this.status() === status) return;

    this.status.set(status);
    this.onFilterChanged();
  }

  isStatusSelected(status: BrandLibraryStatus): boolean {
    return this.status() === status;
  }

  setDocumentType(event: Event): void {
    this.documentType.set(this.chosen(event, this.documentTypeOptions));
    this.onFilterChanged();
  }

  setPurpose(event: Event): void {
    this.purpose.set(this.chosen(event, this.purposeOptions));
    this.onFilterChanged();
  }

  setExtractionState(event: Event): void {
    this.extractionState.set(this.chosen(event, this.extractionStateOptions));
    this.onFilterChanged();
  }

  setChannel(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';
    this.channelKey.set(this.channels().some((channel) => channel.key === value) ? value : '');
    this.onFilterChanged();
  }

  hasActiveFilters(): boolean {
    return (
      this.searchText().trim().length > 0 ||
      this.status() !== 'Active' ||
      this.documentType() !== '' ||
      this.purpose() !== '' ||
      this.channelKey() !== '' ||
      this.extractionState() !== ''
    );
  }

  clearFilters(): void {
    this.searchText.set('');
    this.status.set('Active');
    this.documentType.set('');
    this.purpose.set('');
    this.channelKey.set('');
    this.extractionState.set('');
    this.onFilterChanged();
  }

  retry(): void {
    this.requests$.next(this.firstPage());
  }

  // ---- Adding ----

  openUpload(): void {
    this.uploadOpen.set(true);
  }

  closeUpload(): void {
    this.uploadOpen.set(false);
  }

  /**
   * A new example is the newest edit in the workspace, so the list goes back to the first page of the current
   * search to show it — or to show honestly that the filters in force exclude it.
   */
  onUploaded(): void {
    this.resetPaging();
    this.requests$.next(this.firstPage());
  }

  // ---- Paging ----

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (!this.hasMore() || cursor === null || this.loadingMore()) return;

    this.loadingMore.set(true);
    this.moreFailed.set(false);
    this.requests$.next({ cursor, generation: this.generation });
  }

  // ---- Cards ----

  extractionTone(state: BrandSourceExtractionState): CpStatusPillTone {
    return EXTRACTION_TONE[state];
  }

  /** The channel's own display name, falling back to the stored key for a channel since retired. */
  channelLabel(key: string): string {
    return this.channels().find((channel) => channel.key === key)?.displayName ?? key;
  }

  /** A size a creator can read. Decimal units, which is how an operating system reports a file. */
  fileSize(bytes: number): string {
    if (bytes < 1000) return `${bytes} B`;
    if (bytes < 1000 * 1000) return `${Math.round(bytes / 1000)} KB`;

    return `${(bytes / (1000 * 1000)).toFixed(1)} MB`;
  }

  // ---- Internals ----

  /**
   * The chosen option, or "not filtered" for anything this screen does not offer. An unrecognised value is
   * never sent on: these are allow-lists the server enforces too, and guessing would turn a broken control
   * into a refusal the creator cannot read.
   */
  private chosen<T extends string>(event: Event, options: readonly T[]): T | '' {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';
    return (options as readonly string[]).includes(value) ? (value as T) : '';
  }

  private firstPage(): PageRequest {
    return { cursor: null, generation: this.generation };
  }

  private queryFor(request: PageRequest): BrandLibraryQuery {
    return {
      ...DEFAULT_BRAND_LIBRARY_QUERY,
      search: this.searchText(),
      status: this.status(),
      documentType: filtered(this.documentType()),
      purpose: filtered(this.purpose()),
      channelKey: filtered(this.channelKey()),
      extractionState: filtered(this.extractionState()),
      cursor: request.cursor,
    };
  }

  /**
   * Any change to what is being searched for restarts paging. The cursor names a position in the old ordered
   * set, so carrying it into a new one would either be refused by the server or, worse, answer from the wrong
   * set.
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

  /** The cards stay where they are: they are what a failed read of the new search degrades to. */
  private resetPaging(): void {
    this.generation += 1;
    this.loadingMore.set(false);
    this.moreFailed.set(false);
  }

  private apply(request: PageRequest, outcome: BrandLibraryOutcome): void {
    // An answer to a search the creator has since changed. Its cards belong to a set no longer on screen.
    if (request.generation !== this.generation) return;

    const first = request.cursor === null;

    switch (outcome.status) {
      case 'ok': {
        this.nextCursor.set(outcome.page.nextCursor);

        if (first) {
          this.held = true;
          this.degradedMessage.set('');
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

      case 'invalid_request':
        // The filters, not the connection. The cards on screen were found under filters the server has just
        // refused, so they are forgotten: keeping them beside this message — or bringing them back later as a
        // degraded view — would be showing results for a question it would not answer.
        this.held = false;
        this.items.set([]);
        this.nextCursor.set(null);
        this.loadingMore.set(false);
        this.degradedMessage.set('');
        this.errorMessage.set(FILTER_REJECTED);
        this.phase.set('error');
        return;

      default:
        if (!first) {
          // The cards already read are still true, so they stay; only the next page is missing.
          this.loadingMore.set(false);
          this.moreFailed.set(true);
        } else if (this.held) {
          // Degraded: the last cards that loaded stay on screen, and only the newest read is reported.
          this.degradedMessage.set(COULD_NOT_REFRESH);
          this.phase.set('ready');
        } else {
          this.errorMessage.set(COULD_NOT_LOAD);
          this.phase.set('error');
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
  private focusCard(documentId: string): void {
    afterNextRender(
      () => {
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-example-id="${CSS.escape(documentId)}"] .title a`)
          ?.focus();
      },
      { injector: this.injector },
    );
  }

  /**
   * The channel vocabulary, read once. A failure is not reported as a library error: the list itself loaded,
   * and the only consequence is one filter fewer.
   */
  private async loadChannels(): Promise<void> {
    const outcome = await this.profiles.listContentChannels();
    if (outcome.status !== 'found') return;

    // A retired channel is kept only where it is already in force, so a filter restored from a URL still
    // names what it is filtering by.
    const chosen = this.channelKey();
    this.channels.set(outcome.channels.filter((channel) => channel.isActive || channel.key === chosen));
  }

  private restoreFromUrl(): void {
    const params = this.route.snapshot.queryParamMap;

    this.searchText.set(params.get('search') ?? '');
    this.status.set(decodeBrandLibraryStatus(params.get('status')) ?? 'Active');
    this.documentType.set(this.known(params.get('documentType'), this.documentTypeOptions));
    this.purpose.set(this.known(params.get('purpose'), this.purposeOptions));
    this.extractionState.set(this.known(params.get('extractionState'), this.extractionStateOptions));

    // Not checked against the catalog, which has not been read yet. An unknown key simply matches nothing,
    // which is what the server answers for one too.
    this.channelKey.set(params.get('channelKey') ?? '');
  }

  private known<T extends string>(value: string | null, options: readonly T[]): T | '' {
    return value !== null && (options as readonly string[]).includes(value) ? (value as T) : '';
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
        status: this.status() === 'Active' ? null : this.status(),
        documentType: this.documentType() === '' ? null : this.documentType(),
        purpose: this.purpose() === '' ? null : this.purpose(),
        channelKey: this.channelKey() === '' ? null : this.channelKey(),
        extractionState: this.extractionState() === '' ? null : this.extractionState(),
      },
    });
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }

    throw new Error('BrandLibraryComponent route is missing a workspaceSlug segment.');
  }
}
