import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiProposalStatus } from '../models/ai-proposal.models';
import {
  ApproveLongFormPlanRequest,
  LONG_FORM_CONTEXT_UNPROCESSABLE_CODE,
  LONG_FORM_DRAFT_STALE_CODE,
  LONG_FORM_OUTLINE_NOT_ENABLED_CODE,
  LONG_FORM_PLAN_LOCKED_CODE,
  LONG_FORM_PLAN_NOT_STARTED_CODE,
  LongFormDraft,
  LongFormDraftListQuery,
  LongFormDraftPage,
  PatchLongFormPlanRequest,
  RequestLongFormOutlineRequest,
  StartLongFormDraftRequest,
  decodeLongFormDraft,
  decodeLongFormDraftPage,
  decodeLongFormOutlineRequestStatus,
  decodeStartedLongFormDraft,
  encodeApproveLongFormPlan,
  encodePatchLongFormPlan,
  encodeRequestLongFormOutline,
  encodeStartLongFormDraft,
} from '../models/long-form-draft.models';
import {
  AiRequestOutcome,
  AiWatchOperationOutcome,
  decodeFieldErrors,
  mapAiRequestError,
  mapAiWatchError,
  problemCodeOf,
  problemMessageOf,
  statusCodeOf,
} from './ai-request';

/** What starting a draft did: the draft and its outline request, or the one reason nothing was started. */
export type LongFormStartOutcome =
  | {
      readonly status: 'accepted';
      readonly draft: LongFormDraft;
      readonly operation: AiProposalStatus;
      readonly replayed: boolean;
    }
  | Exclude<AiRequestOutcome, { readonly status: 'accepted' }>;

/** One draft, or the one reason it could not be read. */
export type LongFormDraftOutcome =
  | { readonly status: 'found'; readonly draft: LongFormDraft }
  /** No such draft here — which is also the answer for another workspace's. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

export type LongFormDraftPageOutcome =
  | { readonly status: 'found'; readonly page: LongFormDraftPage }
  /** The cursor was not issued for this list. Start again without one. */
  | { readonly status: 'cursor_invalid' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/** What one write to a draft's plan did. Every failure a creator can act on has its own member. */
export type LongFormPlanWriteOutcome =
  | { readonly status: 'saved'; readonly draft: LongFormDraft }
  /**
   * The draft moved on since the creator opened it — another tab, another device, a new outline. Their edits
   * are untouched; the remedy is to reload and make them again.
   */
  | { readonly status: 'stale' }
  /** The draft's sections are already being written, so its plan is closed. */
  | { readonly status: 'locked'; readonly message: string }
  /** The draft has no plan yet. */
  | { readonly status: 'not_started'; readonly message: string }
  /**
   * The plan was refused: it could not be kept as sent (`400`), or it is not ready to approve (`422`).
   * `fieldErrors` are keyed as the server keys them — `sections[n]`, `sections[n].id`, and so on.
   */
  | {
      readonly status: 'refused';
      readonly message: string;
      readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
    }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

/**
 * The typed client for LF.2.2: starting a long-form draft, following and repeating its outline request, and
 * reading, editing and approving its plan.
 *
 * **It decides nothing about a plan.** Whether a plan is ready to approve is the server's answer, carried on
 * the draft as `planCheck` and in an approval's refusal; this client carries both and judges neither.
 *
 * **No workspace id travels in any body.** The workspace is the route's, resolved server-side.
 */
@Injectable({ providedIn: 'root' })
export class LongFormDraftService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** Starts a draft and asks for its outline. The key is required: planning spends the allowance. */
  async start(
    workspaceSlug: string,
    request: StartLongFormDraftRequest,
    idempotencyKey: string,
  ): Promise<LongFormStartOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeStartLongFormDraft(request), {
          withCredentials: true,
          observe: 'response',
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const started = decodeStartedLongFormDraft(response.body);

      return started
        ? {
            status: 'accepted',
            draft: started.draft,
            operation: started.request,
            replayed: response.headers.has('Idempotent-Replayed'),
          }
        : { status: 'unavailable' };
    } catch (error) {
      return mapRequestError(error);
    }
  }

  /**
   * Asks for another outline for a draft that already exists.
   *
   * Shaped as an ordinary AI request, so {@link AiOperationTracker} can submit and follow it unchanged. The
   * draft itself is read again once the request has finished.
   */
  async requestOutline(
    workspaceSlug: string,
    draftId: string,
    request: RequestLongFormOutlineRequest,
    idempotencyKey: string,
  ): Promise<AiRequestOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(draftId)}/outline-requests`);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestLongFormOutline(request), {
          withCredentials: true,
          observe: 'response',
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const started = decodeStartedLongFormDraft(response.body);

      return started
        ? {
            status: 'accepted',
            operation: started.request,
            replayed: response.headers.has('Idempotent-Replayed'),
          }
        : { status: 'unavailable' };
    } catch (error) {
      // The draft has gone, or was never this workspace's: said as a refusal the creator can read rather
      // than as an outage that might fix itself.
      if (statusCodeOf(error) === 404) {
        return {
          status: 'refused',
          code: problemCodeOf(error) ?? 'content.long_form.draft.not_found',
          message: 'This draft is no longer here.',
        };
      }

      return mapRequestError(error);
    }
  }

  /** Where one outline request has got to. The draft is read separately, from the draft's own route. */
  watchOutline(workspaceSlug: string, draftId: string, requestId: string): Observable<AiWatchOperationOutcome> {
    const url = this.url(
      workspaceSlug,
      `/${encodeURIComponent(draftId)}/outline-requests/${encodeURIComponent(requestId)}`,
    );
    if (!url) return of<AiWatchOperationOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): AiWatchOperationOutcome => {
        const status = decodeLongFormOutlineRequestStatus(raw);
        return status ? { status: 'found', operation: status.request } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => of(mapAiWatchError(error))),
    );
  }

  async read(workspaceSlug: string, draftId: string): Promise<LongFormDraftOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(draftId)}`);
    if (!url) return { status: 'unavailable' };

    try {
      const draft = decodeLongFormDraft(await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true })));

      return draft ? { status: 'found', draft } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 404) return { status: 'not_found' };
      if (code === 403) return { status: 'forbidden' };

      return { status: 'unavailable' };
    }
  }

  async list(workspaceSlug: string, query: LongFormDraftListQuery = {}): Promise<LongFormDraftPageOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    let params = new HttpParams();
    if (query.limit !== undefined) params = params.set('limit', query.limit);
    if (query.cursor) params = params.set('cursor', query.cursor);
    if (query.kind) params = params.set('kind', query.kind);
    if (query.creativeContextId) params = params.set('creativeContextId', query.creativeContextId);

    try {
      const page = decodeLongFormDraftPage(
        await firstValueFrom(this.http.get<unknown>(url, { withCredentials: true, params })),
      );

      return page ? { status: 'found', page } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      if (code === 400) return { status: 'cursor_invalid' };
      if (code === 403) return { status: 'forbidden' };

      return { status: 'unavailable' };
    }
  }

  /** Saves the creator's whole plan as a new revision. */
  patchPlan(
    workspaceSlug: string,
    draftId: string,
    request: PatchLongFormPlanRequest,
  ): Promise<LongFormPlanWriteOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(draftId)}/plan`);

    return this.write(() =>
      url === null
        ? Promise.reject(new Error('No API base.'))
        : firstValueFrom(this.http.patch<unknown>(url, encodePatchLongFormPlan(request), { withCredentials: true })),
    );
  }

  /** Approves one revision of the plan. Nothing is written, generated or spent by approving. */
  approvePlan(
    workspaceSlug: string,
    draftId: string,
    request: ApproveLongFormPlanRequest,
  ): Promise<LongFormPlanWriteOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(draftId)}/plan/approval`);

    return this.write(() =>
      url === null
        ? Promise.reject(new Error('No API base.'))
        : firstValueFrom(this.http.post<unknown>(url, encodeApproveLongFormPlan(request), { withCredentials: true })),
    );
  }

  /**
   * One write, and the outcome it means.
   *
   * The three conflicts are told apart by their stable codes rather than by the `409` they share, because the
   * remedy differs for each: reload, wait for a plan, or stop trying to change one that is closed.
   */
  private async write(send: () => Promise<unknown>): Promise<LongFormPlanWriteOutcome> {
    try {
      const draft = decodeLongFormDraft(await send());

      return draft ? { status: 'saved', draft } : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);
      const problem = problemCodeOf(error);

      if (problem === LONG_FORM_DRAFT_STALE_CODE) return { status: 'stale' };

      if (problem === LONG_FORM_PLAN_LOCKED_CODE) {
        return {
          status: 'locked',
          message: problemMessageOf(error) ?? 'This draft’s sections are already being written.',
        };
      }

      if (problem === LONG_FORM_PLAN_NOT_STARTED_CODE) {
        return { status: 'not_started', message: problemMessageOf(error) ?? 'This draft has no plan yet.' };
      }

      if (code === 404) return { status: 'not_found' };
      if (code === 403) return { status: 'forbidden' };

      if (code === 400 || code === 422) {
        return {
          status: 'refused',
          message: problemMessageOf(error) ?? 'That plan could not be saved as sent.',
          fieldErrors: decodeFieldErrors(error),
        };
      }

      return { status: 'unavailable' };
    }
  }

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/long-form-drafts${suffix}`);
  }
}

/** A failed outline request as the outcome it means, with long-form's own refusals named. */
function mapRequestError(error: unknown): Exclude<AiRequestOutcome, { readonly status: 'accepted' }> {
  const outcome = mapAiRequestError(error, LONG_FORM_OUTLINE_NOT_ENABLED_CODE, [
    LONG_FORM_CONTEXT_UNPROCESSABLE_CODE,
    LONG_FORM_PLAN_LOCKED_CODE,
  ]);

  // The mapper only ever builds 'accepted' from a success, which this is not.
  return outcome.status === 'accepted' ? { status: 'unavailable' } : outcome;
}
