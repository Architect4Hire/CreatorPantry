import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiProposalStatus, decodeAiProposalStatus } from '../models/ai-proposal.models';
import { RequestRecipeConceptsRequest, encodeRequestRecipeConceptsRequest } from '../models/recipe-concept.models';
import { AiRequestOutcome, mapAiRequestError, problemMessageOf } from './ai-request';

/**
 * What a first draft is asked for from: one concept the creator chose, and the brief as it stands.
 *
 * The two ids travel together or not at all — the server refuses half a concept — which is why they are one
 * required pair here rather than two optional fields.
 */
export interface RequestRecipeDraftRequest {
  /** The concept request the chosen concept came from. */
  readonly sourceConceptRequestId: string;
  /** The chosen concept: its `targetId`, the server-minted id its rows share. */
  readonly sourceConceptId: string;
  /**
   * The creator's brief, sent alongside so the draft keeps the constraints a concept's short summary may not
   * repeat — an exclusion above all. Null when there is none to send, which is the case after a refresh.
   */
  readonly brief: RequestRecipeConceptsRequest | null;
}

// AiFirstDraftRequestErrors, mirrored. Renaming one of these server-side is a breaking API change, so they are
// named here rather than matched by shape.
const TASK_NOT_ENABLED_CODE = 'ai.recipeFirstDraft.not_enabled';

/** The concept is not this workspace's to draft from, or is no longer there. */
export const DRAFT_CONCEPT_NOT_FOUND_CODE = 'ai.recipeConcept.not_found';

/** The brief and the concept together are too long to draft from. Shortening the brief is the remedy. */
export const DRAFT_REQUEST_TOO_LARGE_CODE = 'ai.recipeFirstDraft.too_large';

export type WatchRecipeDraftOutcome =
  | { readonly status: 'found'; readonly operation: AiProposalStatus }
  /** No such request in this workspace, or one belonging to another task — indistinguishable, deliberately. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** One of the creator's own wordings, addressed to the part of the draft it replaces. */
export interface RecipeDraftRewrite {
  /** The id of the change row being rewritten. A real row: the server refuses a rewrite of nothing. */
  readonly changeId: string;
  /** Which field of that row: `displayText`, `text`, `title`, or a recipe-level field's own name. */
  readonly field: string;
  readonly value: string;
}

/** What accepting a whole draft sends: every part, named, and the creator's rewrites of any of them. */
export interface AcceptRecipeDraftRequest {
  /**
   * Every change id on the draft. Naming them is how the request states what was reviewed — the server
   * refuses "accept all" from a client that names fewer parts than the draft holds.
   */
  readonly acceptedChangeIds: readonly string[];
  readonly rewrites: readonly RecipeDraftRewrite[];
}

export type AcceptRecipeDraftOutcome =
  /**
   * A recipe exists for this draft. `replayed` when it already did: the draft had been accepted before, and
   * this is the recipe that acceptance created — never a second one.
   */
  | { readonly status: 'accepted'; readonly recipeId: string; readonly replayed: boolean }
  /** The server would not build a recipe from what was sent. `message` is its own sentence about why. */
  | { readonly status: 'refused'; readonly message: string }
  /** The draft has already been decided another way, or is not ready to be decided. Re-read it. */
  | { readonly status: 'already_decided' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

function requestsUrl(apiBase: ApiBaseService, workspaceSlug: string, suffix = ''): string | null {
  const path = `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/recipe-draft-requests${suffix}`;
  return apiBase.url(path);
}

/**
 * The typed client for reading AIREC-002 first-draft requests.
 *
 * Workspace-scoped rather than recipe-scoped, for the reason {@link RecipeConceptService} is: a first-draft
 * request names no recipe, because there is none until a creator accepts the draft.
 *
 * **One method, and the absences are deliberate.** There is no disposition route for a workspace-level
 * request — the same narrowness `RecipeConceptService` documents — so this client has nothing to confirm back
 * to the server, and discarding a draft is client-side. There is no request method either: nothing in the app
 * asks for a first draft yet, and a typed method with no caller is a contract nobody has tested against the
 * route. It arrives with the surface that calls it.
 *
 * No component may inject `HttpClient` for these calls; this service is the only path
 * (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class RecipeDraftService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /**
   * Where one draft request has got to, and the proposed draft once there is one.
   *
   * Never errors the Observable, the same contract the other two AI clients make: every outcome is a value,
   * so a polling loop built on this cannot be killed by one failed poll.
   *
   * Polling before the draft exists is a `200` carrying a status, not a `404` — the request resource exists
   * from the moment it was accepted, so `not_found` here really does mean no such request.
   */
  /**
   * Asks for a recipe first draft from a chosen concept (AIREC-002). `202` means queued, not written: the
   * draft review page watches the request this returns.
   *
   * The caller owns the `Idempotency-Key` across retries of one logical request. Re-sending the same key
   * returns the request the first call queued rather than buying a second draft.
   */
  async requestDraft(
    workspaceSlug: string,
    request: RequestRecipeDraftRequest,
    idempotencyKey: string,
  ): Promise<AiRequestOutcome> {
    const url = requestsUrl(this.apiBase, workspaceSlug);
    if (!url) return { status: 'unavailable' };

    const body = {
      ...(request.brief ? encodeRequestRecipeConceptsRequest(request.brief) : {}),
      sourceConceptRequestId: request.sourceConceptRequestId,
      sourceConceptId: request.sourceConceptId,
    };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, body, {
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
      // The two refusals are named so they keep the server's own sentence: "too large" would otherwise read as
      // a field error with no field, and "not found" as the server being unreachable.
      return mapAiRequestError(error, TASK_NOT_ENABLED_CODE, [
        DRAFT_CONCEPT_NOT_FOUND_CODE,
        DRAFT_REQUEST_TOO_LARGE_CODE,
      ]);
    }
  }

  /**
   * Accepts a reviewed draft, whole, into a new recipe (AIREC-002/007). The only call that creates a recipe
   * from a generated draft.
   *
   * **No idempotency key, on purpose.** The route recognises a draft that has already been decided and
   * answers with the recipe that decision created, so re-sending after a lost response is safe by the
   * server's own rule. A key minted here would be a second, weaker account of the same guarantee.
   */
  async acceptDraft(
    workspaceSlug: string,
    requestId: string,
    request: AcceptRecipeDraftRequest,
  ): Promise<AcceptRecipeDraftOutcome> {
    const url = requestsUrl(this.apiBase, workspaceSlug, `/${encodeURIComponent(requestId)}/acceptance`);
    if (!url) return { status: 'unavailable' };

    const body = {
      decision: 'AcceptAll',
      acceptedChangeIds: [...request.acceptedChangeIds],
      edits: request.rewrites.map((rewrite) => ({
        changeId: rewrite.changeId,
        field: rewrite.field,
        value: rewrite.value,
      })),
    };

    try {
      const raw = await firstValueFrom(this.http.post<unknown>(url, body, { withCredentials: true }));
      const reply = typeof raw === 'object' && raw !== null ? (raw as Record<string, unknown>) : null;
      const recipeId = reply?.['recipeId'];

      if (typeof recipeId === 'string' && recipeId.length > 0) {
        return { status: 'accepted', recipeId, replayed: reply?.['replayed'] === true };
      }

      // A decision with no recipe behind it: the draft was rejected, and this replayed that. Nothing to open.
      return reply !== null && typeof reply['status'] === 'string'
        ? { status: 'already_decided' }
        : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);

      if (code === 400) {
        return {
          status: 'refused',
          message: problemMessageOf(error) ?? 'This draft could not be turned into a recipe as it stands.',
        };
      }

      if (code === 409) return { status: 'already_decided' };
      if (code === 404) return { status: 'not_found' };
      if (code === 403) return { status: 'forbidden' };

      return { status: 'unavailable' };
    }
  }

  watchStatus(workspaceSlug: string, requestId: string): Observable<WatchRecipeDraftOutcome> {
    const url = requestsUrl(this.apiBase, workspaceSlug, `/${encodeURIComponent(requestId)}`);
    if (!url) return of<WatchRecipeDraftOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): WatchRecipeDraftOutcome => {
        const operation = decodeAiProposalStatus(raw);
        return operation ? { status: 'found', operation } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const code = statusCodeOf(error);
        if (code === 404) return of<WatchRecipeDraftOutcome>({ status: 'not_found' });
        if (code === 403) return of<WatchRecipeDraftOutcome>({ status: 'forbidden' });
        return of<WatchRecipeDraftOutcome>({ status: 'unavailable' });
      }),
    );
  }
}
