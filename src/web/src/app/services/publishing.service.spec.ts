import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { PublishingService } from './publishing.service';

const GATEWAY = 'https://gateway.example';
const RECORDS = `${GATEWAY}/api/v1/workspaces/cozy-fall/publishing/records`;
const PROFILES = `${GATEWAY}/api/v1/workspaces/cozy-fall/publishing/profiles`;
const CARD = 'card-1';

function record(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'rec-1',
    contentCardId: CARD,
    publishingProfileId: 'prof-1',
    platform: 'Instagram',
    status: 'Scheduled',
    text: 'Soda bread, out of the oven at last.',
    scheduledAt: '2026-10-16T13:30:00Z',
    scheduleTimeZoneId: 'America/Chicago',
    scheduledLocal: '2026-10-16T08:30:00',
    outcomeUncertain: false,
    failureCategory: null,
    failureSummary: null,
    submittedAt: '2026-10-13T12:00:00Z',
    completedAt: null,
    submitAttempts: 1,
    confirmedAt: '2026-10-13T11:59:00Z',
    concurrencyToken: 'AAAAAAAAB9E=',
    ...over,
  };
}

function problem(code: string, extras: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1', ...extras };
}

describe('PublishingService', () => {
  let service: PublishingService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(PublishingService);
  });

  afterEach(() => http.verify());

  // ---- Reading ----

  it('asks for one card’s publications and decodes them', async () => {
    const reading = firstValueFrom(service.forCard('cozy-fall', CARD));

    const request = http.expectOne((candidate) => candidate.url === RECORDS);
    expect(request.request.method).toBe('GET');
    expect(request.request.params.get('contentCardId')).toBe(CARD);
    expect(request.request.withCredentials).toBeTrue();
    request.flush({ items: [record()], complete: true });

    const outcome = await reading;
    expect(outcome.status).toBe('read');
    if (outcome.status !== 'read') return;
    expect(outcome.list.items[0].status).toBe('Scheduled');
    expect(outcome.list.complete).toBeTrue();
  });

  it('calls a status it does not know unrecognised rather than dropping the publication', async () => {
    const reading = firstValueFrom(service.forCard('cozy-fall', CARD));
    http
      .expectOne((candidate) => candidate.url === RECORDS)
      .flush({
        items: [record({ status: 'SomethingNew' })],
        complete: true,
      });

    const outcome = await reading;
    expect(outcome.status).toBe('read');
    if (outcome.status !== 'read') return;

    // A publication in a state this build has not heard of still exists, and still matters.
    expect(outcome.list.items.length).toBe(1);
    expect(outcome.list.items[0].status).toBe('Unrecognised');
  });

  it('treats one unreadable row as an unreadable list, never as a shorter one', async () => {
    const reading = firstValueFrom(service.forCard('cozy-fall', CARD));
    http
      .expectOne((candidate) => candidate.url === RECORDS)
      .flush({
        items: [record(), { id: 'rec-2' }],
        complete: true,
      });

    expect((await reading).status).toBe('unavailable');
  });

  it('reads the profiles with their connection status', async () => {
    const reading = firstValueFrom(service.profiles('cozy-fall'));

    http.expectOne(PROFILES).flush({
      connection: {
        providerKey: 'buffer',
        status: 'RateLimited',
        lastCheckedAt: null,
        lastSucceededAt: null,
        retryAfterSeconds: 90,
      },
      profiles: [
        {
          id: 'prof-1',
          providerKey: 'buffer',
          platform: 'Instagram',
          displayName: 'Cozy Fall Kitchen',
          timeZoneId: 'America/Chicago',
          status: 'NeedsReconnecting',
          lastSeenAt: '2026-10-13T10:00:00Z',
        },
      ],
    });

    const outcome = await reading;
    expect(outcome.status).toBe('read');
    if (outcome.status !== 'read') return;
    expect(outcome.profiles.connection.status).toBe('RateLimited');
    expect(outcome.profiles.connection.retryAfterSeconds).toBe(90);
    expect(outcome.profiles.profiles[0].status).toBe('NeedsReconnecting');
  });

  // ---- Confirming ----

  it('sends the confirmation with the creator’s key and reports the record it got back', async () => {
    const scheduling = firstValueFrom(
      service.schedule(
        'cozy-fall',
        {
          contentCardId: CARD,
          publishingProfileId: 'prof-1',
          expectedPlatform: 'Instagram',
          text: 'Soda bread.',
          scheduledAt: '2026-10-16T13:30:00Z',
        },
        'key-1',
      ),
    );

    const request = http.expectOne(RECORDS);
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');

    // The explicit confirmation the route requires is always sent, never implied.
    expect(request.request.body.confirmed).toBeTrue();
    expect(request.request.body.expectedPlatform).toBe('Instagram');
    request.flush(record());

    const outcome = await scheduling;
    expect(outcome.status).toBe('confirmed');
    if (outcome.status !== 'confirmed') return;
    expect(outcome.replayed).toBeFalse();
  });

  it('reports a replay as a replay rather than a new publication', async () => {
    const scheduling = firstValueFrom(
      service.schedule(
        'cozy-fall',
        {
          contentCardId: CARD,
          publishingProfileId: 'prof-1',
          expectedPlatform: null,
          text: 'x',
          scheduledAt: '2026-10-16T13:30:00Z',
        },
        'key-1',
      ),
    );

    http.expectOne(RECORDS).flush(record(), { headers: { 'Idempotent-Replayed': 'true' } });

    const outcome = await scheduling;
    if (outcome.status !== 'confirmed') throw new Error('expected a confirmation');
    expect(outcome.replayed).toBeTrue();
  });

  it('reports a confirmation whose outcome nobody knows as confirmed, with the record saying so', async () => {
    const scheduling = firstValueFrom(
      service.schedule(
        'cozy-fall',
        {
          contentCardId: CARD,
          publishingProfileId: 'prof-1',
          expectedPlatform: null,
          text: 'x',
          scheduledAt: '2026-10-16T13:30:00Z',
        },
        'key-1',
      ),
    );

    http
      .expectOne(RECORDS)
      .flush(record({ status: 'Submitting', outcomeUncertain: true, failureCategory: 'provider_timeout' }));

    const outcome = await scheduling;
    if (outcome.status !== 'confirmed') throw new Error('expected a confirmation');

    // `confirmed` is about the request, never about the post: the record is what says what happened.
    expect(outcome.record.outcomeUncertain).toBeTrue();
    expect(outcome.record.status).toBe('Submitting');
  });

  const refusals = [
    ['publishing.duplicate.conflict', 'duplicate'],
    ['publishing.profile.not_ready.unprocessable', 'profile_not_ready'],
    ['publishing.platform.mismatch.conflict', 'platform_mismatch'],
    ['publishing.time_zone.unset.unprocessable', 'time_zone_unset'],
    ['publishing.media.not_publishable.unprocessable', 'media_not_publishable'],
    ['publishing.provider.not_configured.unprocessable', 'provider_not_configured'],
    ['publishing.schedule.forbidden', 'forbidden'],
    ['idempotency.key_reused', 'key_reused'],
  ] as const;

  for (const [code, status] of refusals) {
    it(`maps ${code} to ${status}`, async () => {
      const scheduling = firstValueFrom(
        service.schedule(
          'cozy-fall',
          {
            contentCardId: CARD,
            publishingProfileId: 'prof-1',
            expectedPlatform: null,
            text: 'x',
            scheduledAt: '2026-10-16T13:30:00Z',
          },
          'key-1',
        ),
      );

      // Each code at the status its route actually answers with, so the test cannot pass on a mapping that
      // only works for one of them.
      const httpStatus =
        code.endsWith('.unprocessable') || code === 'idempotency.key_reused'
          ? 422
          : code === 'publishing.schedule.forbidden'
            ? 403
            : 409;

      http
        .expectOne(RECORDS)
        .flush(problem(code, { publishingRecordId: 'rec-9' }), { status: httpStatus, statusText: 'nope' });

      expect((await scheduling).status).toBe(status);
    });
  }

  it('carries the existing publication’s id on a duplicate, so a screen can point at it', async () => {
    const scheduling = firstValueFrom(
      service.schedule(
        'cozy-fall',
        {
          contentCardId: CARD,
          publishingProfileId: 'prof-1',
          expectedPlatform: null,
          text: 'x',
          scheduledAt: '2026-10-16T13:30:00Z',
        },
        'key-1',
      ),
    );

    http.expectOne(RECORDS).flush(problem('publishing.duplicate.conflict', { publishingRecordId: 'rec-9' }), {
      status: 409,
      statusText: 'Conflict',
    });

    const outcome = await scheduling;
    if (outcome.status !== 'duplicate') throw new Error('expected a duplicate');
    expect(outcome.recordId).toBe('rec-9');
  });

  const unknowns: readonly [string, number][] = [
    ['a server error', 500],
    ['a gateway timeout', 504],
    ['a dropped connection', 0],
  ];

  for (const [what, status] of unknowns) {
    it(`treats ${what} during a confirmation as an unknown outcome, never as "nothing was sent"`, async () => {
      const scheduling = firstValueFrom(
        service.schedule(
          'cozy-fall',
          {
            contentCardId: CARD,
            publishingProfileId: 'prof-1',
            expectedPlatform: null,
            text: 'x',
            scheduledAt: '2026-10-16T13:30:00Z',
          },
          'key-1',
        ),
      );

      http.expectOne(RECORDS).flush(null, { status, statusText: 'nope' });

      // The request may have reached the server and the provider. Reporting it as "nothing happened" is the
      // one claim that can end in the same post going out twice.
      expect((await scheduling).status).toBe('outcome_unknown');
    });
  }

  it('treats a confirmation it cannot decode as an unknown outcome', async () => {
    const scheduling = firstValueFrom(
      service.schedule(
        'cozy-fall',
        {
          contentCardId: CARD,
          publishingProfileId: 'prof-1',
          expectedPlatform: null,
          text: 'x',
          scheduledAt: '2026-10-16T13:30:00Z',
        },
        'key-1',
      ),
    );

    // A 201 means it was committed; a body this client cannot read means it cannot say what became of it.
    http.expectOne(RECORDS).flush({ id: 'rec-1' }, { status: 201, statusText: 'Created' });

    expect((await scheduling).status).toBe('outcome_unknown');
  });

  it('believes the server when it says nothing was written', async () => {
    const scheduling = firstValueFrom(
      service.schedule(
        'cozy-fall',
        {
          contentCardId: CARD,
          publishingProfileId: 'prof-1',
          expectedPlatform: null,
          text: 'x',
          scheduledAt: '2026-10-16T13:30:00Z',
        },
        'key-1',
      ),
    );

    http
      .expectOne(RECORDS)
      .flush(problem('publishing.schedule.unavailable'), { status: 503, statusText: 'Unavailable' });

    // The one case where "nothing was sent, try again" is the server's own statement rather than a guess.
    expect((await scheduling).status).toBe('unavailable');
  });

  it('reports losing a race for a shared key as a race', async () => {
    const scheduling = firstValueFrom(
      service.schedule(
        'cozy-fall',
        {
          contentCardId: CARD,
          publishingProfileId: 'prof-1',
          expectedPlatform: null,
          text: 'x',
          scheduledAt: '2026-10-16T13:30:00Z',
        },
        'key-1',
      ),
    );

    http.expectOne(RECORDS).flush(problem('publishing.schedule.conflict'), { status: 409, statusText: 'Conflict' });

    expect((await scheduling).status).toBe('raced');
  });

  // ---- Cancelling ----

  it('quotes the record’s token and sends no idempotency key', async () => {
    const cancelling = firstValueFrom(service.cancel('cozy-fall', 'rec-1', 'AAAAAAAAB9E='));

    const request = http.expectOne(`${RECORDS}/rec-1/cancellation`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body.expectedConcurrencyToken).toBe('AAAAAAAAB9E=');

    // Repeating a cancellation is safe, so there is nothing for a key to protect.
    expect(request.request.headers.has('Idempotency-Key')).toBeFalse();
    request.flush(record({ status: 'Cancelled', completedAt: '2026-10-13T12:05:00Z' }));

    const outcome = await cancelling;
    if (outcome.status !== 'answered') throw new Error('expected an answer');
    expect(outcome.record.status).toBe('Cancelled');
  });

  it('reports a cancellation the provider has not confirmed as the record says, not as cancelled', async () => {
    const cancelling = firstValueFrom(service.cancel('cozy-fall', 'rec-1', 'AAAAAAAAB9E='));

    http.expectOne(`${RECORDS}/rec-1/cancellation`).flush(record({ status: 'Cancelling', outcomeUncertain: true }));

    const outcome = await cancelling;
    if (outcome.status !== 'answered') throw new Error('expected an answer');
    expect(outcome.record.status).toBe('Cancelling');
    expect(outcome.record.outcomeUncertain).toBeTrue();
  });

  const cancelRefusals = [
    ['publishing.record.conflict', 'stale'],
    ['publishing.outcome_unknown.conflict', 'outcome_unknown'],
    ['publishing.already_published.conflict', 'already_published'],
    ['publishing.not_cancellable.conflict', 'not_cancellable'],
  ] as const;

  for (const [code, status] of cancelRefusals) {
    it(`maps ${code} to ${status}`, async () => {
      const cancelling = firstValueFrom(service.cancel('cozy-fall', 'rec-1', 'AAAAAAAAB9E='));

      http.expectOne(`${RECORDS}/rec-1/cancellation`).flush(problem(code), { status: 409, statusText: 'Conflict' });

      expect((await cancelling).status).toBe(status);
    });
  }

  it('treats a cancellation with no usable answer as unknown, and says asking again is safe', async () => {
    const cancelling = firstValueFrom(service.cancel('cozy-fall', 'rec-1', 'AAAAAAAAB9E='));

    http.expectOne(`${RECORDS}/rec-1/cancellation`).flush(null, { status: 504, statusText: 'Timeout' });

    expect((await cancelling).status).toBe('outcome_unknown_answer');
  });

  it('maps a workspace with no provider on a cancellation', async () => {
    const cancelling = firstValueFrom(service.cancel('cozy-fall', 'rec-1', 'AAAAAAAAB9E='));

    http
      .expectOne(`${RECORDS}/rec-1/cancellation`)
      .flush(problem('publishing.provider.not_configured.unprocessable'), { status: 422, statusText: 'Unprocessable' });

    expect((await cancelling).status).toBe('provider_not_configured');
  });

  it('reports a publication that is no longer there as not found', async () => {
    const cancelling = firstValueFrom(service.cancel('cozy-fall', 'rec-1', 'AAAAAAAAB9E='));

    http
      .expectOne(`${RECORDS}/rec-1/cancellation`)
      .flush(problem('publishing.record.not_found'), { status: 404, statusText: 'Not Found' });

    expect((await cancelling).status).toBe('not_found');
  });
});
