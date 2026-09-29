import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiQuotaRefusal, decodeAiQuotaRefusal } from '../models/ai-quota.models';
import {
  AiProposalStatus,
  decodeAiProposalStatus,
} from '../models/ai-proposal.models';
import { RequestRecipeConceptsRequest, encodeRequestRecipeConceptsRequest } from '../models/recipe-concept.models';

export type RequestConceptsOutcome =
  /** `202`: the request is durable and queued. Nothing has been generated yet — poll `watchStatus`. */
  | { readonly status: 'accepted'; readonly operation: AiProposalStatus; readonly replayed: boolean }
  /** AIREC-001 is a real task, switched off for this deployment. A configuration change, not a client fix. */
  | { readonly status: 'task_not_enabled' }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** The `Idempotency-Key` was reused for a different brief. Generate a fresh one; do not re-send this. */
  | { readonly status: 'idempotency_key_conflict' }
  /** Requesting needs the Contributor role. */
  | { readonly status: 'forbidden' }
  /** The account cannot pay for this request: its allowance is spent, or AI is switched off for it. */
  | AiQuotaRefusal
  | { readonly status: 'unavailable' };

export type WatchConceptsOutcome =
  | { readonly status: 'found'; readonly operation: AiProposalStatus }
  /** No such request in this workspace. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body — see ProblemResults.StatusFor server-side. */
function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

/** `ValidationProblemDetails.errors`, keyed by camelCase request field name. */
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

// AiConceptRequestErrors, mirrored. Renaming one of these server-side is a breaking API change, so they are
// named here rather than matched by shape.
const TASK_NOT_ENABLED_CODE = 'ai.recipeConcepts.not_enabled';

function requestsUrl(apiBase: ApiBaseService, workspaceSlug: string, suffix = ''): string | null {
  const path = `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipe-concept-requests${suffix}`;
  return apiBase.url(path);
}

/**
 * The typed client for AIREC-001: ask for concepts from a structured brief, and read where that request has
 * got to. Two routes, both workspace-scoped rather than recipe-scoped — a concept request names no recipe.
 *
 * Deliberately narrower than {@link AiProposalService}: there is no disposition route here. A concept is
 * something a creator looks at and chooses, not a diff they accept or reject field by field, so this client
 * has nothing to confirm back to the server.
 *
 * The caller owns the `Idempotency-Key` across retries of one logical request, the same as every other
 * idempotent command in this app — generation spends a provider budget, so a retried request must return the
 * first answer rather than buy a second.
 *
 * No component may inject `HttpClient` for these calls; this service is the only path (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class RecipeConceptService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** Asks for two to five distinct recipe concepts from a structured brief. */
  async requestConcepts(
    workspaceSlug: string,
    request: RequestRecipeConceptsRequest,
    idempotencyKey: string,
  ): Promise<RequestConceptsOutcome> {
    const url = requestsUrl(this.apiBase, workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestRecipeConceptsRequest(request), {
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
        return problemCodeOf(error) === TASK_NOT_ENABLED_CODE
          ? { status: 'task_not_enabled' }
          : { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      }

      if (code === 422) return { status: 'idempotency_key_conflict' };

      // Before the 403 and 429 fallbacks below: a suspension arrives as a 403 like a role refusal, and a
      // spent allowance as a 429 like the edge's own rate limiter, so only the code tells them apart.
      const refusal = decodeAiQuotaRefusal(error);
      if (refusal) return refusal;

      if (code === 403) return { status: 'forbidden' };
      return { status: 'unavailable' };
    }
  }

  /**
   * Where one request has got to, and the proposed concepts once there are any.
   *
   * Never errors the Observable, the same contract {@link AiProposalService.watchStatus} makes: every outcome
   * is a value, so a polling loop built on this cannot be killed by one failed poll.
   */
  watchStatus(workspaceSlug: string, requestId: string): Observable<WatchConceptsOutcome> {
    const url = requestsUrl(this.apiBase, workspaceSlug, `/${encodeURIComponent(requestId)}`);
    if (!url) return of<WatchConceptsOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): WatchConceptsOutcome => {
        const operation = decodeAiProposalStatus(raw);
        return operation ? { status: 'found', operation } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const code = statusCodeOf(error);
        if (code === 404) return of<WatchConceptsOutcome>({ status: 'not_found' });
        if (code === 403) return of<WatchConceptsOutcome>({ status: 'forbidden' });
        return of<WatchConceptsOutcome>({ status: 'unavailable' });
      }),
    );
  }
}
