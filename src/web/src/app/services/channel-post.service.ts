import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { decodeAiProposalStatus } from '../models/ai-proposal.models';
import {
  CHANNEL_POST_DECISION_CONFLICT_CODE,
  CHANNEL_POST_SOURCE_STALE_CODE,
  CHANNEL_POST_STALE_CODE,
  CHANNEL_POSTS_CHANNEL_RETIRED_CODE,
  CHANNEL_POSTS_CONTEXT_NOT_FOUND_CODE,
  CHANNEL_POSTS_NOT_ENABLED_CODE,
  ChannelPostDispositionRequest,
  ChannelPostPackage,
  EditChannelPostRequest,
  RequestChannelPostsRequest,
  decodeChannelPostPackage,
  decodeChannelPostRequestStatus,
  encodeChannelPostDisposition,
  encodeEditChannelPost,
  encodeRequestChannelPosts,
} from '../models/channel-post.models';
import {
  AiRequestOutcome,
  AiWatchOperationOutcome,
  mapAiRequestError,
  mapAiWatchError,
  problemCodeOf,
  problemMessageOf,
  statusCodeOf,
} from './ai-request';

/** The posts a piece of work holds, or the one reason they could not be read. */
export type ChannelPostPackageOutcome =
  /** Found. `package` is null for a piece of work nothing has been written for yet. */
  | { readonly status: 'found'; readonly package: ChannelPostPackage | null }
  /** No such piece of work here — which is also the answer for another workspace's. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** What one write to a channel did. Every failure a creator can act on has its own member. */
export type ChannelPostWriteOutcome =
  | { readonly status: 'saved'; readonly package: ChannelPostPackage }
  /**
   * The post moved on since the creator opened it — another tab, another device, or a decision in between.
   * Their words are untouched; the remedy is to reload and send them again.
   */
  | { readonly status: 'stale' }
  /** Nothing is awaiting that decision. The server's sentence says what the channel is doing instead. */
  | { readonly status: 'decision_conflict'; readonly message: string }
  /** Accepting would pin words to a recipe version that is no longer the latest. */
  | { readonly status: 'source_stale'; readonly message: string }
  /** The request is the wrong shape, or names a channel posts are not written for. */
  | { readonly status: 'refused'; readonly message: string }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/**
 * The typed client for AF.6.4: asking for posts, following that request, and reading, editing and deciding
 * about the posts themselves.
 *
 * **Two resources, deliberately.** A request is an AI operation and lives under `channel-post-requests`; the
 * posts are the piece of work's own and live under its creative context, because the package outlives every
 * request made for it. So this client asks in one place and writes in another, exactly as the routes do.
 *
 * **It measures nothing and decides nothing.** The count beside a stored revision was measured by the
 * channel's writing profile server-side, and which revision may be accepted is the server's rule; this client
 * carries both and reads the server's refusal where it says no.
 */
@Injectable({ providedIn: 'root' })
export class ChannelPostService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** Asks for one post per named channel. The key is required: writing posts spends the allowance. */
  async request(
    workspaceSlug: string,
    request: RequestChannelPostsRequest,
    idempotencyKey: string,
  ): Promise<AiRequestOutcome> {
    const url = this.requestsUrl(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestChannelPosts(request), {
          withCredentials: true,
          observe: 'response',
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const operation = decodeAiProposalStatus(response.body);

      return operation
        ? { status: 'accepted', operation, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      return mapAiRequestError(error, CHANNEL_POSTS_NOT_ENABLED_CODE, [
        CHANNEL_POSTS_CONTEXT_NOT_FOUND_CODE,
        CHANNEL_POSTS_CHANNEL_RETIRED_CODE,
      ]);
    }
  }

  /**
   * Where one request has got to.
   *
   * It decodes the request half only, so {@link AiOperationTracker} can follow it unchanged. The posts the
   * request produced are read from the piece of work itself — one place the package comes from, rather than
   * two readings free to disagree about which is newer.
   */
  watch(workspaceSlug: string, requestId: string): Observable<AiWatchOperationOutcome> {
    const url = this.requestsUrl(workspaceSlug, `/${encodeURIComponent(requestId)}`);
    if (!url) return of<AiWatchOperationOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): AiWatchOperationOutcome => {
        const status = decodeChannelPostRequestStatus(raw);
        return status ? { status: 'found', operation: status.request } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => of(mapAiWatchError(error))),
    );
  }

  /** The posts written for one piece of work. A `204` is a piece of work with none yet, not an absence. */
  async readPackage(workspaceSlug: string, contextId: string): Promise<ChannelPostPackageOutcome> {
    const url = this.postsUrl(workspaceSlug, contextId);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.get<unknown>(url, { withCredentials: true, observe: 'response' }),
      );

      if (response.status === 204 || response.body === null) return { status: 'found', package: null };

      const decoded = decodeChannelPostPackage(response.body);

      return decoded ? { status: 'found', package: decoded } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 404) return { status: 'not_found' };
      if (code === 403) return { status: 'forbidden' };

      return { status: 'unavailable' };
    }
  }

  /** Replaces one channel's words with the creator's own, as a new revision. */
  edit(
    workspaceSlug: string,
    contextId: string,
    channelKey: string,
    request: EditChannelPostRequest,
  ): Promise<ChannelPostWriteOutcome> {
    const url = this.postsUrl(workspaceSlug, contextId, `/${encodeURIComponent(channelKey)}`);

    return this.write(() =>
      url === null
        ? Promise.reject(new Error('No API base.'))
        : firstValueFrom(
            this.http.patch<unknown>(url, encodeEditChannelPost(request), { withCredentials: true }),
          ),
    );
  }

  /**
   * Accepts, rejects or reaffirms one channel.
   *
   * Regenerating is not here: it spends the allowance and answers with an operation to poll, so it goes
   * through {@link request} naming the one channel — which is what leaves the neighbours alone.
   */
  decide(
    workspaceSlug: string,
    contextId: string,
    channelKey: string,
    request: ChannelPostDispositionRequest,
  ): Promise<ChannelPostWriteOutcome> {
    const url = this.postsUrl(
      workspaceSlug,
      contextId,
      `/${encodeURIComponent(channelKey)}/disposition`,
    );

    return this.write(() =>
      url === null
        ? Promise.reject(new Error('No API base.'))
        : firstValueFrom(
            this.http.post<unknown>(url, encodeChannelPostDisposition(request), { withCredentials: true }),
          ),
    );
  }

  /**
   * One write, and the outcome it means.
   *
   * The three conflicts are told apart by their stable codes rather than by the `409` they share, because the
   * remedy differs for each: reload, look at what the channel is doing, or re-pin the recipe.
   */
  private async write(send: () => Promise<unknown>): Promise<ChannelPostWriteOutcome> {
    try {
      const decoded = decodeChannelPostPackage(await send());

      return decoded ? { status: 'saved', package: decoded } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      const problem = problemCodeOf(error);

      if (problem === CHANNEL_POST_STALE_CODE) return { status: 'stale' };

      if (problem === CHANNEL_POST_DECISION_CONFLICT_CODE) {
        return {
          status: 'decision_conflict',
          message: problemMessageOf(error) ?? 'This post has nothing awaiting that decision.',
        };
      }

      if (problem === CHANNEL_POST_SOURCE_STALE_CODE) {
        return {
          status: 'source_stale',
          message:
            problemMessageOf(error) ??
            'The recipe has changed since this post was written. Edit or regenerate it before accepting.',
        };
      }

      if (code === 404) return { status: 'not_found' };
      if (code === 403) return { status: 'forbidden' };

      if (code === 400 || code === 422) {
        return { status: 'refused', message: problemMessageOf(error) ?? 'That could not be saved as sent.' };
      }

      return { status: 'unavailable' };
    }
  }

  private requestsUrl(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/channel-post-requests${suffix}`,
    );
  }

  private postsUrl(workspaceSlug: string, contextId: string, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/creative-contexts/` +
        `${encodeURIComponent(contextId)}/posts${suffix}`,
    );
  }
}
