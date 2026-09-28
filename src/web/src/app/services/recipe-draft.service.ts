import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiProposalStatus, decodeAiProposalStatus } from '../models/ai-proposal.models';

export type WatchRecipeDraftOutcome =
  | { readonly status: 'found'; readonly operation: AiProposalStatus }
  /** No such request in this workspace, or one belonging to another task — indistinguishable, deliberately. */
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
