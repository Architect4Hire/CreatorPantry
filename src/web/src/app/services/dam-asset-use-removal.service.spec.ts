import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DamAssetService } from './dam-asset.service';

const GATEWAY = 'https://gateway.example';
const BASE = `${GATEWAY}/api/v1/workspaces/cozy-fall/dam-assets`;
const ID = '0f4b2c9a-1111-4222-8333-444455556666';

function useBody(): Record<string, unknown> {
  return {
    id: 'u1',
    platformKey: 'newsletter',
    utilizedOn: '2026-10-04',
    utilizedDay: 'Sunday',
    campaignName: null,
    notes: null,
    createdAt: '2026-10-08T12:00:00+00:00',
  };
}

function removalBody(): Record<string, unknown> {
  return {
    id: ID,
    title: 'Soda bread hero',
    deletedAt: '2026-10-08T14:14:00+00:00',
    deletedByMembershipId: 'm1',
    alreadyDeleted: false,
    affected: { recipeCount: 0, recipes: [], brandProfileCount: 0, testAttachmentCount: 0, any: false },
    concurrencyToken: 'token-after',
  };
}

function problem(code: string, extra: Record<string, unknown> = {}): Record<string, unknown> {
  return { code, title: 'That could not be done.', traceId: 'trace-1', ...extra };
}

describe('DamAssetService — logging a use and removing', () => {
  let service: DamAssetService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: GATEWAY });
    await loading;

    service = TestBed.inject(DamAssetService);
  });

  afterEach(() => http.verify());

  describe('logUtilization', () => {
    const body = { platformKey: 'newsletter', utilizedOn: '2026-10-04', campaignName: null, notes: null };

    it('posts the use as given, with credentials and the idempotency key, and returns what was recorded', async () => {
      const pending = service.logUtilization('cozy-fall', ID, body, 'key-1');

      const request = http.expectOne(`${BASE}/${ID}/utilization`);
      expect(request.request.method).toBe('POST');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.body).toEqual(body);
      request.flush(useBody(), { status: 201, statusText: 'Created' });

      const outcome = await pending;
      expect(outcome.status).toBe('logged');
      if (outcome.status === 'logged') {
        expect(outcome.use.utilizedOn).toBe('2026-10-04');
        // The weekday is the server's, derived in the workspace's zone.
        expect(outcome.use.utilizedDay).toBe('Sunday');
      }
    });

    it("answers not_found for a removed asset, exactly as for an unknown one — no use is logged against either", async () => {
      const pending = service.logUtilization('cozy-fall', ID, body, 'key-1');

      http
        .expectOne(`${BASE}/${ID}/utilization`)
        .flush(problem('media.asset.not_found'), { status: 404, statusText: 'Not Found' });

      expect(await pending).toEqual({ status: 'not_found' });
    });

    it("carries the server's field errors on a refused entry", async () => {
      const pending = service.logUtilization('cozy-fall', ID, body, 'key-1');

      http
        .expectOne(`${BASE}/${ID}/utilization`)
        .flush(problem('media.asset.invalid_request', { title: 'That use could not be logged.', errors: { utilizedOn: ['That date is too far ahead.'] } }), {
          status: 400,
          statusText: 'Bad Request',
        });

      expect(await pending).toEqual({
        status: 'invalid',
        message: 'That use could not be logged.',
        fieldErrors: { utilizedOn: ['That date is too far ahead.'] },
      });
    });

    it('tells forbidden and a failure apart, and treats an unreadable success as not logged', async () => {
      const url = `${BASE}/${ID}/utilization`;

      const forbidden = service.logUtilization('cozy-fall', ID, body, 'k');
      http.expectOne(url).flush(problem('media.asset.forbidden'), { status: 403, statusText: 'Forbidden' });
      expect(await forbidden).toEqual({ status: 'forbidden' });

      const failed = service.logUtilization('cozy-fall', ID, body, 'k');
      http.expectOne(url).flush(null, { status: 503, statusText: 'Unavailable' });
      expect(await failed).toEqual({ status: 'unavailable' });

      const unreadable = service.logUtilization('cozy-fall', ID, body, 'k');
      http.expectOne(url).flush({ id: 'u1' }, { status: 201, statusText: 'Created' });
      expect(await unreadable).toEqual({ status: 'unavailable' });
    });
  });

  describe('remove', () => {
    it('sends a confirmed removal quoting the token, to the asset by id, and returns the impact', async () => {
      const pending = service.remove('cozy-fall', ID, 'token-1');

      const request = http.expectOne(`${BASE}/${ID}`);
      expect(request.request.method).toBe('DELETE');
      expect(request.request.withCredentials).toBeTrue();
      // Exactly the two things the route takes: that a person decided, and what they were looking at.
      expect(request.request.body).toEqual({ confirmed: true, expectedConcurrencyToken: 'token-1' });
      request.flush(removalBody());

      const outcome = await pending;
      expect(outcome.status).toBe('removed');
      if (outcome.status === 'removed') {
        expect(outcome.removal.title).toBe('Soda bread hero');
        expect(outcome.removal.alreadyDeleted).toBeFalse();
      }
    });

    it('reports a conflict as stale, so the creator is shown the latest before being asked again', async () => {
      const pending = service.remove('cozy-fall', ID, 'old-token');

      http.expectOne(`${BASE}/${ID}`).flush(problem('media.asset.stale.conflict'), { status: 409, statusText: 'Conflict' });

      expect(await pending).toEqual({ status: 'stale' });
    });

    it('tells forbidden, not found and a failure apart, and never reports a removal it could not read', async () => {
      const url = `${BASE}/${ID}`;

      const forbidden = service.remove('cozy-fall', ID, 't');
      http.expectOne(url).flush(problem('media.asset.forbidden'), { status: 403, statusText: 'Forbidden' });
      expect(await forbidden).toEqual({ status: 'forbidden' });

      const missing = service.remove('cozy-fall', ID, 't');
      http.expectOne(url).flush(problem('media.asset.not_found'), { status: 404, statusText: 'Not Found' });
      expect(await missing).toEqual({ status: 'not_found' });

      const failed = service.remove('cozy-fall', ID, 't');
      http.expectOne(url).flush(null, { status: 500, statusText: 'Server Error' });
      expect(await failed).toEqual({ status: 'unavailable' });

      const unreadable = service.remove('cozy-fall', ID, 't');
      http.expectOne(url).flush({ id: ID });
      expect(await unreadable).toEqual({ status: 'unavailable' });
    });
  });
});
