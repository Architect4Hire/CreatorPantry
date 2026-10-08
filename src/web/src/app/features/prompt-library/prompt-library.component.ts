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
import {
  PROMPT_IMAGE_KIND_LABELS,
  PROMPT_SOURCE_LABELS,
  PromptSummary,
  isPromptPreviewTruncated,
  promptChannelName,
  promptTitle,
} from '../../models/prompt-library.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { PromptLibraryService, PromptSearchOutcome } from '../../services/prompt-library.service';
import { PromptPreviewComponent } from './prompt-preview.component';

/** How the first page of a search can stand. Later pages have their own two flags, so a failure there keeps the rows. */
type Phase = 'loading' | 'error' | 'ready';

/** `unavailable` is the degraded case: the list still works, and the one thing lost is the channel filter. */
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

/** Long enough that a typed word is one request, short enough that the list still feels live. */
const SEARCH_DEBOUNCE_MS = 300;

const COULD_NOT_LOAD = "Couldn't load your prompts. Check your connection and try again.";

/**
 * The Prompt Library (`:workspaceSlug/prompt-library`): every prompt this workspace has saved, newest first
 * (PROMPT-UI-001, and the way into 002's preview).
 *
 * **Search, the channel filter and the ordering are the server's.** A page is a page of a filtered, ordered
 * set — narrowing or re-sorting the rows held here would silently leave out matches on pages this screen has
 * not fetched. So there is no sort control: the route has one ordering, and the page says which.
 *
 * **Pages are appended, never replaced.** "Load more" follows the server's cursor, so what is on screen is
 * always a true prefix of the whole set. The total is asked for once per search and carried, because following
 * a cursor cannot change it.
 *
 * **Filters live in the URL; the cursor does not.** A search is shareable and restorable; a cursor is a
 * position in one ordered set, refused by the server once the filters or the workspace change.
 *
 * **A row carries a preview, not the prompt.** The whole text is read from the detail route when a creator
 * asks for it, so copying is offered there and in the preview — never from a truncated row.
 *
 * **Nothing of one workspace survives a change to another.** The rows, the cursor and the open preview all go
 * before the next workspace's first page is asked for (.claude/rules/tenancy.md).
 */
@Component({
  selector: 'cp-prompt-library',
  standalone: true,
  imports: [
    RouterLink,
    DatePipe,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpListShellComponent,
    CpNoticeComponent,
    CpToolbarComponent,
    PromptPreviewComponent,
  ],
  templateUrl: './prompt-library.component.html',
  styleUrl: './prompt-library.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PromptLibraryComponent {
  private readonly prompts = inject(PromptLibraryService);
  private readonly brand = inject(BrandProfileService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = signal('');

  protected readonly phase = signal<Phase>('loading');
  protected readonly items = signal<readonly PromptSummary[]>([]);
  private readonly nextCursor = signal<string | null>(null);
  /** Carried across pages: the total is asked for once, because following a cursor cannot change it. */
  private readonly total = signal<number | null>(null);
  protected readonly loadingMore = signal(false);
  protected readonly moreFailed = signal(false);

  readonly searchText = signal('');
  readonly channel = signal<string | null>(null);

  protected readonly channelPhase = signal<ChannelPhase>('loading');
  private readonly channels = signal<readonly ContentChannel[]>([]);

  /** The row whose prompt is open in the preview, or null. */
  protected readonly previewing = signal<PromptSummary | null>(null);
  /** What opened the preview, so focus goes back to it when the preview closes. */
  private previewOpener: HTMLElement | null = null;

  /**
   * Which search the rows on screen belong to. Raised whenever the filters or the workspace change, so an
   * answer to an earlier search — still in flight behind the debounce — is dropped rather than appended.
   */
  private generation = 0;

  private readonly typed$ = new Subject<void>();
  private readonly requests$ = new Subject<PageRequest>();

  /**
   * Every channel, retired ones included: a filter is a read, and a prompt that stored a retired channel is
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

  protected readonly errorMessage = COULD_NOT_LOAD;

  /** What the live region announces after each page, so a result count is not sight-only. */
  protected readonly resultSummary = computed(() => {
    if (this.phase() !== 'ready') return '';

    const shown = this.items().length;
    if (shown === 0) return this.hasActiveFilters() ? 'No prompts match these filters.' : 'No prompts saved yet.';

    const total = this.total();
    const counted = total === null ? `${shown} ${shown === 1 ? 'prompt' : 'prompts'}` : `${shown} of ${total}`;

    return `Showing ${counted}, newest first.`;
  });

  constructor() {
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
          this.prompts
            .search(this.workspaceSlug(), {
              search: this.searchText(),
              channel: this.channel(),
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
        if (slug === null) throw new Error('PromptLibraryComponent route is missing a workspaceSlug segment.');
        if (slug !== this.workspaceSlug()) this.openWorkspace(slug);
      });

    void this.loadChannels();
  }

  // ---- Filters ----

  protected onSearchInput(event: Event): void {
    this.searchText.set(event.target instanceof HTMLInputElement ? event.target.value : '');
    this.onFilterChanged({ debounced: true });
  }

  protected setChannel(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';

    this.channel.set(value === '' ? null : value);
    this.onFilterChanged();
  }

  protected hasActiveFilters(): boolean {
    return this.searchText().trim().length > 0 || this.channel() !== null;
  }

  protected clearFilters(): void {
    this.searchText.set('');
    this.channel.set(null);
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

  // ---- Rows ----

  protected title(prompt: PromptSummary): string {
    return promptTitle(prompt.label);
  }

  protected truncated(prompt: PromptSummary): boolean {
    return isPromptPreviewTruncated(prompt);
  }

  protected channelName(prompt: PromptSummary): string {
    return promptChannelName(this.channels(), prompt.channelKey);
  }

  protected kindLabel(prompt: PromptSummary): string {
    return PROMPT_IMAGE_KIND_LABELS[prompt.imageKind];
  }

  protected sourceLabel(prompt: PromptSummary): string {
    return PROMPT_SOURCE_LABELS[prompt.source];
  }

  // ---- Preview ----

  protected openPreview(prompt: PromptSummary, event: Event): void {
    this.previewOpener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    this.previewing.set(prompt);
  }

  protected closePreview(): void {
    this.previewing.set(null);

    // Back to the button that opened it, so a keyboard user is not left at the top of the document.
    const opener = this.previewOpener;
    this.previewOpener = null;
    if (opener?.isConnected) opener.focus();
  }

  // ---- Internals ----

  private firstPage(): PageRequest {
    return { cursor: null, generation: this.generation };
  }

  /**
   * Forget the workspace on screen, because the route now names another one — and read the new one's first page.
   *
   * Everything read for the previous one goes first: nothing of one workspace may still be showing, and no
   * cursor of its may be sent, while the next one's page is being asked for.
   */
  private openWorkspace(slug: string): void {
    this.workspaceSlug.set(slug);
    this.resetPaging();
    this.items.set([]);
    this.phase.set('loading');
    this.previewing.set(null);
    this.previewOpener = null;
    this.restoreFromUrl();

    this.requests$.next(this.firstPage());
  }

  /**
   * Any change to what is being searched for restarts paging. The cursor names a position in the old ordered
   * set, so carrying it into a new one would be refused by the server — and the total belongs to the old
   * filters too.
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

  private apply(request: PageRequest, outcome: PromptSearchOutcome): void {
    // An answer to a search the creator has since changed. Its rows belong to a set no longer on screen.
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

        // A keyset page cannot repeat a row, but a row is tracked by id, so a repeat would break the list
        // rather than merely look odd. Dropped rather than trusted.
        const held = new Set(this.items().map((item) => item.promptRecordId));
        const added = outcome.page.items.filter((item) => !held.has(item.promptRecordId));

        this.items.update((items) => [...items, ...added]);
        this.loadingMore.set(false);
        if (added.length > 0) this.focusRow(added[0].promptRecordId);
        return;
      }

      case 'cursor_expired':
        // The position is gone, but the search is not. Starting the same search again is what the creator
        // asked for; showing them an error for a cursor they never saw is not.
        this.nextCursor.set(null);
        this.loadingMore.set(false);
        this.requests$.next(this.firstPage());
        return;

      default:
        if (first) {
          this.phase.set('error');
        } else {
          // The rows already read are still true, so they stay; only the next page is missing.
          this.loadingMore.set(false);
          this.moreFailed.set(true);
        }
    }
  }

  /**
   * Put focus on the first row a "Load more" added.
   *
   * The button that was pressed may have just gone — the last page has none — and focus left on a removed
   * element falls back to the top of the document. The first new row is also where reading resumes.
   */
  private focusRow(promptRecordId: string): void {
    afterNextRender(
      () => {
        this.host.nativeElement
          .querySelector<HTMLElement>(`[data-prompt-id="${CSS.escape(promptRecordId)}"] a`)
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
