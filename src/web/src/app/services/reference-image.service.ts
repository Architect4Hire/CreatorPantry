import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { decodeAiProposalStatus } from '../models/ai-proposal.models';
import {
  REFERENCE_IMAGE_NOT_ENABLED_CODE,
  REFERENCE_IMAGE_NOT_FOUND_CODE,
  RequestReferenceImageRequest,
  encodeRequestReferenceImage,
} from '../models/reference-image.models';
import {
  AiRequestOutcome,
  AiWatchOperationOutcome,
  mapAiRequestError,
  mapAiWatchError,
} from './ai-request';

/**
 * The typed client for IMG-004: ask for a reading of a reference photograph, and read where that request has
 * got to.
 *
 * **It sends no bytes.** The image is named as one of the workspace's own brand source documents, which the
 * creator added through the upload route — where its signature, decoded format, dimensions and size were
 * inspected. A second upload surface here would be a second set of those checks to keep in step and a second
 * place to get authorization wrong.
 *
 * Two routes, no disposition and no cancel, for the reason {@link PhotographyConceptService} gives.
 */
@Injectable({ providedIn: 'root' })
export class ReferenceImageService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async request(
    workspaceSlug: string,
    request: RequestReferenceImageRequest,
    idempotencyKey: string,
  ): Promise<AiRequestOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestReferenceImage(request), {
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
      return mapAiRequestError(error, REFERENCE_IMAGE_NOT_ENABLED_CODE, [REFERENCE_IMAGE_NOT_FOUND_CODE]);
    }
  }

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
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/reference-image-requests${suffix}`,
    );
  }
}
