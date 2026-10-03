import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, merge, switchMap, tap } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpListShellComponent,
  CpListShellState,
  CpStatusPillComponent,
  CpStatusPillTone,
  CpToolbarComponent,
} from '@creator-pantry/ui';

import { ContentChannel } from '../../models/brand-profile.models';
import {
  BRAND_LIBRARY_STATUSES,
  BRAND_SOURCE_DOCUMENT_TYPES,
  BRAND_SOURCE_PURPOSES,
  BrandLibraryPage,
  BrandLibraryQuery,
  BrandLibraryRow,
  BrandLibraryStatus,
  BrandSourceDocumentType,
  BrandSourcePurpose,
  DEFAULT_BRAND_LIBRARY_QUERY,
  decodeBrandLibraryStatus,
} from '../../models/brand-source-document.models';
import { BRAND_SOURCE_EXTRACTION_STATES, BrandSourceExtractionState } from '../../models/brand-source-extraction.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { BrandLibraryOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { DOCUMENT_TYPE_LABELS, EXTRACTION_STATE_LABELS, PURPOSE_LABELS } from './brand-library-labels';
import { BrandLibraryUploadComponent } from './brand-library-upload.component';

type LibraryState =
  | { readonly status: 'loading' }
  | { readonly status: 'error'; readonly message: string }
  | { readonly status: 'ready'; readonly page: BrandLibraryPage };

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
 * **Rows do not link anywhere yet.** Source detail is a separate screen, so a row states the facts it has and
 * offers no destination — a card that looked like a link and went nowhere would be worse than one that does
 * not.
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
    CpCardComponent,
    CpEmptyStateComponent,
    CpListShellComponent,
    CpStatusPillComponent,
    CpToolbarComponent,
  ],
  templateUrl: './brand-library.component.html',
  styleUrl: './brand-library.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandLibraryComponent {
  private readonly documents = inject(BrandSourceDocumentService);
  private readonly profiles = inject(BrandProfileService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly workspaceSlug = this.resolveWorkspaceSlug();

  private readonly stateSignal = signal<LibraryState>({ status: 'loading' });
  readonly state = this.stateSignal.asReadonly();

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

  private readonly cursor = signal<string | null>(null);

  /**
   * The cursors of the pages already visited, newest last. A keyset is forward-only, so "previous" is not a
   * query the server can answer — it is a position this screen remembers having been at.
   */
  private readonly history = signal<readonly (string | null)[]>([]);

  readonly pageNumber = computed(() => this.history().length + 1);

  readonly statusOptions = BRAND_LIBRARY_STATUSES;
  readonly documentTypeOptions = BRAND_SOURCE_DOCUMENT_TYPES;
  readonly purposeOptions = BRAND_SOURCE_PURPOSES;
  readonly extractionStateOptions = BRAND_SOURCE_EXTRACTION_STATES;
  readonly typeLabels = DOCUMENT_TYPE_LABELS;
  readonly purposeLabels = PURPOSE_LABELS;
  readonly extractionLabels = EXTRACTION_STATE_LABELS;

  private readonly typed$ = new Subject<void>();
  private readonly immediate$ = new Subject<void>();

  constructor() {
    this.restoreFromUrl();

    merge(this.typed$.pipe(debounceTime(SEARCH_DEBOUNCE_MS)), this.immediate$)
      .pipe(
        tap(() => this.stateSignal.set({ status: 'loading' })),
        // switchMap, not mergeMap: unsubscribing aborts the superseded request, so a slow early response can
        // never arrive after a fast later one and overwrite what the creator is looking at.
        switchMap(() => this.documents.searchLibrary(this.workspaceSlug, this.currentQuery())),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => this.apply(outcome));

    this.immediate$.next();
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
    this.immediate$.next();
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
    this.cursor.set(null);
    this.history.set([]);
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

  rows(): readonly BrandLibraryRow[] {
    const state = this.stateSignal();
    return state.status === 'ready' ? state.page.items : [];
  }

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

  /** What the live region announces after each search, so a result count is not sight-only. */
  resultSummary(): string {
    const state = this.stateSignal();
    if (state.status !== 'ready') return '';

    const shown = state.page.items.length;
    if (shown === 0) return 'No examples match these filters.';

    return `Showing ${shown} ${shown === 1 ? 'example' : 'examples'}, page ${this.pageNumber()}.`;
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

  private currentQuery(): BrandLibraryQuery {
    return {
      ...DEFAULT_BRAND_LIBRARY_QUERY,
      search: this.searchText(),
      status: this.status(),
      documentType: filtered(this.documentType()),
      purpose: filtered(this.purpose()),
      channelKey: filtered(this.channelKey()),
      extractionState: filtered(this.extractionState()),
      cursor: this.cursor(),
    };
  }

  /**
   * Any change to what is being searched for restarts paging. The cursor names a position in the old ordered
   * set, so carrying it into a new one would either be refused by the server or, worse, answer from the wrong
   * set.
   */
  private onFilterChanged(options: { debounced?: boolean } = {}): void {
    this.cursor.set(null);
    this.history.set([]);
    this.writeUrl();

    if (options.debounced) {
      this.typed$.next();
    } else {
      this.immediate$.next();
    }
  }

  private apply(outcome: BrandLibraryOutcome): void {
    switch (outcome.status) {
      case 'ok':
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
