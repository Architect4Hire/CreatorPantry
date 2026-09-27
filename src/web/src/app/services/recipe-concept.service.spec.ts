import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { EMPTY_RECIPE_CONCEPTS_REQUEST } from '../models/recipe-concept.models';
import { RecipeConceptService } from './recipe-concept.service';

const REQUEST_ID = '9f000000-0000-4000-8000-000000000009';
const BASE = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipe-concept-requests';
const STATUS_URL = `${BASE}/${REQUEST_ID}`;

const BRIEF = { ...EMPTY_RECIPE_CONCEPTS_REQUEST, audience: 'Weeknight home cooks', cuisine: 'Sichuan' };

function statusPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Requested',
    taskType: 'RecipeConcepts',
    scope: 'Unspecified',
    sourceVersionId: null,
    requestedAt: '2026-03-12T14:02:00Z',
    statusChangedAt: '2026-03-12T14:02:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

function problem(code: string, title = 'Refused', errors?: Record<string, string[]>): Record<string, unknown> {
  return { code, title, traceId: 'trace-1', ...(errors ? { errors } : {}) };
}

describe('RecipeConceptService', () => {
  let service: RecipeConceptService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeConceptService);
  });

  afterEach(() => http.verify());

  describe('requestConcepts', () => {
    it('posts the allow-listed brief with the callers idempotency key', async () => {
      const pending = service.requestConcepts('cozy-fall', BRIEF, 'key-1');

      const request = http.expectOne(BASE);
      expect(request.request.method).toBe('POST');
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.body).toEqual({ audience: 'Weeknight home cooks', cuisine: 'Sichuan' });

      request.flush(statusPayload(), { status: 202, statusText: 'Accepted' });

      const outcome = await pending;
      expect(outcome.status).toBe('accepted');
      if (outcome.status === 'accepted') {
        expect(outcome.operation.aiProposalRequestId).toBe(REQUEST_ID);
        expect(outcome.replayed).toBeFalse();
      }
    });

    it('reports a replay so a caller does not think it bought a second answer', async () => {
      const pending = service.requestConcepts('cozy-fall', BRIEF, 'key-1');

      http
        .expectOne(BASE)
        .flush(statusPayload(), { status: 202, statusText: 'Accepted', headers: { 'Idempotent-Replayed': 'true' } });

      const outcome = await pending;
      expect(outcome.status === 'accepted' && outcome.replayed).toBeTrue();
    });

    it('maps the not-enabled code to its own outcome', async () => {
      const pending = service.requestConcepts('cozy-fall', BRIEF, 'key-1');

      http.expectOne(BASE).flush(problem('ai.recipeConcepts.not_enabled'), { status: 400, statusText: 'Bad Request' });

      expect((await pending).status).toBe('task_not_enabled');
    });

    it('maps a different 400 to a field validation outcome', async () => {
      const pending = service.requestConcepts('cozy-fall', BRIEF, 'key-1');

      http
        .expectOne(BASE)
        .flush(problem('ai.recipeConcepts.invalid_request', 'Refused', { audience: ['Too long.'] }), {
          status: 400,
          statusText: 'Bad Request',
        });

      const outcome = await pending;
      expect(outcome.status).toBe('validation_failed');
      if (outcome.status === 'validation_failed') expect(outcome.fieldErrors['audience']).toEqual(['Too long.']);
    });

    it('reads a reused key as its own outcome, not as a field problem', async () => {
      const pending = service.requestConcepts('cozy-fall', BRIEF, 'key-1');

      http.expectOne(BASE).flush(problem('idempotency.key_reused'), { status: 422, statusText: 'Unprocessable Content' });

      expect((await pending).status).toBe('idempotency_key_conflict');
    });

    it('maps the remaining statuses', async () => {
      const cases: { status: number; expected: string }[] = [
        { status: 403, expected: 'forbidden' },
        { status: 500, expected: 'unavailable' },
      ];

      for (const { status, expected } of cases) {
        const pending = service.requestConcepts('cozy-fall', BRIEF, 'key-1');
        http.expectOne(BASE).flush(problem('ai.whatever'), { status, statusText: 'x' });
        expect((await pending).status).toBe(expected);
      }
    });

    it('answers unavailable without a request when the gateway is not configured yet', async () => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
      const isolated = TestBed.inject(RecipeConceptService);

      const outcome = await isolated.requestConcepts('cozy-fall', BRIEF, 'key-1');

      expect(outcome.status).toBe('unavailable');
      TestBed.inject(HttpTestingController).verify();
    });
  });

  describe('watchStatus', () => {
    it('reads the operation, and a status before any proposal exists is a normal answer', async () => {
      const outcome = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));

      http.expectOne(STATUS_URL).flush(statusPayload({ status: 'Running' }));

      const result = await outcome;
      expect(result.status).toBe('found');
      if (result.status === 'found') expect(result.operation.status).toBe('Running');
    });

    it('never errors: a failed read is a value', async () => {
      const outcome = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));

      http.expectOne(STATUS_URL).flush(problem('ai.whatever'), { status: 500, statusText: 'x' });

      expect((await outcome).status).toBe('unavailable');
    });

    it('maps a 404 to not_found', async () => {
      const outcome = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));

      http.expectOne(STATUS_URL).flush(problem('ai.recipeConceptRequest.not_found'), { status: 404, statusText: 'x' });

      expect((await outcome).status).toBe('not_found');
    });

    it('maps a 403 to forbidden', async () => {
      const outcome = firstValueFrom(service.watchStatus('cozy-fall', REQUEST_ID));

      http.expectOne(STATUS_URL).flush(problem('ai.whatever'), { status: 403, statusText: 'x' });

      expect((await outcome).status).toBe('forbidden');
    });
  });
});
