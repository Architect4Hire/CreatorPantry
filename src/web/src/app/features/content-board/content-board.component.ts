import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
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
import { ActivatedRoute, Router } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import {
  CpBoardColumnComponent,
  CpBoardComponent,
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpListShellComponent,
  CpListShellState,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpSelectFieldComponent,
  CpSkeletonComponent,
  CpToolbarComponent,
} from '@creator-pantry/ui';

import { injectRouteTitle } from '../../core/route-title';
import { ContentChannel } from '../../models/brand-profile.models';
import {
  CONTENT_BOARD_COLUMNS,
  CONTENT_BOARD_COLUMN_LABELS,
  CONTENT_BOARD_DAYS,
  ContentBoardColumn,
  ContentBoardDay,
  ContentBoardFilters,
  ContentCard,
  ContentCardPlace,
} from '../../models/content-board.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ContentBoardService } from '../../services/content-board.service';
import { ContentBoardCardComponent } from './content-board-card.component';
import { BoardMoveResult, ContentBoardState } from './content-board-state';
import { ContentCardCreateDialogComponent } from './content-card-create-dialog.component';
import { ContentCardMoveDialogComponent } from './content-card-move-dialog.component';

type ChannelPhase = 'loading' | 'ready' | 'unavailable';

const DATE = /^\d{4}-\d{2}-\d{2}$/;
const COULD_NOT_LOAD = "Couldn't load your content board. Check your connection and try again.";

/** How many skeleton cards each column draws while the board loads, in column order. */
const SKELETON_CARDS: readonly number[] = [3, 2, 1, 2];

/**
 * The content board (`:workspaceSlug/content-board`): the workspace's planned work in four columns
 * (BOARD-UI-001 to 003).
 *
 * **A library whose list is columns** (DESIGN-SYSTEM.md, "A board: a library in columns"): the same head,
 * toolbar and list shell as any other, with `[cpBoard]` where a card grid would be.
 *
 * **The order, the counts and the rules are the server's.** Each column shows a prefix of the route's own
 * order and the route's own count; a move is a question put to the route, which alone says whether it may
 * happen and where the card ends up. `ContentBoardState` holds that conversation, including what is done when
 * the answer is no.
 *
 * **Two ways to move a card, one request.** A drag, and the "Move" control on every card, which does the same
 * from the keyboard and can add a note. Both become the same `{ column, beforeCardId }`. Either way the card is
 * shown in its new place at once, and what then happened — kept, or put back and why — is said in words in a
 * live region directly under the filters.
 *
 * **Filters live in the URL.** A filtered board is shareable and restorable, and the URL is the one source the
 * page reads them from.
 *
 * **Nothing of one workspace survives a change to another** (.claude/rules/tenancy.md): the state clears
 * every column before it asks, and both dialogs close.
 */
@Component({
  selector: 'cp-content-board',
  standalone: true,
  imports: [
    CdkDrag,
    CdkDropList,
    CdkDropListGroup,
    CpBoardColumnComponent,
    CpBoardComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpListShellComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpSelectFieldComponent,
    CpSkeletonComponent,
    CpToolbarComponent,
    ContentBoardCardComponent,
    ContentCardCreateDialogComponent,
    ContentCardMoveDialogComponent,
  ],
  templateUrl: './content-board.component.html',
  styleUrl: './content-board.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentBoardComponent {
  /** The page's name, from its own route: what `cp-page-head` prints as the one `h1`. */
  protected readonly pageTitle = injectRouteTitle('Content Board');
  private readonly brand = inject(BrandProfileService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly state = new ContentBoardState(inject(ContentBoardService));

  readonly workspaceSlug = signal('');
  protected readonly filters = signal<ContentBoardFilters>({ channel: null, day: null, date: null });

  protected readonly channelPhase = signal<ChannelPhase>('loading');
  protected readonly channels = signal<readonly ContentChannel[]>([]);

  /** What the last move, create or refusal has to say. Announced politely; an error is announced at once. */
  protected readonly message = signal('');
  protected readonly messageFailed = signal(false);

  protected readonly creating = signal(false);
  protected readonly movingCard = signal<ContentCard | null>(null);

  protected readonly columnLabels = CONTENT_BOARD_COLUMN_LABELS;
  /**
   * The board while it loads (AU.6.1): its four columns by name, holding skeleton cards in uneven numbers so it
   * reads as work in columns rather than a table. Drawn, not counted: no number here is a claim about the board.
   */
  protected readonly loadingColumns = CONTENT_BOARD_COLUMNS.map((column, index) => ({
    column,
    cards: Array.from({ length: SKELETON_CARDS[index % SKELETON_CARDS.length] }),
  }));
  protected readonly days = CONTENT_BOARD_DAYS;
  protected readonly cardNouns: readonly [string, string] = ['card', 'cards'];
  protected readonly errorMessage = COULD_NOT_LOAD;

  protected readonly hasActiveFilters = computed(() => {
    const filters = this.filters();

    return filters.channel !== null || filters.day !== null || filters.date !== null;
  });

  /**
   * Channels that can be filtered by: the catalogue, plus the one in the URL if the catalogue does not list
   * it, so the control never shows "All channels" while a filter is in force.
   */
  protected readonly channelOptions = computed(() => {
    const options = this.channels().map((channel) => ({ key: channel.key, label: channel.displayName }));
    const current = this.filters().channel;
    if (current !== null && !options.some((option) => option.key === current))
      options.push({ key: current, label: current });

    return options;
  });

  protected readonly listShellState = computed<CpListShellState>(() => {
    if (this.state.loading()) return 'loading';
    if (this.state.unavailable()) return 'error';

    const everyColumnAnswered = this.state.columns().every((column) => column.phase === 'ready');

    return everyColumnAnswered && this.state.total() === 0 ? 'empty' : 'ready';
  });

  protected readonly resultSummary = computed(() => {
    if (this.listShellState() !== 'ready') return '';

    const total = this.state.total();
    const cards = total === 1 ? '1 card' : `${total} cards`;

    return this.hasActiveFilters() ? `${cards} match these filters.` : `${cards} on the board.`;
  });

  constructor() {
    const slug$ = combineLatest(this.ancestors().map((node) => node.paramMap)).pipe(
      map((maps) => {
        for (const params of maps) {
          const value = params.get('workspaceSlug');
          if (value) return value;
        }

        return null;
      }),
    );

    // The workspace and the filters are both read from the URL, and either changing starts the board again.
    combineLatest([slug$, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([slug, query]) => {
        if (slug === null) throw new Error('ContentBoardComponent route is missing a workspaceSlug segment.');

        const day = query.get('day');
        const date = query.get('date');
        const filters: ContentBoardFilters = {
          channel: query.get('channel') || null,
          // A value the board could not have written is dropped rather than sent: the route would refuse it.
          day: CONTENT_BOARD_DAYS.includes(day as ContentBoardDay) ? (day as ContentBoardDay) : null,
          date: date !== null && DATE.test(date) ? date : null,
        };

        if (slug !== this.workspaceSlug()) {
          // Another workspace: nothing said or opened for the last one stays on screen.
          this.message.set('');
          this.creating.set(false);
          this.movingCard.set(null);
        }

        this.workspaceSlug.set(slug);
        this.filters.set(filters);
        void this.state.open(slug, filters);
      });

    void this.loadChannels();
  }

  // ---- Filters ----

  protected setChannel(event: Event): void {
    this.setFilter({ channel: valueOf(event) || null });
  }

  protected setDay(event: Event): void {
    this.setFilter({ day: (valueOf(event) || null) as ContentBoardDay | null });
  }

  protected setDate(event: Event): void {
    this.setFilter({ date: valueOf(event) || null });
  }

  protected clearFilters(): void {
    this.setFilter({ channel: null, day: null, date: null });
  }

  protected retry(): void {
    void this.state.open(this.workspaceSlug(), this.filters());
  }

  protected retryColumn(column: ContentBoardColumn): void {
    void this.state.reload(column);
  }

  protected retryChannels(): void {
    void this.loadChannels();
  }

  protected loadMore(column: ContentBoardColumn): void {
    void this.state.loadMore(column);
  }

  /** Why a column holds nothing, in the creator's terms. */
  protected emptyText(): string {
    return this.hasActiveFilters() ? 'No cards here match these filters.' : 'Nothing here yet.';
  }

  // ---- Creating ----

  protected openCreate(): void {
    this.creating.set(true);
  }

  protected closeCreate(): void {
    this.creating.set(false);
  }

  protected async onCreated(card: ContentCard): Promise<void> {
    this.creating.set(false);
    await this.state.reload(card.column);

    const shown = this.state.find(card.id) !== null;
    this.say(
      shown
        ? `Added “${card.title}” to ${CONTENT_BOARD_COLUMN_LABELS[card.column]}.`
        : `Added “${card.title}” to ${CONTENT_BOARD_COLUMN_LABELS[card.column]}. It is not shown, because it does not match the filters in use or is further down the column.`,
      false,
    );
    if (shown) this.focusCard(card.id);
  }

  // ---- Moving ----

  protected openMove(card: ContentCard): void {
    this.movingCard.set(card);
  }

  protected closeMove(): void {
    this.movingCard.set(null);
  }

  /** The keyboard path: the form has said where. Focus follows the card, wherever it ends up. */
  protected async onMoveChosen(place: ContentCardPlace): Promise<void> {
    const card = this.movingCard();
    if (card === null) return;

    this.movingCard.set(null);

    const result = await this.state.move(card.id, place);
    this.report(result);
    this.focusCard(card.id);
  }

  /** The pointer path: a card was dropped. The same question, asked the same way. */
  protected async onDrop(event: CdkDragDrop<ContentBoardColumn, ContentBoardColumn, ContentCard>): Promise<void> {
    const card = event.item.data;
    const column = event.container.data;
    if (event.previousContainer === event.container && event.previousIndex === event.currentIndex) return;

    // The index is among the cards the drop list holds without the one being dragged.
    const others = this.state.column(column).cards.filter((each) => each.id !== card.id);
    const beforeCardId = others[event.currentIndex]?.id ?? null;

    this.report(await this.state.move(card.id, { column, beforeCardId }));
  }

  private report(result: BoardMoveResult): void {
    if (result.message === '') return;

    this.say(result.message, result.status !== 'moved' && result.status !== 'unchanged');
  }

  private say(message: string, failed: boolean): void {
    this.message.set(message);
    this.messageFailed.set(failed);
  }

  /** Put focus on a card's title once the board has redrawn: a card that changed column is a new element. */
  private focusCard(cardId: string): void {
    afterNextRender(
      () => {
        this.host.nativeElement.querySelector<HTMLElement>(`[data-card-id="${CSS.escape(cardId)}"] .title a`)?.focus();
      },
      { injector: this.injector },
    );
  }

  private setFilter(change: Partial<ContentBoardFilters>): void {
    const next = { ...this.filters(), ...change };

    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { channel: next.channel, day: next.day, date: next.date },
      replaceUrl: true,
    });
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

  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}

function valueOf(event: Event): string {
  const target = event.target;

  return target instanceof HTMLSelectElement || target instanceof HTMLInputElement ? target.value : '';
}
