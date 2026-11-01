import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { decodeAiProposalStatus } from '../models/ai-proposal.models';
import { LONG_FORM_SEO_NOT_ENABLED_CODE, LONG_FORM_SEO_REFUSAL_CODES } from '../models/long-form-seo.models';
import { AiRequestOutcome, AiWatchOperationOutcome, mapAiRequestError, mapAiWatchError } from './ai-request';

/**
 * The typed client for an SEO package about an accepted long-form piece (LF.5.3).
 *
 * Two calls, as for every AI request: ask, and follow what was asked for. Asking spends the account's
 * allowance, so it always carries an `Idempotency-Key`; a retry with the same key returns the first answer
 * and buys nothing more. There is no call that applies a package to anything: it is a set of recommendations
 * a creator reads.
 */
@Injectable({ providedIn: 'root' })
export class LongFormSeoService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** Asks for a package about the accepted revision. The server refuses any other. */
  async request(
    workspaceSlug: string,
    draftId: string,
    revisionNumber: number,
    idempotencyKey: string,
  ): Promise<AiRequestOutcome> {
    const url = this.url(workspaceSlug, draftId, '');
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(
          url,
          { revisionNumber },
          { withCredentials: true, observe: 'response', headers: { 'Idempotency-Key': idempotencyKey } },
        ),
      );
      const operation = decodeAiProposalStatus(response.body);

      return operation
        ? { status: 'accepted', operation, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      return mapAiRequestError(error, LONG_FORM_SEO_NOT_ENABLED_CODE, LONG_FORM_SEO_REFUSAL_CODES);
    }
  }

  /** Where one request has got to. Never throws, so a polling loop cannot be killed by one failed poll. */
  async watch(workspaceSlug: string, draftId: string, requestId: string): Promise<AiWatchOperationOutcome> {
    const url = this.url(workspaceSlug, draftId, `/${encodeURIComponent(requestId)}`);
    if (!url) return { status: 'unavailable' };

    try {
      const operation = decodeAiProposalStatus(
        await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })),
      );

      return operation ? { status: 'found', operation } : { status: 'unavailable' };
    } catch (error) {
      return mapAiWatchError(error);
    }
  }

  private url(workspaceSlug: string, draftId: string, suffix: string): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/long-form-drafts/${encodeURIComponent(draftId)}/seo-package-requests${suffix}`,
    );
  }
}
