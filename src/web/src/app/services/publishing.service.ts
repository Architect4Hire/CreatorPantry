import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';

import { ApiBaseService } from '../core/api-base.service';
import {
  PublishingProfiles,
  PublishingRecord,
  PublishingRecordList,
  SchedulePublishingInput,
  decodePublishingProfiles,
  decodePublishingRecord,
  decodePublishingRecordList,
  encodeSchedule,
} from '../models/publishing.models';
import { decodeFieldErrors, problemCodeOf, statusCodeOf } from './ai-request';

export type PublishingProfilesOutcome =
  { readonly status: 'read'; readonly profiles: PublishingProfiles } | { readonly status: 'unavailable' };

export type PublishingRecordsOutcome =
  { readonly status: 'read'; readonly list: PublishingRecordList } | { readonly status: 'unavailable' };

/**
 * How a confirmation ended.
 *
 * **`confirmed` does not mean published.** It means the server committed the confirmation; the record's own
 * `status` says what became of it, and `Pending`, `Submitting` with an uncertain outcome and `Failed` all
 * arrive this way.
 */
export type SchedulePublishingOutcome =
  | { readonly status: 'confirmed'; readonly record: PublishingRecord; readonly replayed: boolean }
  | { readonly status: 'invalid'; readonly errors: Record<string, readonly string[]> }
  /** Something with this content is already going out to that profile at that time. */
  | { readonly status: 'duplicate'; readonly recordId: string | null }
  /** The provider will not post to that profile: it needs reconnecting, or is locked, or is gone. */
  | { readonly status: 'profile_not_ready'; readonly message: string }
  /** The profile list this was composed against is out of date. Read it again. */
  | { readonly status: 'platform_mismatch' }
  /** The workspace has no scheduling time zone. Nothing is guessed. */
  | { readonly status: 'time_zone_unset' }
  /** A picture cannot be published through the provider yet. */
  | { readonly status: 'media_not_publishable'; readonly message: string }
  | { readonly status: 'provider_not_configured' }
  | { readonly status: 'forbidden' }
  /** The key was reused for a different body. Generate a fresh one. */
  | { readonly status: 'key_reused' }
  /** Two confirmations shared a key and this one lost the race. Repeating it returns the winner's record. */
  | { readonly status: 'raced' }
  /**
   * The server said, in so many words, that nothing was written and nothing was sent. The same request, with
   * the same key, is safe to repeat.
   */
  | { readonly status: 'unavailable' }
  /**
   * No usable answer arrived — a dropped connection, a timeout, a gateway error, a body that could not be
   * read.
   *
   * **Whether the publication was confirmed and sent is not known.** This is deliberately not `unavailable`:
   * the request may have reached the server and the provider, so a screen must not say nothing happened.
   * Reading the card's publications again is how it is settled; repeating with the same key is also safe.
   */
  | { readonly status: 'outcome_unknown' };

/**
 * How a cancellation ended.
 *
 * **`answered` is not `cancelled`.** The server's record says whether the post is gone, still being removed,
 * or still going out, and a screen reads that rather than assuming the ask succeeded.
 */
export type CancelPublishingOutcome =
  | { readonly status: 'answered'; readonly record: PublishingRecord }
  /** The record changed since it was read. Nothing was cancelled. */
  | { readonly status: 'stale' }
  /** Whether it was sent is not known yet, so it cannot be cancelled. Reconciliation settles it. */
  | { readonly status: 'outcome_unknown' }
  | { readonly status: 'already_published' }
  /** It failed and will not go out, so there is nothing to cancel. */
  | { readonly status: 'not_cancellable' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'provider_not_configured' }
  /** No usable answer arrived. Whether the removal was asked for is not known; asking again is safe. */
  | { readonly status: 'outcome_unknown_answer' }
  | { readonly status: 'unavailable' };

/** `PublishingErrorCodes`, the ones this client branches on. */
const CODES = {
  invalid: 'publishing.schedule.invalid',
  forbidden: 'publishing.schedule.forbidden',
  raced: 'publishing.schedule.conflict',
  scheduleUnavailable: 'publishing.schedule.unavailable',
  cancelInvalid: 'publishing.cancel.invalid',
  cancelForbidden: 'publishing.cancel.forbidden',
  profileNotReady: 'publishing.profile.not_ready.unprocessable',
  platformMismatch: 'publishing.platform.mismatch.conflict',
  timeZoneUnset: 'publishing.time_zone.unset.unprocessable',
  mediaNotPublishable: 'publishing.media.not_publishable.unprocessable',
  providerNotConfigured: 'publishing.provider.not_configured.unprocessable',
  duplicate: 'publishing.duplicate.conflict',
  recordConflict: 'publishing.record.conflict',
  outcomeUnknown: 'publishing.outcome_unknown.conflict',
  notCancellable: 'publishing.not_cancellable.conflict',
  alreadyPublished: 'publishing.already_published.conflict',
  keyReused: 'idempotency.key_reused',
} as const;

/** The server's own sentence for a refusal, when it sent one worth showing. */
function titleOf(error: unknown, fallback: string): string {
  const problem = (error as { error?: { title?: unknown } } | null)?.error;

  return typeof problem?.title === 'string' && problem.title.trim() !== '' ? problem.title : fallback;
}

function extensionOf(error: unknown, name: string): string | null {
  const problem = (error as { error?: Record<string, unknown> } | null)?.error;
  const value = problem?.[name];

  return typeof value === 'string' ? value : null;
}

/**
 * The typed client for publishing (PUB-001/002/003).
 *
 * **Nothing here declares an outcome the server did not.** A confirmation and a cancellation each resolve to
 * the record the server returned, whatever state it is in; the only things this client decides are which
 * refusal a `ProblemDetails` code is.
 *
 * **The caller owns the `Idempotency-Key` across retries** of one confirmation, so a request whose answer was
 * lost is replayed rather than published twice.
 *
 * **Nothing here throws for an expected failure.** Each call resolves to an outcome a screen can render.
 */
@Injectable({ providedIn: 'root' })
export class PublishingService {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(ApiBaseService);

  /** The places this workspace can publish to, and how current the list is. Any member may read. */
  profiles(workspaceSlug: string): Observable<PublishingProfilesOutcome> {
    const url = this.url(workspaceSlug, 'profiles');
    if (!url) return of<PublishingProfilesOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true }).pipe(
      map((raw): PublishingProfilesOutcome => {
        const profiles = decodePublishingProfiles(raw);

        return profiles ? { status: 'read', profiles } : { status: 'unavailable' };
      }),
      catchError(() => of<PublishingProfilesOutcome>({ status: 'unavailable' })),
    );
  }

  /** One card's publications, newest scheduled first. Any member may read. */
  forCard(workspaceSlug: string, cardId: string): Observable<PublishingRecordsOutcome> {
    const url = this.url(workspaceSlug, 'records');
    if (!url) return of<PublishingRecordsOutcome>({ status: 'unavailable' });

    return this.http.get<unknown>(url, { withCredentials: true, params: { contentCardId: cardId } }).pipe(
      map((raw): PublishingRecordsOutcome => {
        const list = decodePublishingRecordList(raw);

        return list ? { status: 'read', list } : { status: 'unavailable' };
      }),
      catchError(() => of<PublishingRecordsOutcome>({ status: 'unavailable' })),
    );
  }

  /** Confirms a publication. Takes the Editor role. */
  schedule(
    workspaceSlug: string,
    input: SchedulePublishingInput,
    idempotencyKey: string,
  ): Observable<SchedulePublishingOutcome> {
    const url = this.url(workspaceSlug, 'records');
    if (!url) return of<SchedulePublishingOutcome>({ status: 'unavailable' });

    return this.http
      .post<unknown>(url, encodeSchedule(input), {
        withCredentials: true,
        observe: 'response',
        headers: { 'Idempotency-Key': idempotencyKey },
      })
      .pipe(
        map((response): SchedulePublishingOutcome => {
          const record = decodePublishingRecord(response.body);

          // A 201 whose body will not decode means the publication was confirmed and this client cannot say
          // what became of it — which is an unknown outcome, not a failure.
          return record
            ? { status: 'confirmed', record, replayed: response.headers.has('Idempotent-Replayed') }
            : { status: 'outcome_unknown' };
        }),
        catchError((error: unknown) => of(this.scheduleRefusal(error))),
      );
  }

  /**
   * Stops a publication going out, quoting the token of the record as it was read.
   *
   * Repeating it is safe: the record's id is what makes a cancellation converge, so there is no key here.
   */
  cancel(workspaceSlug: string, recordId: string, concurrencyToken: string): Observable<CancelPublishingOutcome> {
    const url = this.url(workspaceSlug, `records/${encodeURIComponent(recordId)}/cancellation`);
    if (!url) return of<CancelPublishingOutcome>({ status: 'unavailable' });

    return this.http.post<unknown>(url, { expectedConcurrencyToken: concurrencyToken }, { withCredentials: true }).pipe(
      map((raw): CancelPublishingOutcome => {
        const record = decodePublishingRecord(raw);

        return record ? { status: 'answered', record } : { status: 'unavailable' };
      }),
      catchError((error: unknown) => of(this.cancelRefusal(error))),
    );
  }

  private scheduleRefusal(error: unknown): SchedulePublishingOutcome {
    switch (problemCodeOf(error)) {
      case CODES.keyReused:
        return { status: 'key_reused' };
      case CODES.duplicate:
        return { status: 'duplicate', recordId: extensionOf(error, 'publishingRecordId') };
      case CODES.profileNotReady:
        return {
          status: 'profile_not_ready',
          message: titleOf(error, 'The provider will not post to that profile at the moment.'),
        };
      case CODES.platformMismatch:
        return { status: 'platform_mismatch' };
      case CODES.timeZoneUnset:
        return { status: 'time_zone_unset' };
      case CODES.mediaNotPublishable:
        return {
          status: 'media_not_publishable',
          message: titleOf(error, 'A picture cannot be published through the provider yet.'),
        };
      case CODES.providerNotConfigured:
        return { status: 'provider_not_configured' };
      case CODES.forbidden:
      case CODES.cancelForbidden:
        return { status: 'forbidden' };
      case CODES.raced:
        return { status: 'raced' };
      case CODES.scheduleUnavailable:
        return { status: 'unavailable' };
      default:
        break;
    }

    switch (statusCodeOf(error)) {
      case 400:
        return { status: 'invalid', errors: decodeFieldErrors(error) };
      case 403:
        return { status: 'forbidden' };
      default:
        // Anything else — a timeout, a dropped connection, a 500, a 504, a body that would not decode — may
        // have reached the provider. Saying "nothing was sent" here is the one thing this client must not do.
        return { status: 'outcome_unknown' };
    }
  }

  private cancelRefusal(error: unknown): CancelPublishingOutcome {
    switch (problemCodeOf(error)) {
      case CODES.recordConflict:
        return { status: 'stale' };
      case CODES.outcomeUnknown:
        return { status: 'outcome_unknown' };
      case CODES.alreadyPublished:
        return { status: 'already_published' };
      case CODES.notCancellable:
        return { status: 'not_cancellable' };
      case CODES.cancelForbidden:
      case CODES.forbidden:
        return { status: 'forbidden' };
      case CODES.providerNotConfigured:
        return { status: 'provider_not_configured' };
      default:
        break;
    }

    switch (statusCodeOf(error)) {
      case 400:
        return { status: 'unavailable' };
      case 403:
        return { status: 'forbidden' };
      case 404:
        return { status: 'not_found' };
      default:
        // The removal may have been asked for. Asking again is safe, so the screen says so rather than
        // claiming nothing happened.
        return { status: 'outcome_unknown_answer' };
    }
  }

  private url(workspaceSlug: string, path: string): string | null {
    return this.apiBase.url(`/api/v1/workspaces/${encodeURIComponent(workspaceSlug)}/publishing/${path}`);
  }
}
