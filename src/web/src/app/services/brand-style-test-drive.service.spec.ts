import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { BrandStyleTestDriveService } from './brand-style-test-drive.service';

const REQUEST_ID = '44444444-4444-4444-8444-444444444444';
const GUIDE_ID = '11111111-1111-4111-8111-111111111111';
const BASE = 'https://gateway.example/api/v1/workspaces/cozy-fall/brand-style-test-drives';
const STATUS_URL = `${BASE}/${REQUEST_ID}`;

function payload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    requestId: REQUEST_ID,
    status: 'Requested',
    failureCategory: null,
    requestedAt: '2026-10-04T12:00:00Z',
    statusChangedAt: '2026-10-04T12:00:00Z',
    comparison: null,
    ...overrides,
  };
}

function problem(code: string, title = 'Refused', errors?: Record<string, string[]>): Record<string, unknown> {
  return { code, title, traceId: 'trace-1', ...(errors ? { errors } : {}) };
}

describe('BrandStyleTestDriveService', () => {
  let service: BrandStyleTestDriveService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(BrandStyleTestDriveService);
  });

  afterEach(() => http.verify());

  describe('request', () => {
    it('posts the guide and version with the callers idempotency key', async () => {
      const pending = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-1');

      const request = http.expectOne(BASE);
      expect(request.request.method).toBe('POST');
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.body).toEqual({ guideId: GUIDE_ID, versionNumber: 2 });

      request.flush(payload(), { status: 202, statusText: 'Accepted' });

      const outcome = await pending;
      expect(outcome.status).toBe('accepted');
      if (outcome.status === 'accepted') {
        expect(outcome.testDrive.requestId).toBe(REQUEST_ID);
        expect(outcome.replayed).toBeFalse();
      }
    });

    /** Two generations: a caller that thought a replay was a fresh answer would double-count the spend. */
    it('reports a replay so a caller does not think it bought a second pair', async () => {
      const pending = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-1');

      http
        .expectOne(BASE)
        .flush(payload(), { status: 202, statusText: 'Accepted', headers: { 'Idempotent-Replayed': 'true' } });

      const outcome = await pending;
      expect(outcome.status === 'accepted' && outcome.replayed).toBeTrue();
    });

    it('maps the not-enabled code to its own outcome', async () => {
      const pending = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-1');

      http
        .expectOne(BASE)
        .flush(problem('ai.brandStyleTestDrive.not_enabled'), { status: 400, statusText: 'Bad Request' });

      expect((await pending).status).toBe('task_not_enabled');
    });

    /**
     * Its own outcome because its remedy is its own: write a part of the guide. Reading it as a generic
     * validation failure would put the message against a field the creator never filled in.
     */
    it('maps an empty guide version to its own outcome', async () => {
      const pending = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-1');

      http
        .expectOne(BASE)
        .flush(problem('ai.brandStyleTestDriveGuide.invalid_request'), { status: 400, statusText: 'Bad Request' });

      expect((await pending).status).toBe('guide_has_no_guidance');
    });

    it('carries field errors through for anything else that is a bad request', async () => {
      const pending = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 0 }, 'key-1');

      http.expectOne(BASE).flush(
        problem('ai.brandStyleTestDrive.invalid_request', 'Not valid', {
          VersionNumber: ['Name the version of that guide to try out.'],
        }),
        { status: 400, statusText: 'Bad Request' },
      );

      const outcome = await pending;
      expect(outcome.status).toBe('validation_failed');
      expect(outcome.status === 'validation_failed' && outcome.fieldErrors['VersionNumber'].length).toBe(1);
    });

    it('maps a spent allowance to its own outcome, with the figures a creator needs', async () => {
      const pending = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-1');

      http.expectOne(BASE).flush(
        {
          ...problem('ai.quota.exhausted', 'Your AI allowance for this period is spent.'),
          unit: 'Credits',
          allowance: 1000,
          remaining: 5,
          required: 40,
          resetsAt: '2026-11-01T00:00:00+00:00',
        },
        { status: 429, statusText: 'Too Many Requests' },
      );

      const outcome = await pending;
      expect(outcome.status).toBe('quota_exhausted');
      expect(outcome.status === 'quota_exhausted' && outcome.required).toBe(40);
    });

    /**
     * A suspension arrives as a 403, the same status a role refusal uses. Decoded before the generic 403
     * fallback, so it does not read as "you need the Contributor role".
     */
    it('tells a suspended account apart from a role refusal', async () => {
      const suspended = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-1');

      http.expectOne(BASE).flush(
        { ...problem('ai.quota.suspended', 'Switched off.'), unit: 'Credits' },
        { status: 403, statusText: 'Forbidden' },
      );

      expect((await suspended).status).toBe('account_suspended');

      const forbidden = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-2');

      http
        .expectOne(BASE)
        .flush(problem('workspace.forbidden', 'Not your role.'), { status: 403, statusText: 'Forbidden' });

      expect((await forbidden).status).toBe('forbidden');
    });

    it('maps a reused key to its own outcome and an unknown guide to not found', async () => {
      const reused = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-1');
      http
        .expectOne(BASE)
        .flush(problem('idempotency.key_reused'), { status: 422, statusText: 'Unprocessable Content' });
      expect((await reused).status).toBe('idempotency_key_conflict');

      const missing = service.request('cozy-fall', { guideId: GUIDE_ID, versionNumber: 2 }, 'key-2');
      http
        .expectOne(BASE)
        .flush(problem('ai.brandStyleTestDrive.guide.not_found'), { status: 404, statusText: 'Not Found' });
      expect((await missing).status).toBe('not_found');
    });
  });

  describe('watchStatus', () => {
    it('reads the comparison once there is one', async () => {
      const pending = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));

      http.expectOne(STATUS_URL).flush(
        payload({
          status: 'Proposed',
          comparison: {
            subject: 'a one-pan lemon chicken for a weeknight',
            guideId: GUIDE_ID,
            guideVersionNumber: 2,
            guideVersionWasActive: false,
            modelName: 'test-model',
            promptTemplateVersion: '1.0.0',
            generatedAt: '2026-10-04T12:01:00Z',
            samples: [{ sample: 'blogIntro', withoutGuide: 'Plain.', withGuide: 'Theirs.', notes: [] }],
            appliedRules: [{ label: 'Voice', summary: 'Warm, direct, never fussy.' }],
            citations: [],
            notices: [],
            groundingChangedSince: false,
          },
        }),
      );

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      expect(outcome.status === 'found' && outcome.testDrive.comparison?.appliedRules[0].label).toBe('Voice');
    });

    /** Never errors: one failed poll must not kill the subscription a screen is polling through. */
    it('turns every failure into a value', async () => {
      const missing = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
      http.expectOne(STATUS_URL).flush(problem('x.not_found'), { status: 404, statusText: 'Not Found' });
      expect((await missing).status).toBe('not_found');

      const forbidden = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
      http.expectOne(STATUS_URL).flush(problem('x.forbidden'), { status: 403, statusText: 'Forbidden' });
      expect((await forbidden).status).toBe('forbidden');

      const broken = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));
      http.expectOne(STATUS_URL).flush({ nonsense: true });
      expect((await broken).status).toBe('unavailable');
    });
  });
});
