import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiQuotaRefusal, decodeAiQuotaRefusal } from '../models/ai-quota.models';
import { AiProposalStatus, decodeAiProposalStatus } from '../models/ai-proposal.models';
import {
  RequestRecipeAdaptationRequest,
  encodeRequestRecipeAdaptationRequest,
} from '../models/recipe-adaptation.models';

export type RequestAdaptationOutcome =
  /** `202`: the request is durable and queued. Nothing has been proposed yet — poll through the proposal route. */
  | { readonly status: 'accepted'; readonly operation: AiProposalStatus; readonly replayed: boolean }
  /** AIREC-005 is a real task, switched off for this deployment. A configuration change, not a client fix. */
  | { readonly status: 'task_not_enabled' }
  /**
   * The pinned version is not the recipe's current version — someone saved while the creator was deciding
   * what to ask for. The remedy is to re-read the recipe and ask again against the version that exists now.
   */
  | { readonly status: 'source_version_invalid' }
  /**
   * A yield goal's multiplier or target yield could not be resolved to a scaling factor — a non-positive
   * value, or a target yield against a recipe whose own yield is not a structured number.
   */
  | { readonly status: 'yield_target_invalid' }
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

// AiAdaptationRequestErrors, mirrored. All three answer 400, so the code is what tells them apart: a task
// nobody switched on is a deployment change, a stale version and an unresolvable yield target are each
// something the creator can act on themselves, in different ways.
const TASK_NOT_ENABLED_CODE = 'ai.recipeAdaptation.not_enabled';
const SOURCE_VERSION_INVALID_CODE = 'ai.recipeAdaptationSource.invalid_request';
const YIELD_TARGET_INVALID_CODE = 'ai.recipeAdaptationYieldTarget.invalid_request';

/**
 * The typed client for asking for an AIREC-005 single-goal adaptation.
 *
 * **One method, because reading the answer is not this route's job.** An adaptation produces an ordinary
 * proposal against the pinned version, which {@link AiProposalService} already polls and decides — so the
 * request id this returns goes straight to `cp-ai-proposal-panel`, the same way a revision's does.
 *
 * The caller owns the `Idempotency-Key` across retries of one logical request; this service never generates
 * one. Asking *again* after a failure is a new request and needs a new key.
 *
 * No component may inject `HttpClient` for these calls (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class RecipeAdaptationService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Asks for a single-goal adaptation of one exact version of a recipe.
   *
   * `202`, never `201`: nothing has been proposed when this returns, and nothing about the recipe changes
   * even when it has. The operation is durable and the model is called by a worker afterwards.
   */
  async requestAdaptation(
    workspaceSlug: string,
    recipeId: string,
    request: RequestRecipeAdaptationRequest,
    idempotencyKey: string,
  ): Promise<RequestAdaptationOutcome> {
    const url = this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}` +
        `/recipes/${encodeURIComponent(recipeId)}/adaptation-requests`,
    );
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestRecipeAdaptationRequest(request), {
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
          case YIELD_TARGET_INVALID_CODE:
            return { status: 'yield_target_invalid' };
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
}
