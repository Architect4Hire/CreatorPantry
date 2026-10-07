import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { PhotographyConceptService } from './photography-concept.service';

const BASE = 'https://gateway.example/api/v1/workspaces/cozy-fall/photography-concept-requests';

function accepted(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    aiProposalRequestId: 'r-1',
    status: 'Requested',
    taskType: 'PhotographyConcept',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-10-07T12:00:00Z',
    statusChangedAt: '2026-10-07T12:00:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

describe('PhotographyConceptService', () => {
  let service: PhotographyConceptService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(PhotographyConceptService);
  });

  afterEach(() => http.verify());

  it('posts the brief with the caller’s idempotency key and reports the accepted operation', async () => {
    const pending = service.request(
      'cozy-fall',
      { channelKey: 'instagram', creatorConcept: 'A tight crop.', sceneOverrides: [], styleOverrides: [] },
      'key-1',
    );

    const request = http.expectOne(BASE);
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.body).toEqual({ channelKey: 'instagram', creatorConcept: 'A tight crop.' });

    request.flush(accepted(), { status: 202, statusText: 'Accepted' });

    const outcome = await pending;
    expect(outcome.status).toBe('accepted');
    if (outcome.status === 'accepted') {
      expect(outcome.operation.aiProposalRequestId).toBe('r-1');
      expect(outcome.replayed).toBeFalse();
    }
  });

  it('reports a replay, so a retry is not read as a second generation', async () => {
    const pending = service.request(
      'cozy-fall',
      { channelKey: null, creatorConcept: '', sceneOverrides: [], styleOverrides: [] },
      'key-1',
    );

    http.expectOne(BASE).flush(accepted(), {
      status: 202,
      statusText: 'Accepted',
      headers: { 'Idempotent-Replayed': 'true' },
    });

    const outcome = await pending;
    expect(outcome.status).toBe('accepted');
    if (outcome.status === 'accepted') expect(outcome.replayed).toBeTrue();
  });

  it('escapes the workspace segment rather than letting it shape the path', async () => {
    const pending = service.request(
      'cozy fall/../other',
      { channelKey: null, creatorConcept: '', sceneOverrides: [], styleOverrides: [] },
      'key-1',
    );

    http
      .expectOne(
        'https://gateway.example/api/v1/workspaces/cozy%20fall%2F..%2Fother/photography-concept-requests',
      )
      .flush(accepted(), { status: 202, statusText: 'Accepted' });

    await pending;
  });

  it('reads where a request has got to, by its id', async () => {
    const pending = firstValueFrom(service.watch('cozy-fall', 'r-1'));

    const request = http.expectOne(`${BASE}/r-1`);
    expect(request.request.method).toBe('GET');
    request.flush(accepted({ status: 'Running' }));

    const outcome = await pending;
    expect(outcome.status).toBe('found');
    if (outcome.status === 'found') expect(outcome.operation.status).toBe('Running');
  });

  it('tells a request that is gone from one it may not read', async () => {
    const gone = firstValueFrom(service.watch('cozy-fall', 'r-1'));
    http.expectOne(`${BASE}/r-1`).flush({ code: 'x' }, { status: 404, statusText: 'Not Found' });
    expect((await gone).status).toBe('not_found');

    const refused = firstValueFrom(service.watch('cozy-fall', 'r-2'));
    http.expectOne(`${BASE}/r-2`).flush({ code: 'x' }, { status: 403, statusText: 'Forbidden' });
    expect((await refused).status).toBe('forbidden');
  });

  it('reports a body it cannot read as unavailable rather than half an operation', async () => {
    const pending = firstValueFrom(service.watch('cozy-fall', 'r-1'));
    http.expectOne(`${BASE}/r-1`).flush(accepted({ status: 'Nowhere' }));

    expect((await pending).status).toBe('unavailable');
  });

  it('aborts the watch when the caller unsubscribes', () => {
    const subscription = service.watch('cozy-fall', 'r-1').subscribe();
    const request = http.expectOne(`${BASE}/r-1`);

    subscription.unsubscribe();

    expect(request.cancelled).toBeTrue();
  });
});
