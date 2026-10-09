import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  CreativeContext,
  CreativeContextDraft,
  CreativeContextPage,
  CreativeContextPatch,
  CreativeContextSource,
  decodeCreativeContext,
  decodeCreativeContextPage,
  encodeCreativeContextDraft,
  encodeCreativeContextPatch,
  encodeCreativeContextSource,
} from '../models/creative-context.models';
import { decodeFieldErrors, problemCodeOf, statusCodeOf } from './ai-request';

export type CreativeContextCreateOutcome =
  | { readonly status: 'created'; readonly context: CreativeContext }
  /** The body failed shape validation. Carries the server's field errors, keyed by request field. */
  | { readonly status: 'invalid'; readonly errors: Record<string, readonly string[]> }
  /**
   * The source, a channel or the theme cannot be used: it does not exist here, or no longer can be pointed at.
   * One answer for every such case, as the route gives — it never says which.
   */
  | { readonly status: 'source_unavailable' }
  | { readonly status: 'forbidden' }
  /** The `Idempotency-Key` was reused for a different body. Generate a fresh one; do not re-send this. */
  | { readonly status: 'key_reused' }
  | { readonly status: 'unavailable' };

export type CreativeContextReadOutcome =
  | { readonly status: 'found'; readonly context: CreativeContext }
  /** Unknown, or another workspace's. One answer for both, as the route gives. */
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

export type CreativeContextListOutcome =
  | { readonly status: 'found'; readonly page: CreativeContextPage }
  /** The cursor was not issued for this workspace's list. Start the list again. */
  | { readonly status: 'cursor_expired' }
  | { readonly status: 'unavailable' };

export type CreativeContextWriteOutcome =
  | { readonly status: 'saved'; readonly context: CreativeContext }
  /** Changed since the read this edit was composed against. Nothing was written: re-read, keep the edit. */
  | { readonly status: 'stale' }
  | { readonly status: 'invalid'; readonly errors: Record<string, readonly string[]> }
  | { readonly status: 'source_unavailable' }
  /** The context already names that source. */
  | { readonly status: 'duplicate' }
  /** The context already names as many sources as one may. */
  | { readonly status: 'limit' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** `ContentErrorCodes`, the ones this client branches on. */
const CODES = {
  cursorInvalid: 'content.creative_context.cursor.invalid',
  stale: 'content.creative_context.stale.conflict',
  duplicate: 'content.creative_context.reference.duplicate.conflict',
  limit: 'content.creative_context.reference_limit.unprocessable',
  keyReused: 'idempotency.key_reused',
} as const;

/**
 * The typed client for creative contexts (AF.1.3): the shared record of what one piece of work is about, which
 * every AI surface reads instead of asking for the recipe, the channel, the day and the picture again.
 *
 * **Nothing here throws for an expected failure.** Each call resolves to an outcome a screen can render, and a
 * response that does not decode is `unavailable` rather than a half-built context.
 *
 * **Edits are optimistic.** Every write carries the `concurrencyToken` of the read it was composed against and
 * returns the whole context with the next one. `stale` means nothing was written — the caller keeps what the
 * creator typed, re-reads, and sends again.
 */
@Injectable({ providedIn: 'root' })
export class CreativeContextService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Starts a context, optionally from one source.
   *
   * The caller owns the `Idempotency-Key` across retries of one logical request: re-sending the same key with
   * the same draft returns the context the first call created rather than a second one.
   */
  create(
    workspaceSlug: string,
    draft: CreativeContextDraft,
    idempotencyKey: string,
  ): Observable<CreativeContextCreateOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return of<CreativeContextCreateOutcome>({ status: 'unavailable' });

    return this.http
      .post<unknown>(url, encodeCreativeContextDraft(draft), {
        withCredentials: true,
        headers: { 'Idempotency-Key': idempotencyKey },
      })
      .pipe(
        map((raw): CreativeContextCreateOutcome => {
          const context = decodeCreativeContext(raw);

          return context ? { status: 'created', context } : { status: 'unavailable' };
        }),
        catchError((error: unknown) => {
          const status = statusCodeOf(error);

          if (status === 400) return of<CreativeContextCreateOutcome>({ status: 'invalid', errors: decodeFieldErrors(error) });
          if (status === 403) return of<CreativeContextCreateOutcome>({ status: 'forbidden' });
          if (status === 422) {
            return of<CreativeContextCreateOutcome>(
              problemCodeOf(error) === CODES.keyReused ? { status: 'key_reused' } : { status: 'source_unavailable' },
            );
          }

          return of<CreativeContextCreateOutcome>({ status: 'unavailable' });
        }),
      );
  }

  /** One context in full. Any member may read; an archived one is still readable. */
  get(workspaceSlug: string, contextId: string): Observable<CreativeContextReadOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(contextId)}`);
    if (!url) return of<CreativeContextReadOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): CreativeContextReadOutcome => {
        const context = decodeCreativeContext(raw);

        return context ? { status: 'found', context } : { status: 'unavailable' };
      }),
      catchError((error: unknown) =>
        of<CreativeContextReadOutcome>(statusCodeOf(error) === 404 ? { status: 'not_found' } : { status: 'unavailable' }),
      ),
    );
  }

  /** One page of live contexts, most recently updated first. Follow `nextCursor` until it is null. */
  list(workspaceSlug: string, cursor: string | null = null, limit: number | null = null): Observable<CreativeContextListOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return of<CreativeContextListOutcome>({ status: 'unavailable' });

    const params: Record<string, string> = {};
    if (cursor) params['cursor'] = cursor;
    if (limit !== null) params['limit'] = String(limit);

    return this.http.get<unknown>(url, { withCredentials: true, params }).pipe(
      map((raw): CreativeContextListOutcome => {
        const page = decodeCreativeContextPage(raw);

        return page ? { status: 'found', page } : { status: 'unavailable' };
      }),
      catchError((error: unknown) =>
        of<CreativeContextListOutcome>(
          statusCodeOf(error) === 400 && problemCodeOf(error) === CODES.cursorInvalid
            ? { status: 'cursor_expired' }
            : { status: 'unavailable' },
        ),
      ),
    );
  }

  /** Edits the creator's words, channels, day, theme or archived state. */
  patch(
    workspaceSlug: string,
    contextId: string,
    patch: CreativeContextPatch,
    expectedConcurrencyToken: string,
  ): Observable<CreativeContextWriteOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(contextId)}`);
    if (!url) return of<CreativeContextWriteOutcome>({ status: 'unavailable' });

    return this.written(
      this.http.patch<unknown>(url, encodeCreativeContextPatch(patch, expectedConcurrencyToken), { withCredentials: true }),
    );
  }

  /** Names one more source, after those the context already names. */
  addReference(
    workspaceSlug: string,
    contextId: string,
    source: CreativeContextSource,
    expectedConcurrencyToken: string,
  ): Observable<CreativeContextWriteOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(contextId)}/references`);
    if (!url) return of<CreativeContextWriteOutcome>({ status: 'unavailable' });

    return this.written(
      this.http.post<unknown>(
        url,
        { expectedConcurrencyToken, reference: encodeCreativeContextSource(source) },
        { withCredentials: true },
      ),
    );
  }

  /**
   * Stops the context naming a source. `referenceId` is the reference's own id, not the id of what it names —
   * and only the reference goes; the recipe, picture or prompt is untouched.
   */
  removeReference(
    workspaceSlug: string,
    contextId: string,
    referenceId: string,
    expectedConcurrencyToken: string,
  ): Observable<CreativeContextWriteOutcome> {
    const url = this.url(
      workspaceSlug,
      `/${encodeURIComponent(contextId)}/references/${encodeURIComponent(referenceId)}`,
    );
    if (!url) return of<CreativeContextWriteOutcome>({ status: 'unavailable' });

    // A query parameter because a DELETE carries no body. HttpClient encodes it, which matters: a base64 token
    // can contain `+` and `/`.
    return this.written(
      this.http.delete<unknown>(url, { withCredentials: true, params: { expectedConcurrencyToken } }),
    );
  }

  /** The one mapping from a write's response to its outcome, shared so the three writes cannot disagree. */
  private written(response: Observable<unknown>): Observable<CreativeContextWriteOutcome> {
    return response.pipe(
      map((raw): CreativeContextWriteOutcome => {
        const context = decodeCreativeContext(raw);

        return context ? { status: 'saved', context } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const status = statusCodeOf(error);
        const code = problemCodeOf(error);

        if (status === 400) return of<CreativeContextWriteOutcome>({ status: 'invalid', errors: decodeFieldErrors(error) });
        if (status === 403) return of<CreativeContextWriteOutcome>({ status: 'forbidden' });
        if (status === 404) return of<CreativeContextWriteOutcome>({ status: 'not_found' });
        if (status === 409) {
          // Stale only when the server says so by name. An unrecognised conflict is not a reason to tell a
          // creator their work changed under them.
          if (code === CODES.stale) return of<CreativeContextWriteOutcome>({ status: 'stale' });
          if (code === CODES.duplicate) return of<CreativeContextWriteOutcome>({ status: 'duplicate' });
        }
        if (status === 422) {
          return of<CreativeContextWriteOutcome>(code === CODES.limit ? { status: 'limit' } : { status: 'source_unavailable' });
        }

        return of<CreativeContextWriteOutcome>({ status: 'unavailable' });
      }),
    );
  }

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/creative-contexts${suffix}`);
  }
}
