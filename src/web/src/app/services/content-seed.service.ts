import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  CONTENT_SEED_INVALID_CODE,
  ContentSeed,
  ContentSeedQuery,
  contentSeedQueryParams,
  decodeContentSeed,
} from '../models/content-seed.models';

/**
 * What one seed request came back with.
 *
 * `refused` is its own answer rather than folded into `unavailable`: the server refuses a pinned key that no
 * catalogue has, or that names a retired entry, and it names the field. That is something the creator can act
 * on — a pin to change — where an outage is not, and a client that showed one as the other would either blame
 * the network for a bad pin or silently drop the pin and generate around it.
 */
export type GenerateContentSeedOutcome =
  | { readonly status: 'found'; readonly seed: ContentSeed }
  | { readonly status: 'refused'; readonly fieldErrors: Record<string, readonly string[]> }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body, which tells apart responses that share a status. */
function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

/** `ValidationProblemDetails.errors`, keyed by the request's field name — PascalCase on this route. */
function decodeFieldErrors(error: unknown): Record<string, readonly string[]> {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const errors = body?.['errors'];
  if (typeof errors !== 'object' || errors === null) return {};

  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors as Record<string, unknown>)) {
    if (Array.isArray(messages) && messages.every((message) => typeof message === 'string')) {
      result[field] = messages;
    }
  }
  return result;
}

/**
 * The typed client for the workspace's content-seed generator,
 * `GET /api/v1/workspaces/{workspaceSlug}/content-seeds`.
 *
 * **It reads and writes nothing.** A seed is not persisted anywhere, so there is no detail route, no list and
 * no cache here: every call is a fresh suggestion, and the only way back to an earlier one is its token.
 *
 * **Nothing is cached, deliberately.** The response is `no-store` because it is personalised and different on
 * every call that does not pin a token; holding one would hand the next caller someone else's idea.
 *
 * The workspace comes from the route segment and the caller's membership. No component injects `HttpClient` for
 * this call (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class ContentSeedService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * One seed for `workspaceSlug`, with any facet the creator has already decided pinned.
   *
   * **Returns an observable so the caller can cancel it.** Unsubscribing aborts the request, which is what lets
   * a creator who hit "Try another" twice abandon the first answer instead of racing it against the second.
   */
  generate(workspaceSlug: string, query: ContentSeedQuery = {}): Observable<GenerateContentSeedOutcome> {
    const url = this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/content-seeds`);
    if (!url) return of<GenerateContentSeedOutcome>({ status: 'unavailable' });

    return this.http
      .get<unknown>(url, { withCredentials: true, params: contentSeedQueryParams(query) })
      .pipe(
        map((raw): GenerateContentSeedOutcome => {
          const seed = decodeContentSeed(raw);
          return seed ? { status: 'found', seed } : { status: 'unavailable' };
        }),
        catchError((error: unknown) => {
          // A 400 is the only refusal this route has. Any other status — including a 404 for a workspace the
          // caller cannot see — leaves nothing to act on, and all of them have the same remedy: ask again.
          if (statusCodeOf(error) === 400 && problemCodeOf(error) === CONTENT_SEED_INVALID_CODE) {
            return of<GenerateContentSeedOutcome>({ status: 'refused', fieldErrors: decodeFieldErrors(error) });
          }

          return of<GenerateContentSeedOutcome>({ status: 'unavailable' });
        }),
      );
  }
}
