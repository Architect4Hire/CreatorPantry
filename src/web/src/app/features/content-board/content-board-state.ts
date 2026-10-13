import { Signal, computed, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import {
  CONTENT_BOARD_COLUMNS,
  CONTENT_BOARD_COLUMN_LABELS,
  ContentBoardColumn,
  ContentBoardFilters,
  ContentCard,
  ContentCardPlace,
  NO_BOARD_FILTERS,
} from '../../models/content-board.models';
import { ContentBoardService } from '../../services/content-board.service';

/** How many cards one read of a column asks for. The route clamps to 100. */
export const BOARD_PAGE_SIZE = 25;
const BOARD_MAX_PAGE = 100;

export type BoardColumnPhase = 'loading' | 'ready' | 'unavailable';

/** One column as the board holds it: a true prefix of the server's ordered column, and its whole count. */
export interface BoardColumnState {
  readonly column: ContentBoardColumn;
  readonly phase: BoardColumnPhase;
  readonly cards: readonly ContentCard[];
  /** Every card the filters match in this column, loaded or not. Null until the server has said. */
  readonly count: number | null;
  readonly nextCursor: string | null;
  readonly loadingMore: boolean;
  readonly moreFailed: boolean;
}

/** How a move ended, and what to tell the creator about it. Only `moved` changed anything. */
export interface BoardMoveResult {
  readonly status: 'moved' | 'unchanged' | 'busy' | 'stale' | 'refused' | 'gone' | 'forbidden' | 'unavailable';
  readonly message: string;
}

type Columns = Readonly<Record<ContentBoardColumn, BoardColumnState>>;

function blank(phase: BoardColumnPhase): Columns {
  const columns = {} as Record<ContentBoardColumn, BoardColumnState>;
  for (const column of CONTENT_BOARD_COLUMNS) {
    columns[column] = { column, phase, cards: [], count: null, nextCursor: null, loadingMore: false, moreFailed: false };
  }

  return columns;
}

/**
 * What the board page knows: four columns, each a prefix of the server's own order, and the one move in flight.
 *
 * **Each column is read on its own**, with the route's `column` filter, so one that fails or is slow does not
 * hold up the other three and "Load more" follows that column's cursor alone.
 *
 * **A move is optimistic, one at a time, and the server's answer is final.** The card is shown where the
 * creator put it at once. When the route says yes, the columns it touched are read again — making room can
 * change a neighbour's `concurrencyToken`, and a card dropped at the bottom of a column that is only partly
 * loaded belongs after cards this screen has not fetched. When it says anything else, the board is put back
 * exactly as it was and then, where the refusal means this screen was out of date, read again. No rule about
 * which moves are allowed lives here: every column is offered and the route decides.
 *
 * **Nothing of one workspace survives a change to another.** `open` clears every column before it asks, and an
 * answer for a workspace or a set of filters the board has since left is dropped (.claude/rules/tenancy.md).
 *
 * A plain class, not a service: one per board page, made and owned by it.
 */
export class ContentBoardState {
  private readonly columnsState = signal<Columns>(blank('loading'));
  private readonly movingState = signal<string | null>(null);

  private slug = '';
  private filters: ContentBoardFilters = NO_BOARD_FILTERS;
  /** Bumped by every `open`, so an answer to an earlier one can tell it has been superseded. */
  private generation = 0;

  readonly columns: Signal<readonly BoardColumnState[]> = computed(() =>
    CONTENT_BOARD_COLUMNS.map((column) => this.columnsState()[column]),
  );

  /** The id of the card whose move is waiting for its answer, or null. */
  readonly moving = this.movingState.asReadonly();

  /** True until every column's first read has answered. */
  readonly loading = computed(() => this.columns().some((column) => column.phase === 'loading'));

  /** True when no column could be read at all: the board has nothing to show and says so once. */
  readonly unavailable = computed(() => this.columns().every((column) => column.phase === 'unavailable'));

  /** Cards the filters match across the whole board, counting only columns that answered. */
  readonly total = computed(() => this.columns().reduce((sum, column) => sum + (column.count ?? 0), 0));

  constructor(private readonly board: ContentBoardService) {}

  column(column: ContentBoardColumn): BoardColumnState {
    return this.columnsState()[column];
  }

  find(cardId: string): ContentCard | null {
    for (const column of this.columns()) {
      const card = column.cards.find((each) => each.id === cardId);
      if (card) return card;
    }

    return null;
  }

  /** Start again for a workspace and a set of filters: clear everything, then read all four columns. */
  async open(slug: string, filters: ContentBoardFilters): Promise<void> {
    this.slug = slug;
    this.filters = filters;
    this.generation += 1;
    this.movingState.set(null);
    this.columnsState.set(blank('loading'));

    await Promise.all(CONTENT_BOARD_COLUMNS.map((column) => this.read(column, BOARD_PAGE_SIZE)));
  }

  /** Read one column again from its top, keeping as much of it on screen as there was. */
  async reload(column: ContentBoardColumn): Promise<void> {
    const held = this.column(column).cards.length;

    await this.read(column, Math.min(BOARD_MAX_PAGE, Math.max(BOARD_PAGE_SIZE, held + 1)));
  }

  /** The next page of one column, appended. A failure keeps what is there and says so. */
  async loadMore(column: ContentBoardColumn): Promise<void> {
    const current = this.column(column);
    if (current.nextCursor === null || current.loadingMore) return;

    const generation = this.generation;
    this.patch(column, { loadingMore: true, moreFailed: false });

    const outcome = await firstValueFrom(
      this.board.list(this.slug, this.filters, column, current.nextCursor, BOARD_PAGE_SIZE),
    );
    if (generation !== this.generation) return;

    if (outcome.status === 'cursor_expired') {
      // The column changed shape under the cursor. Its top is read again rather than a page guessed at.
      this.patch(column, { loadingMore: false });
      await this.reload(column);
      return;
    }

    if (outcome.status !== 'found') {
      this.patch(column, { loadingMore: false, moreFailed: true });
      return;
    }

    const known = new Set(this.column(column).cards.map((card) => card.id));
    this.patch(column, {
      // A card that moved down between two reads would otherwise be listed twice.
      cards: [...this.column(column).cards, ...outcome.page.items.filter((card) => !known.has(card.id))],
      nextCursor: outcome.page.nextCursor,
      count: outcome.page.columns.find((each) => each.column === column)?.count ?? this.column(column).count,
      loadingMore: false,
    });
  }

  /** Take a card off the board that the server says is gone. */
  drop(cardId: string): void {
    const card = this.find(cardId);
    if (card === null) return;

    const column = this.column(card.column);
    this.patch(card.column, {
      cards: column.cards.filter((each) => each.id !== cardId),
      count: column.count === null ? null : Math.max(0, column.count - 1),
    });
  }

  /**
   * Ask for a card to be somewhere else, showing it there while the route decides.
   *
   * `place.beforeCardId` names the card it goes immediately before; null is the bottom of the column.
   */
  async move(cardId: string, place: ContentCardPlace): Promise<BoardMoveResult> {
    if (this.movingState() !== null) {
      return { status: 'busy', message: 'One move at a time: the last one is still being saved.' };
    }

    const card = this.find(cardId);
    if (card === null) return { status: 'gone', message: 'That card is no longer on the board.' };

    const from = card.column;
    const to = place.column;
    const title = card.title;

    if (this.alreadyThere(card, place)) {
      return { status: 'unchanged', message: `“${title}” is already there.` };
    }

    const before = this.columnsState();
    const generation = this.generation;

    this.movingState.set(cardId);
    this.columnsState.set(this.placed(before, card, place));

    const outcome = await firstValueFrom(this.board.move(this.slug, cardId, place, card.concurrencyToken));
    if (generation !== this.generation) return { status: 'unavailable', message: '' };

    this.movingState.set(null);

    if (outcome.status === 'saved') {
      // The server's copy first, so the card carries its new token even if the reads below fail.
      this.replace(outcome.detail.card);
      await Promise.all(from === to ? [this.reload(to)] : [this.reload(from), this.reload(to)]);

      return { status: 'moved', message: `Moved “${title}” ${this.where(place)}.` };
    }

    // Anything else wrote nothing. Back exactly as it was, before anything is read again.
    this.columnsState.set(before);

    switch (outcome.status) {
      case 'stale':
        await Promise.all(from === to ? [this.reload(to)] : [this.reload(from), this.reload(to)]);

        return {
          status: 'stale',
          message: `The board changed before “${title}” could be moved, so it was not. The board has been read again.`,
        };

      case 'refused':
      case 'invalid':
        await this.reload(to);

        return {
          status: 'refused',
          message: `“${title}” could not be put there, so it has not moved. ${CONTENT_BOARD_COLUMN_LABELS[to]} has been read again.`,
        };

      case 'not_found':
        this.drop(cardId);
        await this.reload(from);

        return { status: 'gone', message: `“${title}” has been deleted, so it could not be moved.` };

      case 'forbidden':
        return {
          status: 'forbidden',
          message: `You do not have permission to move cards in this workspace, so “${title}” has not moved.`,
        };

      default:
        return {
          status: 'unavailable',
          message: `“${title}” could not be moved just now, so it is back where it was. Nothing was changed.`,
        };
    }
  }

  // ---- Reading ----

  private async read(column: ContentBoardColumn, limit: number): Promise<void> {
    const generation = this.generation;
    const first = this.column(column).phase !== 'ready';

    const outcome = await firstValueFrom(this.board.list(this.slug, this.filters, column, null, limit));
    if (generation !== this.generation) return;

    if (outcome.status !== 'found') {
      // A column already on screen stays there: a refresh that failed is not a reason to take cards away.
      if (first) this.patch(column, { phase: 'unavailable' });
      return;
    }

    this.patch(column, {
      phase: 'ready',
      cards: outcome.page.items,
      nextCursor: outcome.page.nextCursor,
      count: outcome.page.columns.find((each) => each.column === column)?.count ?? outcome.page.items.length,
      loadingMore: false,
      moreFailed: false,
    });
  }

  // ---- Placing ----

  private alreadyThere(card: ContentCard, place: ContentCardPlace): boolean {
    if (card.column !== place.column) return false;

    const cards = this.column(card.column).cards;
    const index = cards.findIndex((each) => each.id === card.id);
    const next = cards[index + 1] ?? null;

    // Before itself, or before the card it is already before. The bottom counts only when the whole column
    // is on screen: otherwise "the bottom" is below cards this screen has not read.
    if (place.beforeCardId === card.id) return true;
    if (place.beforeCardId !== null) return next?.id === place.beforeCardId;

    return next === null && this.column(card.column).nextCursor === null;
  }

  private placed(columns: Columns, card: ContentCard, place: ContentCardPlace): Columns {
    const next = { ...columns };
    const source = columns[card.column];
    const sourceCards = source.cards.filter((each) => each.id !== card.id);
    const changedColumn = card.column !== place.column;

    next[card.column] = {
      ...source,
      cards: sourceCards,
      count: changedColumn && source.count !== null ? Math.max(0, source.count - 1) : source.count,
    };

    const target = next[place.column];
    const targetCards = target.cards.filter((each) => each.id !== card.id);
    const index = place.beforeCardId === null ? -1 : targetCards.findIndex((each) => each.id === place.beforeCardId);
    const at = index === -1 ? targetCards.length : index;

    next[place.column] = {
      ...target,
      cards: [...targetCards.slice(0, at), { ...card, column: place.column }, ...targetCards.slice(at)],
      count: changedColumn && target.count !== null ? target.count + 1 : target.count,
    };

    return next;
  }

  private where(place: ContentCardPlace): string {
    const column = CONTENT_BOARD_COLUMN_LABELS[place.column];
    const neighbour = place.beforeCardId === null ? null : this.find(place.beforeCardId);

    return neighbour === null ? `to the bottom of ${column}` : `to ${column}, before “${neighbour.title}”`;
  }

  private replace(card: ContentCard): void {
    const columns = { ...this.columnsState() };
    for (const column of CONTENT_BOARD_COLUMNS) {
      columns[column] = {
        ...columns[column],
        cards: columns[column].cards.map((each) => (each.id === card.id ? card : each)),
      };
    }
    this.columnsState.set(columns);
  }

  private patch(column: ContentBoardColumn, change: Partial<BoardColumnState>): void {
    this.columnsState.update((columns) => ({ ...columns, [column]: { ...columns[column], ...change } }));
  }
}
