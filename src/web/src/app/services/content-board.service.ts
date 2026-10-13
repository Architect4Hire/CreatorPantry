import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  ContentBoardColumn,
  ContentBoardFilters,
  ContentCardDetail,
  ContentCardFields,
  ContentCardPage,
  ContentCardPlace,
  PipelineBoardCard,
  decodeContentCardDetail,
  decodeContentCardPage,
  decodePipelineBoardCard,
  encodeContentBoardQuery,
  encodeContentCardMove,
  encodeContentCardPatch,
  encodeNewContentCard,
} from '../models/content-board.models';
import { decodeFieldErrors, problemCodeOf, statusCodeOf } from './ai-request';

export type ContentBoardListOutcome =
  | { readonly status: 'found'; readonly page: ContentCardPage }
  /** The cursor was not issued for these filters. Start the column again. */
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'unavailable' };

export type ContentCardReadOutcome =
  | { readonly status: 'found'; readonly detail: ContentCardDetail }
  /** Unknown, deleted, or another workspace's. One answer for all three, as the route gives. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

/** How a create, an edit or a move ended. Anything but `saved` wrote nothing. */
export type ContentCardWriteOutcome =
  | { readonly status: 'saved'; readonly detail: ContentCardDetail }
  /** The card, or a card that had to make room for it, changed first. Read again; the change was not made. */
  | { readonly status: 'stale' }
  | { readonly status: 'invalid'; readonly errors: Record<string, readonly string[]> }
  /** A move's neighbour is no longer in that column, or a linked record can no longer be linked. */
  | { readonly status: 'refused'; readonly errors: Record<string, readonly string[]> }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  /** The `Idempotency-Key` was reused for a different body. Generate a fresh one. */
  | { readonly status: 'key_reused' }
  | { readonly status: 'unavailable' };

export type ContentCardDeleteOutcome =
  | { readonly status: 'deleted' }
  /** The card changed after it was read. Nothing was deleted. */
  | { readonly status: 'stale' }
  /** Already gone: deleted by someone else, or by an earlier attempt whose answer was lost. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** `ContentErrorCodes`, the ones this client branches on. */
const CODES = {
  cursorInvalid: 'content.card.cursor.invalid',
  stale: 'content.card.stale.conflict',
  keyReused: 'idempotency.key_reused',
} as const;

export type PipelineBoardCardReadOutcome =
  | { readonly status: 'found'; readonly card: PipelineBoardCard }
  /**
   * This piece of work has no card the pipeline made. Also the answer for work that is unknown or another
   * workspace's, as the route gives: one answer for all three.
   */
  | { readonly status: 'none' }
  | { readonly status: 'unavailable' };

export type PipelineBoardCardCommitOutcome =
  /** The card is on the board. `created` is false when it already was and this call changed nothing. */
  | { readonly status: 'on_board'; readonly card: PipelineBoardCard; readonly created: boolean }
  /** No picture of this run is in the library yet. Nothing was created. */
  | { readonly status: 'no_saved_picture' }
  /** The piece of work is unknown, archived or another workspace's. One answer for all three. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  /** Nothing was written, and the same call is safe to make again. */
  | { readonly status: 'unavailable' };

/**
 * The typed client for the content board (13.8) and for the Content Pipeline's last step (13.7a).
 *
 * **The server decides everything about a card's place.** A move names a column and the card to go before;
 * which moves are allowed, where the card lands and what history it leaves are the route's answers, and this
 * client only reports them. An edit, a move and a delete each quote the `concurrencyToken` of the read they
 * were composed against; `stale` means nothing was written.
 *
 * **The pipeline's commit carries no body and no key.** The server makes the card from what the piece of work holds, and
 * keeps one such card per piece of work itself — so a retry after a lost answer, or a run resumed days later,
 * is answered with the card that is already there. That is why nothing here remembers that a commit was sent.
 *
 * **Nothing here throws for an expected failure.** Each call resolves to an outcome a screen can render.
 */
@Injectable({ providedIn: 'root' })
export class ContentBoardService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * One page of one column, or of the whole board when `column` is null, in the board's own order.
   *
   * `page.columns` counts every card the filters match in each column, across all pages.
   */
  list(
    workspaceSlug: string,
    filters: ContentBoardFilters,
    column: ContentBoardColumn | null = null,
    cursor: string | null = null,
    limit: number | null = null,
  ): Observable<ContentBoardListOutcome> {
    const url = this.cardsUrl(workspaceSlug);
    if (!url) return of<ContentBoardListOutcome>({ status: 'unavailable' });

    return this.http
      .get<unknown>(url, { withCredentials: true, params: encodeContentBoardQuery(filters, column, cursor, limit) })
      .pipe(
        map((raw): ContentBoardListOutcome => {
          const page = decodeContentCardPage(raw);

          return page ? { status: 'found', page } : { status: 'unavailable' };
        }),
        catchError((error: unknown) =>
          of<ContentBoardListOutcome>(
            statusCodeOf(error) === 400 && problemCodeOf(error) === CODES.cursorInvalid
              ? { status: 'cursor_expired' }
              : { status: 'unavailable' },
          ),
        ),
      );
  }

  /** One card in full: its description, what it points at and its history. Any member may read. */
  detail(workspaceSlug: string, cardId: string): Observable<ContentCardReadOutcome> {
    const url = this.cardsUrl(workspaceSlug, `/${encodeURIComponent(cardId)}`);
    if (!url) return of<ContentCardReadOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): ContentCardReadOutcome => {
        const detail = decodeContentCardDetail(raw);

        return detail ? { status: 'found', detail } : { status: 'unavailable' };
      }),
      catchError((error: unknown) =>
        of<ContentCardReadOutcome>(statusCodeOf(error) === 404 ? { status: 'not_found' } : { status: 'unavailable' }),
      ),
    );
  }

  /**
   * Puts a new card at the bottom of a column.
   *
   * The caller owns the `Idempotency-Key` across retries of one logical request, so a create whose answer was
   * lost is not a second card.
   */
  create(
    workspaceSlug: string,
    fields: ContentCardFields,
    column: ContentBoardColumn,
    idempotencyKey: string,
  ): Observable<ContentCardWriteOutcome> {
    const url = this.cardsUrl(workspaceSlug);
    if (!url) return of<ContentCardWriteOutcome>({ status: 'unavailable' });

    return this.written(
      this.http.post<unknown>(url, encodeNewContentCard(fields, column), {
        withCredentials: true,
        headers: { 'Idempotency-Key': idempotencyKey },
      }),
    );
  }

  /** Changes only the fields that differ from `before`. A field the creator did not touch is not sent. */
  update(
    workspaceSlug: string,
    cardId: string,
    before: ContentCardFields,
    after: ContentCardFields,
    expectedConcurrencyToken: string,
  ): Observable<ContentCardWriteOutcome> {
    const url = this.cardsUrl(workspaceSlug, `/${encodeURIComponent(cardId)}`);
    if (!url) return of<ContentCardWriteOutcome>({ status: 'unavailable' });

    return this.written(
      this.http.patch<unknown>(url, encodeContentCardPatch(before, after, expectedConcurrencyToken), {
        withCredentials: true,
      }),
    );
  }

  /** Asks for a card to be in a column, before a named card or at the bottom. The server decides if it may. */
  move(
    workspaceSlug: string,
    cardId: string,
    place: ContentCardPlace,
    expectedConcurrencyToken: string,
  ): Observable<ContentCardWriteOutcome> {
    const url = this.cardsUrl(workspaceSlug, `/${encodeURIComponent(cardId)}/move`);
    if (!url) return of<ContentCardWriteOutcome>({ status: 'unavailable' });

    return this.written(
      this.http.post<unknown>(url, encodeContentCardMove(place, expectedConcurrencyToken), { withCredentials: true }),
    );
  }

  /**
   * Deletes a card, with its history. **Its linked picture is always kept**: this client never asks for one to
   * be removed, so taking a card off the board cannot take anything out of the library.
   *
   * The key is required by the route, and is what lets a retry after a lost answer be told what happened.
   */
  remove(
    workspaceSlug: string,
    cardId: string,
    expectedConcurrencyToken: string,
    idempotencyKey: string,
  ): Observable<ContentCardDeleteOutcome> {
    const url = this.cardsUrl(workspaceSlug, `/${encodeURIComponent(cardId)}`);
    if (!url) return of<ContentCardDeleteOutcome>({ status: 'unavailable' });

    return this.http
      .delete<unknown>(url, {
        withCredentials: true,
        headers: { 'Idempotency-Key': idempotencyKey },
        body: { confirmed: true, expectedConcurrencyToken },
      })
      .pipe(
        map((): ContentCardDeleteOutcome => ({ status: 'deleted' })),
        catchError((error: unknown) => {
          const status = statusCodeOf(error);

          if (status === 404) return of<ContentCardDeleteOutcome>({ status: 'not_found' });
          if (status === 403) return of<ContentCardDeleteOutcome>({ status: 'forbidden' });
          if (status === 409 && problemCodeOf(error) === CODES.stale) return of<ContentCardDeleteOutcome>({ status: 'stale' });

          return of<ContentCardDeleteOutcome>({ status: 'unavailable' });
        }),
      );
  }

  /** The card the pipeline made for this piece of work, when it has made one. Any member may read. */
  pipelineCard(workspaceSlug: string, contextId: string): Observable<PipelineBoardCardReadOutcome> {
    const url = this.url(workspaceSlug, contextId);
    if (!url) return of<PipelineBoardCardReadOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): PipelineBoardCardReadOutcome => {
        const card = decodePipelineBoardCard(raw);

        return card ? { status: 'found', card } : { status: 'unavailable' };
      }),
      catchError((error: unknown) =>
        of<PipelineBoardCardReadOutcome>(statusCodeOf(error) === 404 ? { status: 'none' } : { status: 'unavailable' }),
      ),
    );
  }

  /** Puts this piece of work on the board as one card, or answers with the card it already has. */
  commitPipeline(workspaceSlug: string, contextId: string): Observable<PipelineBoardCardCommitOutcome> {
    const url = this.url(workspaceSlug, contextId);
    if (!url) return of<PipelineBoardCardCommitOutcome>({ status: 'unavailable' });

    return this.http.post<unknown>(url, null, { withCredentials: true, observe: 'response' }).pipe(
      map((response): PipelineBoardCardCommitOutcome => {
        const card = decodePipelineBoardCard(response.body);

        // 201 is the call that made it; 200 is a repeat of one that already had.
        return card ? { status: 'on_board', card, created: response.status === 201 } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const status = statusCodeOf(error);

        if (status === 422) return of<PipelineBoardCardCommitOutcome>({ status: 'no_saved_picture' });
        if (status === 404) return of<PipelineBoardCardCommitOutcome>({ status: 'not_found' });
        if (status === 403) return of<PipelineBoardCardCommitOutcome>({ status: 'forbidden' });

        return of<PipelineBoardCardCommitOutcome>({ status: 'unavailable' });
      }),
    );
  }

  /** The one mapping from a write's response to its outcome, shared so the three writes cannot disagree. */
  private written(response: Observable<unknown>): Observable<ContentCardWriteOutcome> {
    return response.pipe(
      map((raw): ContentCardWriteOutcome => {
        const detail = decodeContentCardDetail(raw);

        return detail ? { status: 'saved', detail } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const status = statusCodeOf(error);
        const code = problemCodeOf(error);

        if (status === 400) return of<ContentCardWriteOutcome>({ status: 'invalid', errors: decodeFieldErrors(error) });
        if (status === 403) return of<ContentCardWriteOutcome>({ status: 'forbidden' });
        if (status === 404) return of<ContentCardWriteOutcome>({ status: 'not_found' });

        // Stale only when the server says so by name. An unrecognised conflict is "not written, try again",
        // not a reason to tell a creator the board changed under them.
        if (status === 409 && code === CODES.stale) return of<ContentCardWriteOutcome>({ status: 'stale' });
        if (status === 422) {
          return of<ContentCardWriteOutcome>(
            code === CODES.keyReused ? { status: 'key_reused' } : { status: 'refused', errors: decodeFieldErrors(error) },
          );
        }

        return of<ContentCardWriteOutcome>({ status: 'unavailable' });
      }),
    );
  }

  private cardsUrl(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/content-cards${suffix}`);
  }

  private url(workspaceSlug: string, contextId: string): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/creative-contexts/${encodeURIComponent(contextId)}/board-card`,
    );
  }
}
