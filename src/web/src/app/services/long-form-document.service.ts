import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiQuotaRefusal, decodeAiQuotaRefusal } from '../models/ai-quota.models';
import {
  AcceptLongFormDocumentRequest,
  EditLongFormSectionRequest,
  LONG_FORM_ACCEPTANCE_CONFIRMATION_REQUIRED_CODE,
  LONG_FORM_SECTION_NOT_ENABLED_CODE,
  LongFormAcceptanceIssueDetail,
  LongFormAssembledDocument,
  LongFormDocumentReview,
  LongFormSectionWritingRequest,
  RequestLongFormSectionRequest,
  SettleLongFormSectionRequest,
  decodeLongFormAcceptanceIssues,
  decodeLongFormAssembledDocument,
  decodeLongFormDocumentReview,
  decodeLongFormSectionWritingRequest,
  encodeAcceptLongFormDocument,
  encodeEditLongFormSection,
  encodeRequestLongFormSection,
  encodeSettleLongFormSection,
} from '../models/long-form-document.models';
import { LONG_FORM_DRAFT_STALE_CODE } from '../models/long-form-draft.models';
import { isRecord } from '../models/recipe.models';
import { decodeFieldErrors, problemCodeOf, problemMessageOf, statusCodeOf } from './ai-request';

/** The two files an accepted piece can be downloaded as (LF.5.2). */
export type LongFormExportFormat = 'markdown' | 'html';

/** The document read whole, or the one reason it could not be. */
export type LongFormDocumentOutcome =
  | { readonly status: 'found'; readonly review: LongFormDocumentReview }
  /** No such draft here — which is also the answer for another workspace's. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** What asking for one section, or for the rest, did. */
export type LongFormWritingOutcome =
  /** Queued. `requests` is empty when "write the rest" found nothing left to write. */
  | { readonly status: 'accepted'; readonly requests: readonly LongFormSectionWritingRequest[]; readonly replayed: boolean }
  /**
   * The account cannot pay for this. For "write the rest" the two counts say how many sections were asked
   * for and how many the allowance could have covered; nothing was started.
   */
  | {
      readonly status: 'blocked';
      readonly refusal: AiQuotaRefusal;
      readonly sectionsRequested: number | null;
      readonly sectionsCoverable: number | null;
    }
  /** Refused for a reason the creator can act on, in the server's own words: settled, changed, finished. */
  | { readonly status: 'refused'; readonly code: string; readonly message: string }
  | { readonly status: 'not_enabled' }
  /** The key was used for a different request. Ask again, which mints a fresh one. */
  | { readonly status: 'key_reused' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** What one write to a section — the creator's text, or its settled mark — did. */
export type LongFormSectionWriteOutcome =
  | { readonly status: 'saved' }
  /** The draft moved on since it was read. The creator's text is untouched; the remedy is to reload. */
  | { readonly status: 'stale' }
  | {
      readonly status: 'refused';
      readonly code: string;
      readonly message: string;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** What an acceptance did. */
export type LongFormAcceptanceOutcome =
  | { readonly status: 'accepted'; readonly document: LongFormAssembledDocument }
  /** The document has something wrong with it that was not confirmed. `issues` is everything wrong with it. */
  | { readonly status: 'confirmation_required'; readonly issues: readonly LongFormAcceptanceIssueDetail[] }
  | { readonly status: 'stale' }
  | { readonly status: 'refused'; readonly code: string; readonly message: string }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/**
 * The typed client for a long-form draft read and worked on whole (LF.3.3, LF.3.4): the assembled document,
 * asking for sections to be written, the creator's own edits and settled marks, and acceptance.
 *
 * **It decides nothing about a document.** What is wrong with one, and whether an acceptance may go ahead, is
 * the server's answer; this carries it.
 *
 * **No workspace id travels in any body.** The workspace is the route's, resolved server-side.
 */
@Injectable({ providedIn: 'root' })
export class LongFormDocumentService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  async read(workspaceSlug: string, draftId: string): Promise<LongFormDocumentOutcome> {
    const url = this.url(workspaceSlug, draftId, '/document');
    if (!url) return { status: 'unavailable' };

    try {
      const review = decodeLongFormDocumentReview(
        await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })),
      );

      return review ? { status: 'found', review } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 404) return { status: 'not_found' };
      if (code === 403) return { status: 'forbidden' };

      return { status: 'unavailable' };
    }
  }

  /** Asks for one section to be written, or written again. The key is required: writing spends the allowance. */
  async requestSection(
    workspaceSlug: string,
    draftId: string,
    sectionId: string,
    request: RequestLongFormSectionRequest,
    idempotencyKey: string,
  ): Promise<LongFormWritingOutcome> {
    const url = this.url(workspaceSlug, draftId, `/sections/${encodeURIComponent(sectionId)}/writing-requests`);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestLongFormSection(request), {
          withCredentials: true,
          observe: 'response',
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const queued = isRecord(response.body) ? decodeLongFormSectionWritingRequest(response.body['request']) : null;

      return queued
        ? { status: 'accepted', requests: [queued], replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      return mapWritingError(error);
    }
  }

  /** Asks for every section not yet written. All of them are queued, or none is. */
  async requestRest(
    workspaceSlug: string,
    draftId: string,
    revisionNumber: number,
    idempotencyKey: string,
  ): Promise<LongFormWritingOutcome> {
    const url = this.url(workspaceSlug, draftId, '/section-writing-batches');
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(
          url,
          { revisionNumber },
          { withCredentials: true, observe: 'response', headers: { 'Idempotency-Key': idempotencyKey } },
        ),
      );

      const raw = isRecord(response.body) ? response.body['requests'] : null;
      if (!Array.isArray(raw)) return { status: 'unavailable' };

      const requests: LongFormSectionWritingRequest[] = [];
      for (const item of raw) {
        const queued = decodeLongFormSectionWritingRequest(item);
        if (queued === null) return { status: 'unavailable' };
        requests.push(queued);
      }

      return { status: 'accepted', requests, replayed: response.headers.has('Idempotent-Replayed') };
    } catch (error) {
      return mapWritingError(error);
    }
  }

  /** Saves the creator's own text for one section. */
  editSection(
    workspaceSlug: string,
    draftId: string,
    sectionId: string,
    request: EditLongFormSectionRequest,
  ): Promise<LongFormSectionWriteOutcome> {
    const url = this.url(workspaceSlug, draftId, `/sections/${encodeURIComponent(sectionId)}`);

    return this.write(() =>
      url === null
        ? Promise.reject(new Error('No API base.'))
        : firstValueFrom(this.http.patch<unknown>(url, encodeEditLongFormSection(request), { withCredentials: true })),
    );
  }

  /** Marks one section settled, or clears the mark. */
  settleSection(
    workspaceSlug: string,
    draftId: string,
    sectionId: string,
    request: SettleLongFormSectionRequest,
  ): Promise<LongFormSectionWriteOutcome> {
    const url = this.url(workspaceSlug, draftId, `/sections/${encodeURIComponent(sectionId)}/settlement`);

    return this.write(() =>
      url === null
        ? Promise.reject(new Error('No API base.'))
        : firstValueFrom(this.http.post<unknown>(url, encodeSettleLongFormSection(request), { withCredentials: true })),
    );
  }

  /** Accepts the document. Nothing is published, scheduled or exported by accepting. */
  async accept(
    workspaceSlug: string,
    draftId: string,
    request: AcceptLongFormDocumentRequest,
  ): Promise<LongFormAcceptanceOutcome> {
    const url = this.url(workspaceSlug, draftId, '/acceptance');
    if (!url) return { status: 'unavailable' };

    try {
      const document = decodeLongFormAssembledDocument(
        await firstValueFrom(
          this.http.post<unknown>(url, encodeAcceptLongFormDocument(request), { withCredentials: true }),
        ),
      );

      return document ? { status: 'accepted', document } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      const problem = problemCodeOf(error);

      if (problem === LONG_FORM_ACCEPTANCE_CONFIRMATION_REQUIRED_CODE) {
        const issues = decodeLongFormAcceptanceIssues(extensionOf(error, 'issues'));

        // A refusal that cannot say what is wrong cannot be confirmed past, so it is not offered as one.
        return issues ? { status: 'confirmation_required', issues } : { status: 'unavailable' };
      }

      if (problem === LONG_FORM_DRAFT_STALE_CODE) return { status: 'stale' };
      if (code === 404) return { status: 'not_found' };
      if (code === 403) return { status: 'forbidden' };

      if (code === 400 || code === 409 || code === 422) {
        return {
          status: 'refused',
          code: problem ?? '',
          message: problemMessageOf(error) ?? 'This piece could not be accepted as it is.',
        };
      }

      return { status: 'unavailable' };
    }
  }

  private async write(send: () => Promise<unknown>): Promise<LongFormSectionWriteOutcome> {
    try {
      await send();

      // The reply is the draft's sections; the screen reads the document again, which is the one shape it
      // shows. Nothing of the reply is kept here, so there is nothing of it to get out of step.
      return { status: 'saved' };
    } catch (error) {
      const code = statusCodeOf(error);
      const problem = problemCodeOf(error);

      if (problem === LONG_FORM_DRAFT_STALE_CODE) return { status: 'stale' };
      if (code === 404) return { status: 'not_found' };
      if (code === 403) return { status: 'forbidden' };

      if (code === 400 || code === 409 || code === 422) {
        return {
          status: 'refused',
          code: problem ?? '',
          message: problemMessageOf(error) ?? 'That change could not be saved.',
          fieldErrors: decodeFieldErrors(error),
        };
      }

      return { status: 'unavailable' };
    }
  }

  /**
   * The link a browser follows to download an accepted piece, or null while the gateway address is not known.
   *
   * Only the link is built here. The file is made by the server and fetched by the browser with the session
   * cookie, so this never holds its bytes and never produces an address that is not the gateway's own export
   * route. There is nothing to choose: the export is always of the accepted revision.
   */
  exportUrl(workspaceSlug: string, draftId: string, format: LongFormExportFormat): string | null {
    return this.url(workspaceSlug, draftId, `/exports/${format}`);
  }

  private url(workspaceSlug: string, draftId: string, suffix: string): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/long-form-drafts/${encodeURIComponent(draftId)}${suffix}`,
    );
  }
}

function extensionOf(error: unknown, name: string): unknown {
  const body = error instanceof HttpErrorResponse ? (error.error as unknown) : null;

  return isRecord(body) ? body[name] : undefined;
}

function countOf(error: unknown, name: string): number | null {
  const value = extensionOf(error, name);

  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : null;
}

/** A failed writing request as the outcome it means. */
function mapWritingError(error: unknown): LongFormWritingOutcome {
  const code = statusCodeOf(error);
  const problem = problemCodeOf(error);

  // Before the 403 and 429 fallbacks: a suspension is a 403 like a role refusal and a spent allowance a 429
  // like the edge's rate limiter, so only the code tells them apart.
  const refusal = decodeAiQuotaRefusal(error);
  if (refusal) {
    return {
      status: 'blocked',
      refusal,
      sectionsRequested: countOf(error, 'sectionsRequested'),
      sectionsCoverable: countOf(error, 'sectionsCoverable'),
    };
  }

  if (problem === LONG_FORM_SECTION_NOT_ENABLED_CODE) return { status: 'not_enabled' };
  if (code === 422) return { status: 'key_reused' };
  if (code === 404) return { status: 'not_found' };
  if (code === 403) return { status: 'forbidden' };

  if (code === 409 || code === 400) {
    return {
      status: 'refused',
      code: problem ?? '',
      message: problemMessageOf(error) ?? 'That section cannot be written just now.',
    };
  }

  return { status: 'unavailable' };
}
