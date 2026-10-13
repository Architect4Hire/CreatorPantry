import { Observable, of } from 'rxjs';

import {
  CONTENT_BOARD_COLUMNS,
  ContentBoardColumn,
  ContentBoardFilters,
  ContentCard,
  ContentCardDetail,
  ContentCardFields,
  ContentCardPlace,
} from '../models/content-board.models';
import {
  ContentBoardListOutcome,
  ContentCardDeleteOutcome,
  ContentCardReadOutcome,
  ContentCardWriteOutcome,
} from './content-board.service';

/** A card with everything a spec does not care about filled in. */
export function boardCard(id: string, column: ContentBoardColumn, overrides: Partial<ContentCard> = {}): ContentCard {
  return {
    id,
    title: `Card ${id}`,
    descriptionPreview: null,
    descriptionLength: 0,
    column,
    channelKey: null,
    plannedDate: null,
    plannedDay: null,
    cuisineCode: null,
    dishTypeCode: null,
    methodCode: null,
    lineage: {
      creativeContextId: null,
      recipeId: null,
      recipeVersionId: null,
      promptRecordId: null,
      mediaAssetId: null,
      mediaAssetVersionNumber: null,
      socialPackageId: null,
    },
    moveCount: 0,
    hasPackage: false,
    publishing: null,
    createdAt: '2026-10-13T09:00:00Z',
    updatedAt: '2026-10-13T09:00:00Z',
    concurrencyToken: `${id}-t1`,
    ...overrides,
  };
}

/** What a write should answer instead of happening, for a spec about a refusal. */
export type FakeBoardRefusal = Exclude<ContentCardWriteOutcome['status'], 'saved'>;

/**
 * An in-memory stand-in for `ContentBoardService`, for specs only.
 *
 * It behaves like the routes in the ways the board depends on: each column is an ordered list read a page at
 * a time, a move with an old token is `stale` and changes nothing, a move before a card that is not in the
 * target column is `refused`, a successful move gives the card a new token, and one workspace's cards do not
 * resolve under another's slug. `offline` makes every call answer `unavailable` without recording it.
 */
export class FakeContentBoardService {
  offline = false;
  /** Columns whose reads answer `unavailable`, for a spec about one column failing on its own. */
  readonly unreadable = new Set<ContentBoardColumn>();
  /** When set, the next move, create or edit answers with this instead of happening. */
  refuseNext: FakeBoardRefusal | null = null;
  refuseDeleteWith: Exclude<ContentCardDeleteOutcome['status'], 'deleted'> | null = null;

  readonly lists: { slug: string; filters: ContentBoardFilters; column: ContentBoardColumn | null; cursor: string | null; limit: number | null }[] = [];
  readonly moves: { slug: string; cardId: string; place: ContentCardPlace; token: string }[] = [];
  readonly creates: { slug: string; fields: ContentCardFields; column: ContentBoardColumn; key: string }[] = [];
  readonly updates: { slug: string; cardId: string; before: ContentCardFields; after: ContentCardFields; token: string }[] = [];
  readonly removes: { slug: string; cardId: string; token: string; key: string }[] = [];
  readonly details: { slug: string; cardId: string }[] = [];

  private readonly boards = new Map<string, ContentCard[]>();
  private readonly byKey = new Map<string, string>();
  private revision = 1;
  private minted = 0;

  /** Put cards on the "server", in the order each column should read. */
  seed(slug: string, cards: readonly ContentCard[]): void {
    this.boards.set(slug, [...cards]);
  }

  /** What the "server" holds in one column now, in order. */
  stored(slug: string, column: ContentBoardColumn): readonly ContentCard[] {
    return (this.boards.get(slug) ?? []).filter((card) => card.column === column);
  }

  /** Somebody else changes a card: its token moves, and anything given here with it. */
  touch(slug: string, cardId: string, change: Partial<ContentCard> = {}): void {
    this.boards.set(
      slug,
      (this.boards.get(slug) ?? []).map((card) =>
        card.id === cardId ? { ...card, ...change, concurrencyToken: `${cardId}-t${++this.revision}` } : card,
      ),
    );
  }

  /** Somebody else deletes a card. */
  erase(slug: string, cardId: string): void {
    this.boards.set(slug, (this.boards.get(slug) ?? []).filter((card) => card.id !== cardId));
  }

  list(
    slug: string,
    filters: ContentBoardFilters,
    column: ContentBoardColumn | null = null,
    cursor: string | null = null,
    limit: number | null = null,
  ): Observable<ContentBoardListOutcome> {
    if (this.offline || (column !== null && this.unreadable.has(column))) return of({ status: 'unavailable' });

    this.lists.push({ slug, filters, column, cursor, limit });

    const matching = (this.boards.get(slug) ?? []).filter(
      (card) =>
        (filters.channel === null || card.channelKey === filters.channel) &&
        (filters.day === null || card.plannedDay === filters.day) &&
        (filters.date === null || card.plannedDate === filters.date),
    );
    const shown = CONTENT_BOARD_COLUMNS.filter((each) => column === null || each === column);
    const ordered = shown.flatMap((each) => matching.filter((card) => card.column === each));
    const start = cursor === null ? 0 : Number(cursor);
    const size = limit ?? 25;
    const items = ordered.slice(start, start + size);

    return of({
      status: 'found',
      page: {
        items,
        nextCursor: start + size < ordered.length ? String(start + size) : null,
        columns: shown.map((each) => ({ column: each, count: matching.filter((card) => card.column === each).length })),
      },
    });
  }

  detail(slug: string, cardId: string): Observable<ContentCardReadOutcome> {
    if (this.offline) return of({ status: 'unavailable' });

    this.details.push({ slug, cardId });
    const card = this.card(slug, cardId);

    return of(card ? { status: 'found', detail: this.detailOf(card) } : { status: 'not_found' });
  }

  create(slug: string, fields: ContentCardFields, column: ContentBoardColumn, key: string): Observable<ContentCardWriteOutcome> {
    if (this.offline) return of({ status: 'unavailable' });

    this.creates.push({ slug, fields, column, key });
    if (this.refused()) return of(this.refusal());

    const replayed = this.byKey.get(`${slug}|${key}`);
    if (replayed) return of({ status: 'saved', detail: this.detailOf(this.card(slug, replayed)!) });

    const card = boardCard(`new-${++this.minted}`, column, {
      title: fields.title,
      descriptionPreview: fields.description,
      descriptionLength: fields.description?.length ?? 0,
      channelKey: fields.channelKey,
      plannedDate: fields.plannedDate,
    });
    this.boards.set(slug, [...(this.boards.get(slug) ?? []), card]);
    this.byKey.set(`${slug}|${key}`, card.id);

    return of({ status: 'saved', detail: this.detailOf(card) });
  }

  update(
    slug: string,
    cardId: string,
    before: ContentCardFields,
    after: ContentCardFields,
    token: string,
  ): Observable<ContentCardWriteOutcome> {
    if (this.offline) return of({ status: 'unavailable' });

    this.updates.push({ slug, cardId, before, after, token });
    if (this.refused()) return of(this.refusal());

    const card = this.card(slug, cardId);
    if (!card) return of({ status: 'not_found' });
    if (card.concurrencyToken !== token) return of({ status: 'stale' });

    this.touch(slug, cardId, {
      title: after.title,
      descriptionPreview: after.description,
      descriptionLength: after.description?.length ?? 0,
      channelKey: after.channelKey,
      plannedDate: after.plannedDate,
    });

    return of({ status: 'saved', detail: this.detailOf(this.card(slug, cardId)!) });
  }

  move(slug: string, cardId: string, place: ContentCardPlace, token: string): Observable<ContentCardWriteOutcome> {
    if (this.offline) return of({ status: 'unavailable' });

    this.moves.push({ slug, cardId, place, token });
    if (this.refused()) return of(this.refusal());

    const all = this.boards.get(slug) ?? [];
    const card = all.find((each) => each.id === cardId);
    if (!card) return of({ status: 'not_found' });
    if (card.concurrencyToken !== token) return of({ status: 'stale' });

    const rest = all.filter((each) => each.id !== cardId);
    const neighbour = place.beforeCardId === null ? null : rest.find((each) => each.id === place.beforeCardId);
    if (place.beforeCardId !== null && neighbour?.column !== place.column) return of({ status: 'refused', errors: {} });

    const moved: ContentCard = {
      ...card,
      column: place.column,
      moveCount: card.column === place.column ? card.moveCount : card.moveCount + 1,
      concurrencyToken: `${cardId}-t${++this.revision}`,
    };

    if (neighbour) {
      rest.splice(rest.indexOf(neighbour), 0, moved);
    } else {
      // The bottom of its column: after the last card there, wherever that is in the flat list.
      rest.push(moved);
    }
    this.boards.set(slug, rest);

    return of({ status: 'saved', detail: this.detailOf(moved) });
  }

  remove(slug: string, cardId: string, token: string, key: string): Observable<ContentCardDeleteOutcome> {
    if (this.offline) return of({ status: 'unavailable' });

    this.removes.push({ slug, cardId, token, key });
    if (this.refuseDeleteWith) {
      const status = this.refuseDeleteWith;
      this.refuseDeleteWith = null;

      return of({ status });
    }

    const card = this.card(slug, cardId);
    if (!card) return of({ status: 'not_found' });
    if (card.concurrencyToken !== token) return of({ status: 'stale' });

    this.erase(slug, cardId);

    return of({ status: 'deleted' });
  }

  /** What a detail read says beyond the card. A spec sets these to show lineage or history. */
  readonly extras = new Map<string, Partial<Omit<ContentCardDetail, 'card'>>>();

  private detailOf(card: ContentCard): ContentCardDetail {
    return {
      card,
      description: card.descriptionPreview,
      lineage: [],
      history: [{ sequence: 1, fromColumn: null, toColumn: card.column, note: null, occurredAt: card.createdAt }],
      historyTruncated: false,
      ...this.extras.get(card.id),
    };
  }

  private card(slug: string, cardId: string): ContentCard | null {
    return (this.boards.get(slug) ?? []).find((card) => card.id === cardId) ?? null;
  }

  private refused(): boolean {
    return this.refuseNext !== null;
  }

  private refusal(): ContentCardWriteOutcome {
    const status = this.refuseNext!;
    this.refuseNext = null;

    return status === 'invalid' || status === 'refused' ? { status, errors: {} } : { status };
  }
}
