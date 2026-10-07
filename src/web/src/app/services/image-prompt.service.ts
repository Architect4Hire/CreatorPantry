import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { decodeAiProposalStatus } from '../models/ai-proposal.models';
import {
  IMAGE_PROMPT_BRIEF_NOT_FOUND_CODE,
  IMAGE_PROMPT_CONCEPT_NOT_FOUND_CODE,
  IMAGE_PROMPT_NOT_ENABLED_CODE,
  RequestImagePromptRequest,
  encodeRequestImagePrompt,
} from '../models/image-prompt.models';
import {
  AiRequestOutcome,
  AiWatchOperationOutcome,
  mapAiRequestError,
  mapAiWatchError,
} from './ai-request';

/**
 * The typed client for IMG-002: compose an editable image prompt for one shot of one concept, and read where
 * that request has got to.
 *
 * **What comes back is a draft, not a decision.** The creator's edited text is the prompt that counts; this
 * client neither knows nor cares which, and nothing here saves one anywhere.
 *
 * Two routes, no disposition and no cancel, for the reason {@link PhotographyConceptService} gives.
 */
@Injectable({ providedIn: 'root' })
export class ImagePromptService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async request(
    workspaceSlug: string,
    request: RequestImagePromptRequest,
    idempotencyKey: string,
  ): Promise<AiRequestOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestImagePrompt(request), {
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
      return mapAiRequestError(error, IMAGE_PROMPT_NOT_ENABLED_CODE, [
        IMAGE_PROMPT_CONCEPT_NOT_FOUND_CODE,
        IMAGE_PROMPT_BRIEF_NOT_FOUND_CODE,
      ]);
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
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/image-prompt-requests${suffix}`,
    );
  }
}
