import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { DishFacetService } from './dish-facet.service';

const BASE = 'https://gateway.example/api/v1/workspaces/cozy-fall/dish-facet-requests';

function accepted(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    aiProposalRequestId: 'r-1',
    status: 'Requested',
    taskType: 'DishFacetSuggestion',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-10T12:00:00Z',
    statusChangedAt: '2026-10-10T12:00:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

describe('DishFacetService', () => {
  let service: DishFacetService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(DishFacetService);
  });

  afterEach(() => http.verify());

  it('posts the name with the caller’s idempotency key and reports the accepted operation', async () => {
    const pending = service.request('cozy-fall', 'Fattoush Salad with Chicken Shawarma', 'key-1');

    const request = http.expectOne(BASE);
    expect(request.request.method).toBe('POST');
    expect(request.request.withCredentials).toBeTrue();
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
    expect(request.request.body).toEqual({ dishName: 'Fattoush Salad with Chicken Shawarma' });

    request.flush(accepted(), { status: 202, statusText: 'Accepted' });

    const outcome = await pending;
    expect(outcome.status).toBe('accepted');
    if (outcome.status === 'accepted') expect(outcome.operation.taskType).toBe('DishFacetSuggestion');
  });

  it('reports a task switched off for this deployment as that, not as a bad request', async () => {
    const pending = service.request('cozy-fall', 'Fattoush', 'key-1');

    http
      .expectOne(BASE)
      .flush({ code: 'ai.dishFacets.not_enabled' }, { status: 400, statusText: 'Bad Request' });

    expect((await pending).status).toBe('task_not_enabled');
  });

  it('reads where a request has got to', async () => {
    const watching = firstValueFrom(service.watch('cozy-fall', 'r-1'));

    http.expectOne(`${BASE}/r-1`).flush(accepted({ status: 'Running' }));

    const outcome = await watching;
    expect(outcome.status).toBe('found');
  });

  it('reports a request that is not this workspace’s as not found', async () => {
    const watching = firstValueFrom(service.watch('cozy-fall', 'r-9'));

    http.expectOne(`${BASE}/r-9`).flush({}, { status: 404, statusText: 'Not Found' });

    expect((await watching).status).toBe('not_found');
  });
});
