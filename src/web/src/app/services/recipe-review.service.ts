import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiQuotaRefusal, decodeAiQuotaRefusal } from '../models/ai-quota.models';
import { AiProposalStatus, decodeAiProposalStatus } from '../models/ai-proposal.models';
import { RequestRecipeReviewRequest, encodeRequestRecipeReviewRequest } from '../models/recipe-review.models';

export type RequestReviewOutcome =
  /** `202`: the request is durable and queued. Nothing has been proposed yet — poll `watchStatus`. */
  | { readonly status: 'accepted'; readonly operation: AiProposalStatus; readonly replayed: boolean }
  /** AIREC-006 is a real task, switched off for this deployment. A configuration change, not a client fix. */
  | { readonly status: 'task_not_enabled' }
  /**
   * The pinned version is not the recipe's current version — someone saved while the creator was deciding
   * to ask. The remedy is to re-read the recipe and ask again against the version that exists now.
   */
  | { readonly status: 'source_version_invalid' }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** The `Idempotency-Key` was reused for a different request. Generate a fresh one; do not re-send this. */
  | { readonly status: 'idempotency_key_conflict' }
  /** Requesting needs the Contributor role. */
  | { readonly status: 'forbidden' }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  /** The account cannot pay for this request: its allowance is spent, or AI is switched off for it. */
  | AiQuotaRefusal
  | { readonly status: 'unavailable' };

export type WatchReviewOutcome =
  | { readonly status: 'found'; readonly operation: AiProposalStatus }
  /** No such request on this recipe, in this workspace. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

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

// AiRecipeReviewRequestErrors, mirrored.
const TASK_NOT_ENABLED_CODE = 'ai.recipeReview.not_enabled';
const SOURCE_VERSION_INVALID_CODE = 'ai.recipeReviewSource.invalid_request';

/**
 * The typed client for asking for an AIREC-006 review, and for reading it back.
 *
 * **Two methods, for the same reason `RecipeSubstitutionService` has two.** A review is advisory and its
 * operation is deliberately refused by the generic `ai-proposals` route, so this service owns reading it back
 * too, through this route's own `GET`.
 *
 * The caller owns the `Idempotency-Key` across retries of one logical request; this service never generates
 * one. No component may inject `HttpClient` for these calls (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class RecipeReviewService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private url(workspaceSlug: string, recipeId: string, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}` +
        `/recipes/${encodeURIComponent(recipeId)}/review-requests${suffix}`,
    );
  }

  /**
   * Asks for a review of one exact version of a recipe.
   *
   * `202`, never `201`: nothing has been proposed when this returns, and nothing about the recipe ever
   * changes because of it — a review is a review, never an edit.
   */
  async requestReview(
    workspaceSlug: string,
    recipeId: string,
    request: RequestRecipeReviewRequest,
    idempotencyKey: string,
  ): Promise<RequestReviewOutcome> {
    const url = this.url(workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestRecipeReviewRequest(request), {
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
      const code = statusCodeOf(error);

      if (code === 400) {
        switch (problemCodeOf(error)) {
          case TASK_NOT_ENABLED_CODE:
            return { status: 'task_not_enabled' };
          case SOURCE_VERSION_INVALID_CODE:
            return { status: 'source_version_invalid' };
          default:
            return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
        }
      }

      if (code === 422) return { status: 'idempotency_key_conflict' };

      // Before the 403 and 429 fallbacks below: a suspension arrives as a 403 like a role refusal, and a
      // spent allowance as a 429 like the edge's own rate limiter, so only the code tells them apart.
      const refusal = decodeAiQuotaRefusal(error);
      if (refusal) return refusal;

      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };
      return { status: 'unavailable' };
    }
  }

  /**
   * Where a requested review has got to, and the findings once there are any.
   *
   * Never errors: every outcome is a value, so one failed poll cannot kill a subscription.
   */
  watchStatus(workspaceSlug: string, recipeId: string, requestId: string): Observable<WatchReviewOutcome> {
    const url = this.url(workspaceSlug, recipeId, `/${encodeURIComponent(requestId)}`);
    if (!url) return of<WatchReviewOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): WatchReviewOutcome => {
        const operation = decodeAiProposalStatus(raw);
        return operation ? { status: 'found', operation } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const code = statusCodeOf(error);
        if (code === 404) return of<WatchReviewOutcome>({ status: 'not_found' });
        if (code === 403) return of<WatchReviewOutcome>({ status: 'forbidden' });
        return of<WatchReviewOutcome>({ status: 'unavailable' });
      }),
    );
  }
}
