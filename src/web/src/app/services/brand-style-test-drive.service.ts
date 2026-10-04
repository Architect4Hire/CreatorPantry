import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import { AiQuotaRefusal, decodeAiQuotaRefusal } from '../models/ai-quota.models';
import {
  BrandStyleTestDrive,
  RequestBrandStyleTestDrive,
  decodeBrandStyleTestDrive,
  encodeRequestBrandStyleTestDrive,
} from '../models/brand-style-test-drive.models';

export type RequestTestDriveOutcome =
  /** `202`: the request is durable and queued. Nothing has been written yet — poll `watchStatus`. */
  | { readonly status: 'accepted'; readonly testDrive: BrandStyleTestDrive; readonly replayed: boolean }
  /** 11A.24 is a real task, switched off for this deployment. A configuration change, not a client fix. */
  | { readonly status: 'task_not_enabled' }
  /**
   * The version named has nothing written in it yet, so both columns would come back the same. Refused before
   * any model was called, so this cost nothing.
   */
  | { readonly status: 'guide_has_no_guidance' }
  | { readonly status: 'validation_failed'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  /** The `Idempotency-Key` was reused for a different request. Generate a fresh one; do not re-send this. */
  | { readonly status: 'idempotency_key_conflict' }
  /** Asking needs the Contributor role. */
  | { readonly status: 'forbidden' }
  /** Unknown guide or version, or one belonging to another workspace — deliberately indistinguishable. */
  | { readonly status: 'not_found' }
  /** The account cannot pay for this: its allowance is spent, or AI is switched off for it. */
  | AiQuotaRefusal
  | { readonly status: 'unavailable' };

export type WatchTestDriveOutcome =
  | { readonly status: 'found'; readonly testDrive: BrandStyleTestDrive }
  /** No such test drive on this workspace. */
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'unavailable' };

function statusCodeOf(error: unknown): number | null {
  return error instanceof HttpErrorResponse ? error.status : null;
}

function problemCodeOf(error: unknown): string | null {
  const body = error instanceof HttpErrorResponse ? (error.error as Record<string, unknown> | null) : null;
  const code = body?.['code'];
  return typeof code === 'string' ? code : null;
}

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

// AiBrandStyleTestDriveRequestErrors, mirrored.
const TASK_NOT_ENABLED_CODE = 'ai.brandStyleTestDrive.not_enabled';
const GUIDE_HAS_NO_GUIDANCE_CODE = 'ai.brandStyleTestDriveGuide.invalid_request';

/**
 * The typed client for trying a brand guide version out, and for reading the comparison back.
 *
 * Two methods, as every other AI request service has: a test drive runs in the background, and this route owns
 * reading it back because its reply is a comparison rather than the generic proposal shape.
 *
 * The caller owns the `Idempotency-Key` across retries of one logical request; this service never generates
 * one — a test drive is two generations, and a fresh key on a retry buys a second pair. No component may
 * inject `HttpClient` for these calls (.claude/rules/frontend.md).
 */
@Injectable({ providedIn: 'root' })
export class BrandStyleTestDriveService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  private url(workspaceSlug: string, suffix = ''): string | null {
    return this.apiBase.url(
      `/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/brand-style-test-drives${suffix}`,
    );
  }

  /**
   * Asks for a test drive of one exact guide version.
   *
   * `202`, never `201`: nothing has been written when this returns, and nothing about the guide, a recipe or
   * any content ever changes because of it.
   */
  async request(
    workspaceSlug: string,
    request: RequestBrandStyleTestDrive,
    idempotencyKey: string,
  ): Promise<RequestTestDriveOutcome> {
    const url = this.url(workspaceSlug);
    if (!url) return { status: 'unavailable' };

    try {
      const response = await firstValueFrom(
        this.http.post<unknown>(url, encodeRequestBrandStyleTestDrive(request), {
          withCredentials: true,
          observe: 'response',
          headers: { 'Idempotency-Key': idempotencyKey },
        }),
      );
      const testDrive = decodeBrandStyleTestDrive(response.body);
      return testDrive
        ? { status: 'accepted', testDrive, replayed: response.headers.has('Idempotent-Replayed') }
        : { status: 'unavailable' };
    } catch (error) {
      const code = statusCodeOf(error);

      if (code === 400) {
        switch (problemCodeOf(error)) {
          case TASK_NOT_ENABLED_CODE:
            return { status: 'task_not_enabled' };
          case GUIDE_HAS_NO_GUIDANCE_CODE:
            return { status: 'guide_has_no_guidance' };
          default:
            return { status: 'validation_failed', fieldErrors: decodeFieldErrors(error) };
        }
      }

      if (code === 422) return { status: 'idempotency_key_conflict' };

      // Before the 403 and 429 fallbacks below: a suspension arrives as a 403 like a role refusal, and a
      // spent allowance as a 429 like the edge's own rate limiter, so only the code tells them apart.
      const refusal = decodeAiQuotaRefusal(error);
      if (refusal) return refusal;

      if (code === 403) return { status: 'forbidden' };
      if (code === 404) return { status: 'not_found' };
      return { status: 'unavailable' };
    }
  }

  /**
   * Where a requested test drive has got to, and the comparison once there is one.
   *
   * Never errors: every outcome is a value, so one failed poll cannot kill a subscription.
   */
  watchStatus(workspaceSlug: string, requestId: string): Observable<WatchTestDriveOutcome> {
    const url = this.url(workspaceSlug, `/${encodeURIComponent(requestId)}`);
    if (!url) return of<WatchTestDriveOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): WatchTestDriveOutcome => {
        const testDrive = decodeBrandStyleTestDrive(raw);
        return testDrive ? { status: 'found', testDrive } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => {
        const code = statusCodeOf(error);
        if (code === 404) return of<WatchTestDriveOutcome>({ status: 'not_found' });
        if (code === 403) return of<WatchTestDriveOutcome>({ status: 'forbidden' });
        return of<WatchTestDriveOutcome>({ status: 'unavailable' });
      }),
    );
  }
}
