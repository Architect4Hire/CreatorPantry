import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { LongFormSeoService } from './long-form-seo.service';

const ROOT = 'https://gateway.example/api/v1/workspaces/cozy-fall/long-form-drafts/d-1/seo-package-requests';

const QUEUED = {
  aiProposalRequestId: 'req-1',
  status: 'Requested',
  taskType: 'SeoPackage',
  scope: 'NotApplicable',
  sourceVersionId: null,
  requestedAt: '2026-10-12T00:00:00Z',
  statusChangedAt: '2026-10-12T00:00:00Z',
  failureCategory: null,
  proposal: null,
};

describe('LongFormSeoService', () => {
  let service: LongFormSeoService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(LongFormSeoService);
  });

  afterEach(() => http.verify());

  it('asks with the accepted revision and the key, and nothing that names a workspace or a draft', async () => {
    const asking = service.request('cozy-fall', 'd-1', 4, 'key-1');

    const call = http.expectOne(ROOT);
    expect(call.request.method).toBe('POST');
    expect(call.request.body).toEqual({ revisionNumber: 4 });
    expect(call.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(call.request.withCredentials).toBeTrue();
    call.flush(QUEUED, { status: 202, statusText: 'Accepted' });

    const outcome = await asking;
    expect(outcome.status).toBe('accepted');
    if (outcome.status === 'accepted') {
      expect(outcome.operation.aiProposalRequestId).toBe('req-1');
      expect(outcome.replayed).toBeFalse();
    }
  });

  it('carries the server’s sentence when the piece cannot be written about, and tells the other refusals apart', async () => {
    const cases: [number, Record<string, unknown>, string][] = [
      [409, { code: 'ai.seoPackage.longFormDraft.not_accepted.conflict', detail: 'Accept it first.' }, 'refused'],
      [404, { code: 'ai.seoPackage.longFormDraft.not_found', detail: 'That long-form draft does not exist.' }, 'refused'],
      [400, { code: 'ai.seoPackageLongFormRevision.invalid_request', detail: 'Not the accepted revision.' }, 'refused'],
      [400, { code: 'ai.seoPackage.not_enabled' }, 'task_not_enabled'],
      [400, { code: 'ai.seoPackage.invalid_request', errors: { sections: ['No.'] } }, 'validation_failed'],
      [422, { code: 'idempotency.key_reused' }, 'idempotency_key_conflict'],
      [403, {}, 'forbidden'],
      [503, {}, 'unavailable'],
    ];

    for (const [status, body, expected] of cases) {
      const asking = service.request('cozy-fall', 'd-1', 4, 'key-1');
      http.expectOne(ROOT).flush(body, { status, statusText: 'x' });

      expect((await asking).status).withContext(String(status)).toBe(expected);
    }
  });

  it('follows a request by its id, and a failed poll is an outcome and never an error', async () => {
    const watching = service.watch('cozy-fall', 'd-1', 'req/1');
    const call = http.expectOne(`${ROOT}/req%2F1`);
    expect(call.request.method).toBe('GET');
    call.flush({ ...QUEUED, status: 'Running' });

    const found = await watching;
    expect(found.status).toBe('found');

    for (const [status, expected] of [[404, 'not_found'], [403, 'forbidden'], [500, 'unavailable']] as const) {
      const again = service.watch('cozy-fall', 'd-1', 'req-1');
      http.expectOne(`${ROOT}/req-1`).flush({}, { status, statusText: 'x' });

      expect((await again).status).toBe(expected);
    }
  });

  it('is unavailable rather than half read when the reply is not a request', async () => {
    const watching = service.watch('cozy-fall', 'd-1', 'req-1');
    http.expectOne(`${ROOT}/req-1`).flush({ status: 'Running' });

    expect((await watching).status).toBe('unavailable');
  });
});
