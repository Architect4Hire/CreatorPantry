import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { decodeAiProposalStatus } from '../models/ai-proposal.models';
import { DISH_FACETS_NOT_ENABLED_CODE, encodeRequestDishFacets } from '../models/dish-facet.models';
import {
  AiRequestOutcome,
  AiWatchOperationOutcome,
  mapAiRequestError,
  mapAiWatchError,
} from './ai-request';

/**
 * The typed client for reading a dish name into a cuisine, a dish type and a method: ask, and read where that
 * request has got to.
 *
 * Two routes and no third, as for {@link PhotographyConceptService}: a reading is a suggestion the caller fills
 * a creator's own controls from, so there is nothing to accept back to the server.
 *
 * The caller owns the `Idempotency-Key` across retries of one logical request, which matters more here than on
 * most of these routes: this is asked for as a creator arrives at a step, not by a button they pressed once.
 *
 * No component injects `HttpClient` for these calls (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class DishFacetService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async request(workspaceSlug: string, dishName: string, idempotencyKey: string): Promise<AiRequestOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestDishFacets(dishName), {
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
      return mapAiRequestError(error, DISH_FACETS_NOT_ENABLED_CODE, []);
    }
  }

  /** Where one request has got to, and the reading once there is one. Never errors the observable. */
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
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/dish-facet-requests${suffix}`,
    );
  }
}
