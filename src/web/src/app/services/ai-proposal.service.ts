import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  AiProposalDispositionRequest,
  AiProposalDispositionResult,
  AiProposalStatus,
  RequestAiProposalRequest,
  decodeAiProposalDispositionResult,
  decodeAiProposalStatus,
  encodeAiProposalDispositionRequest,
  encodeRequestAiProposalRequest,
} from '../models/ai-proposal.models';

export type RequestAiProposalOutcome =
  /** `202`: the request is durable and queued. Nothing has been generated yet — poll `watchStatus`. */
  | { readonly status: 'accepted'; readonly operation: AiProposalStatus; readonly replayed: boolean }
  /** No such task. Enabling something will not help; the discriminator is wrong. */
  | { readonly status: 'task_unknown' }
  /** A real task this deployment has not switched on. A configuration change, not a client fix. */
  | { readonly status: 'task_not_enabled' }
  /** The pinned version is not the recipe's current version — someone saved while this was being asked. */
  | { readonly status: 'source_version_invalid' }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** The `Idempotency-Key` was reused for a different request. Generate a fresh one; do not re-send this. */
  | { readonly status: 'idempotency_key_conflict' }
  /** Requesting needs the Contributor role. */
  | { readonly status: 'forbidden' }
  /** Unknown recipe, or one belonging to another workspace — deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  | { readonly status: 'conflict' }
  | { readonly status: 'unavailable' };

export type AiProposalStatusOutcome =
  | { readonly status: 'found'; readonly operation: AiProposalStatus }
  /** No such request on this recipe, in this workspace. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

export type AiProposalDispositionOutcome =
  | { readonly status: 'decided'; readonly result: AiProposalDispositionResult }
  /**
   * The confirmation named a change this proposal does not contain, or an accept-all did not name every
   * change. Nothing was written — an invalid selection must not apply the part of itself that was valid.
   */
  | { readonly status: 'selection_invalid'; readonly message: string }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /**
   * The proposal has already been decided, or never reached a state where it could be. Terminal states do
   * not reopen, so re-read the status rather than retrying.
   */
  | { readonly status: 'already_decided' }
  /**
   * The recipe has moved past the version this proposal was computed against. Every "was" value the creator
   * reviewed describes content that is no longer there, so the only recovery is a fresh request against the
   * current version — never a silent rebase.
   */
  | { readonly status: 'source_stale' }
  /** The recipe is archived and will not accept content changes. Unarchive first; retrying cannot help. */
  | { readonly status: 'recipe_archived' }
  /** A member of the workspace whose role may not decide a proposal's fate. */
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

/** The stable `code` extension on a ProblemDetails body, which is what distinguishes one 400 or 409 from another. */
function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

/** `ProblemDetails.title`, which carries the server's own sentence about a refusal. */
function problemMessageOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const title = body?.['title'];
  return typeof title === 'string' ? title : null;
}

/** `ValidationProblemDetails.errors`, keyed by camelCase request field name (`OperationError.FieldErrors`). */
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

// AiProposalErrors, mirrored. Renaming one of these server-side is a breaking API change, so they are named
// here rather than matched by shape: the dotted suffix is also what selects the HTTP status, and a client that
// went by status alone could not tell three different 409s apart.
const TASK_UNKNOWN_CODE = 'ai.task.unknown';
const TASK_NOT_ENABLED_CODE = 'ai.task.not_enabled';
const SOURCE_VERSION_INVALID_CODE = 'ai.sourceVersion.invalid_request';
const SELECTION_INVALID_CODE = 'ai.selection.invalid_request';
const PROPOSAL_DECIDED_CODE = 'ai.proposal.conflict';

// RecipeErrorCodes, passed through unchanged by the AI module because accepted changes travel the recipe
// module's own facade. A stale source and an archived recipe are both 409s with opposite remedies: one says
// "start again from the current recipe", the other says "unarchive first, then this will work".
const RECIPE_STALE_CODE = 'recipes.recipe.conflict';
const RECIPE_ARCHIVED_CODE = 'recipes.archived.conflict';

function proposalsUrl(apiBase: ApiBaseService, workspaceSlug: string, recipeId: string, suffix = ''): string | null {
  const path =
    `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}` +
    `/recipes/${encodeURIComponent(recipeId)}/ai-proposals${suffix}`;
  return apiBase.url(path);
}

/**
 * The typed client for the Phase 8 AI proposal lifecycle: ask, watch, decide.
 *
 * Three routes, and the client's part in each is deliberately small. It names an allow-listed task
 * discriminator and a source version and nothing else; it reads a status it does not interpret; and it
 * confirms a decision by naming ids the server itself computed. No prompt, no model, no template and no
 * workspace can be expressed through this service, because none of them is a field on either request.
 *
 * The caller owns the `Idempotency-Key` across retries of one logical request — this service never generates
 * or caches one. Asking *again* after a failure is a new request and needs a new key: a retried operation is
 * a new row server-side, because a row that changed its mind about what happened to it would make the
 * provenance recorded against an accepted proposal ambiguous.
 *
 * No component may inject `HttpClient` for these calls; this service is the only path
 * (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class AiProposalService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Asks for a proposal against one exact version of a recipe.
   *
   * `202`, never `201`: nothing has been generated when this returns. The operation is durable and the model
   * is called by a worker afterwards, so the outcome carries a status to poll rather than a result.
   */
  async requestProposal(
    workspaceSlug: string,
    recipeId: string,
    request: RequestAiProposalRequest,
    idempotencyKey: string,
  ): Promise<RequestAiProposalOutcome> {
    const url = proposalsUrl(this.apiBase, workspaceSlug, recipeId);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestAiProposalRequest(request), {
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
        // Four different 400s with four different remedies. Enabling a task is a deployment change, a
        // misspelled discriminator is a bug, and a stale source version means the recipe was saved while the
        // creator was deciding to ask — which is the one a creator can act on, by asking again.
        switch (problemCodeOf(error)) {
          case TASK_UNKNOWN_CODE:
            return { status: 'task_unknown' };
          case TASK_NOT_ENABLED_CODE:
            return { status: 'task_not_enabled' };
          case SOURCE_VERSION_INVALID_CODE:
            return { status: 'source_version_invalid' };
          default:
            return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
        }
      }

      // The key was reused for a different payload. Not a field-validation failure: the caller needs a fresh
      // key, not a corrected form.
      if (code === 422) return { status: 'idempotency_key_conflict' };
      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };
      if (code === 409) return { status: 'conflict' };
      return { status: 'unavailable' };
    }
  }

  /**
   * Where one request has got to, and the proposal itself once there is one.
   *
   * An Observable where the two commands return Promises, and that is the point rather than an
   * inconsistency: this is what a panel polls, and unsubscribing an `HttpClient` request aborts it. A
   * watcher that is torn down — the creator navigated away, the operation reached a terminal state — leaves
   * no request in flight.
   *
   * It never errors. Every outcome is a value, so one failed poll cannot kill the subscription and leave a
   * panel unable to ask again without being rebuilt.
   *
   * Polling before a proposal exists is a `200` carrying a status, not a `404`: the request resource exists
   * from the moment it was accepted, so `not_found` here really does mean no such request.
   */
  watchStatus(workspaceSlug: string, recipeId: string, requestId: string): Observable<AiProposalStatusOutcome> {
    const url = proposalsUrl(this.apiBase, workspaceSlug, recipeId, `/${encodeURIComponent(requestId)}`);
    if (!url) return of<AiProposalStatusOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): AiProposalStatusOutcome => {
        const operation = decodeAiProposalStatus(raw);
        return operation ? { status: 'found', operation } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const code = statusCodeOf(error);
        if (code === 404) return of<AiProposalStatusOutcome>({ status: 'not_found' });
        if (code === 403) return of<AiProposalStatusOutcome>({ status: 'forbidden' });
        return of<AiProposalStatusOutcome>({ status: 'unavailable' });
      }),
    );
  }

  /**
   * Records what the creator decided, and applies what they accepted.
   *
   * Addressed by the **request** id — the same one `watchStatus` takes. An operation has at most one
   * proposal, so the request identifies it without ambiguity.
   *
   * Finishes before it returns: there is no provider call, and the recipe version, the per-change decisions,
   * the feedback and the operation's terminal status all commit in one transaction server-side. Retrying is
   * safe and needs no idempotency key — the same decision is recognised and answered without writing, and a
   * *different* decision is a conflict rather than a second version of the recipe.
   */
  async disposition(
    workspaceSlug: string,
    recipeId: string,
    requestId: string,
    request: AiProposalDispositionRequest,
  ): Promise<AiProposalDispositionOutcome> {
    const url = proposalsUrl(
      this.apiBase,
      workspaceSlug,
      recipeId,
      `/${encodeURIComponent(requestId)}/disposition`,
    );
    if (!url) return { status: 'unavailable' };

    try {
      const raw = await firstValueFrom(
        this.http.post<unknown>(url, encodeAiProposalDispositionRequest(request), { withCredentials: true }),
      );
      const result = decodeAiProposalDispositionResult(raw);
      return result ? { status: 'decided', result } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);

      if (code === 400) {
        // The server's own sentence is carried through for a selection refusal: it names which change was
        // the problem, and paraphrasing it here would tell the creator less than the server already said.
        return problemCodeOf(error) === SELECTION_INVALID_CODE
          ? {
              status: 'selection_invalid',
              message: problemMessageOf(error) ?? 'Those changes could not be accepted as selected.',
            }
          : { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
      }

      if (code === 409) {
        // Three 409s, three opposite remedies — see the code constants above.
        switch (problemCodeOf(error)) {
          case RECIPE_STALE_CODE:
            return { status: 'source_stale' };
          case RECIPE_ARCHIVED_CODE:
            return { status: 'recipe_archived' };
          case PROPOSAL_DECIDED_CODE:
          default:
            return { status: 'already_decided' };
        }
      }

      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };
      return { status: 'unavailable' };
    }
  }
}
