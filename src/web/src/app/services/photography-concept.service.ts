import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { decodeAiProposalStatus } from '../models/ai-proposal.models';
import {
  PHOTOGRAPHY_CONCEPT_NOT_ENABLED_CODE,
  PHOTOGRAPHY_CONCEPT_RECIPE_NOT_FOUND_CODE,
  RequestPhotographyConceptsRequest,
  encodeRequestPhotographyConcepts,
} from '../models/photography-concept.models';
import {
  AiRequestOutcome,
  AiWatchOperationOutcome,
  mapAiRequestError,
  mapAiWatchError,
} from './ai-request';

/**
 * The typed client for IMG-001: ask for photography concepts, and read where that request has got to.
 *
 * Two routes and no third. **There is no disposition route and no cancel route** — a concept is something a
 * creator looks at and chooses, and the server offers asking and reading and nothing else. A client that wanted
 * to stop a generation can only stop watching it, which is what the panel's own button says.
 *
 * The caller owns the `Idempotency-Key` across retries of one logical request: generation spends a provider
 * budget, so a retried ask must return the first answer rather than buy a second.
 *
 * No component injects `HttpClient` for these calls (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class PhotographyConceptService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async request(
    workspaceSlug: string,
    request: RequestPhotographyConceptsRequest,
    idempotencyKey: string,
  ): Promise<AiRequestOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestPhotographyConcepts(request), {
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
      return mapAiRequestError(error, PHOTOGRAPHY_CONCEPT_NOT_ENABLED_CODE, [
        PHOTOGRAPHY_CONCEPT_RECIPE_NOT_FOUND_CODE,
      ]);
    }
  }

  /** Where one request has got to, and the concepts once there are any. Never errors the observable. */
  watch(workspaceSlug: string, requestId: string): Observable<AiWatchOperationOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(requestId)}`);
    if (!url) return of<AiWatchOperationOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): AiWatchOperationOutcome => {
        const operation = decodeAiProposalStatus(raw);
        return operation ? { status: 'found', operation } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => of(mapAiWatchError(error))),
    );
  }

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/photography-concept-requests${suffix}`,
    );
  }
}
