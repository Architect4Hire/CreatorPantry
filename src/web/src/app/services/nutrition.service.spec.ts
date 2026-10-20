import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import {
  NutritionEstimateOutcome,
  NutritionMappingDecisionOutcome,
  NutritionService,
} from './nutrition.service';

const RECIPE_ID = '2a1b7c6d-0000-4000-8000-000000000001';
const INGREDIENT_ID = '2a1b7c6d-0000-4000-8000-0000000000a1';
const ESTIMATE_URL =
  `https://gateway.example/api/v1/workspaces/cozy-fall/recipes/${RECIPE_ID}/versions/4/analysis/nutrition`;
const MAPPING_URL = `https://gateway.example/api/v1/workspaces/cozy-fall/nutrition-mappings/${INGREDIENT_ID}`;

function estimatePayload(): Record<string, unknown> {
  return {
    recipeId: RECIPE_ID,
    recipeVersionId: 'v4',
    versionNumber: 4,
    label: 'Estimate',
    methodVersion: 'nutrition-estimate/1',
    method: 'Per 100 g, multiplied by weight in grams and summed.',
    status: 'Complete',
    publishable: true,
    partial: false,
    confidence: 'Mass',
    servings: 12,
    servingsSource: 'ServingCount',
    nutrients: [],
    lines: [],
    sources: [],
    unresolved: { unmapped: [], needsReview: [], unconvertible: [], notQuantified: [] },
  };
}

function mappingPayload(): Record<string, unknown> {
  return {
    id: 'm1',
    ingredientId: INGREDIENT_ID,
    status: 'Confirmed',
    matchMethod: 'CreatorSelected',
    confidence: null,
    reference: {
      sourceCode: 'fdc',
      externalRecordId: '173410',
      description: 'Butter, salted',
      sourceVersion: '2026-01-01',
    },
    reviewSourceVersion: null,
    unresolvedReason: null,
    portion: null,
    rejectedReferenceCount: 0,
    confirmedAt: '2026-10-20T10:00:00Z',
    updatedAt: '2026-10-20T10:00:00Z',
    concurrencyToken: 'token-2',
  };
}

describe('NutritionService', () => {
  let service: NutritionService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(NutritionService);
  });

  afterEach(() => http.verify());

  function estimate(servings: number | null = null): Promise<NutritionEstimateOutcome> {
    return service.estimate('cozy-fall', RECIPE_ID, 4, { servings });
  }

  function confirm(): Promise<NutritionMappingDecisionOutcome> {
    return service.confirmMapping('cozy-fall', INGREDIENT_ID, { expectedConcurrencyToken: 'token-1' });
  }

  describe('the estimate', () => {
    it('POSTs the yield basis to the named version, with credentials, and decodes a 200', async () => {
      const call = estimate(24);
      const req = http.expectOne(ESTIMATE_URL);

      expect(req.request.method).toBe('POST');
      expect(req.request.withCredentials).toBeTrue();
      expect(req.request.body).toEqual({ servings: 24 });

      req.flush(estimatePayload());

      const outcome = await call;
      expect(outcome.status).toBe('estimated');
      expect(outcome.status === 'estimated' && outcome.estimate.label).toBe('Estimate');
    });

    it('sends a null serving count rather than omitting it, which the server reads the same way', async () => {
      const call = estimate(null);
      const req = http.expectOne(ESTIMATE_URL);

      expect(req.request.body).toEqual({ servings: null });
      req.flush(estimatePayload());

      expect((await call).status).toBe('estimated');
    });

    it('reports an estimate it cannot read as unavailable rather than showing part of one', async () => {
      const call = estimate();
      const broken = estimatePayload();
      broken['status'] = 'Probably';
      http.expectOne(ESTIMATE_URL).flush(broken);

      expect((await call).status).toBe('unavailable');
    });

    it('carries the field errors from a refused serving count', async () => {
      const call = estimate(0);
      http.expectOne(ESTIMATE_URL).flush(
        { code: 'nutrition.estimate.invalid', errors: { servings: ['Enter a number above 0.'] } },
        { status: 400, statusText: 'Bad Request' },
      );

      const outcome = await call;
      expect(outcome.status).toBe('invalid_request');
      expect(outcome.status === 'invalid_request' && outcome.fieldErrors['servings'][0]).toBe(
        'Enter a number above 0.',
      );
    });

    it("keeps the server's sentence for a total too large to state", async () => {
      const call = estimate();
      http.expectOne(ESTIMATE_URL).flush(
        { code: 'nutrition.estimate.quantities.unprocessable', title: 'The total energy is too large to state.' },
        { status: 422, statusText: 'Unprocessable Content' },
      );

      const outcome = await call;
      expect(outcome.status).toBe('quantities_unprocessable');
      expect(outcome.status === 'quantities_unprocessable' && outcome.message).toBe(
        'The total energy is too large to state.',
      );
    });

    it("keeps the server's sentence for a source that cannot be asked, which says whether waiting helps", async () => {
      const call = estimate();
      http.expectOne(ESTIMATE_URL).flush(
        {
          code: 'nutrition.estimate.source.unavailable',
          title: 'The nutrition source is not available on this installation.',
        },
        { status: 503, statusText: 'Service Unavailable' },
      );

      const outcome = await call;
      expect(outcome.status).toBe('source_unavailable');
      expect(outcome.status === 'source_unavailable' && outcome.message).toContain('not available on this installation');
    });

    it('tells a missing version from an unreadable recipe by the stable code on the 404', async () => {
      const version = estimate();
      http
        .expectOne(ESTIMATE_URL)
        .flush({ code: 'recipes.version.not_found' }, { status: 404, statusText: 'Not Found' });
      expect((await version).status).toBe('version_not_found');

      const recipe = estimate();
      http
        .expectOne(ESTIMATE_URL)
        .flush({ code: 'recipes.recipe.not_found' }, { status: 404, statusText: 'Not Found' });
      expect((await recipe).status).toBe('not_found');
    });

    it('keeps a lost session apart from an outage', async () => {
      const unauthorized = estimate();
      http.expectOne(ESTIMATE_URL).flush({}, { status: 401, statusText: 'Unauthorized' });
      expect((await unauthorized).status).toBe('unauthorized');

      const outage = estimate();
      http.expectOne(ESTIMATE_URL).flush({}, { status: 500, statusText: 'Server Error' });
      expect((await outage).status).toBe('unavailable');
    });
  });

  describe('the mapping', () => {
    it('reads one ingredient’s mapping with credentials', async () => {
      const call = service.getMapping('cozy-fall', INGREDIENT_ID);
      const req = http.expectOne(MAPPING_URL);

      expect(req.request.method).toBe('GET');
      expect(req.request.withCredentials).toBeTrue();

      req.flush(mappingPayload());

      const outcome = await call;
      expect(outcome.status).toBe('found');
      expect(outcome.status === 'found' && outcome.mapping.concurrencyToken).toBe('token-2');
    });

    it('reports an ingredient with no mapping as not found, which is also another workspace’s answer', async () => {
      const call = service.getMapping('cozy-fall', INGREDIENT_ID);
      http
        .expectOne(MAPPING_URL)
        .flush({ code: 'nutrition.mapping.not_found' }, { status: 404, statusText: 'Not Found' });

      expect((await call).status).toBe('not_found');
    });

    it('POSTs a confirmation as the token alone, and decodes the mapping that comes back', async () => {
      const call = confirm();
      const req = http.expectOne(`${MAPPING_URL}/confirm`);

      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ expectedConcurrencyToken: 'token-1' });

      req.flush(mappingPayload());

      const outcome = await call;
      expect(outcome.status).toBe('decided');
      expect(outcome.status === 'decided' && outcome.mapping.status).toBe('Confirmed');
    });

    it('POSTs a rejection to its own operation', async () => {
      const call = service.rejectMapping('cozy-fall', INGREDIENT_ID, { expectedConcurrencyToken: 'token-1' });
      const req = http.expectOne(`${MAPPING_URL}/reject`);

      expect(req.request.body).toEqual({ expectedConcurrencyToken: 'token-1' });
      req.flush({ ...mappingPayload(), status: 'Unresolved', reference: null, unresolvedReason: 'ReferenceRejected' });

      const outcome = await call;
      expect(outcome.status).toBe('decided');
      expect(outcome.status === 'decided' && outcome.mapping.unresolvedReason).toBe('ReferenceRejected');
    });

    it('tells a stale token from a mapping with nothing left to decide', async () => {
      const stale = confirm();
      http
        .expectOne(`${MAPPING_URL}/confirm`)
        .flush({ code: 'nutrition.mapping.conflict' }, { status: 409, statusText: 'Conflict' });
      expect((await stale).status).toBe('conflict');

      const settled = confirm();
      http
        .expectOne(`${MAPPING_URL}/confirm`)
        .flush({ code: 'nutrition.mapping.state.conflict' }, { status: 409, statusText: 'Conflict' });
      expect((await settled).status).toBe('state_conflict');
    });

    it('reports a role refusal as a role refusal, not as a lost session', async () => {
      const call = confirm();
      http
        .expectOne(`${MAPPING_URL}/confirm`)
        .flush({ code: 'nutrition.mapping.forbidden' }, { status: 403, statusText: 'Forbidden' });

      expect((await call).status).toBe('forbidden');
    });

    it('reports a 503 as retryable, with the sentence saying which one it was', async () => {
      const call = confirm();
      http.expectOne(`${MAPPING_URL}/confirm`).flush(
        { code: 'nutrition.mapping.save.unavailable', title: 'The store refused the write. Nothing was saved.' },
        { status: 503, statusText: 'Service Unavailable' },
      );

      const outcome = await call;
      expect(outcome.status).toBe('retryable');
      expect(outcome.status === 'retryable' && outcome.message).toContain('Nothing was saved');
    });

    it('carries the field errors from a decision the server would not read', async () => {
      const call = confirm();
      http.expectOne(`${MAPPING_URL}/confirm`).flush(
        {
          code: 'nutrition.mapping.invalid',
          errors: { expectedConcurrencyToken: ['Name the token of the mapping you read.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );

      const outcome = await call;
      expect(outcome.status).toBe('invalid_request');
      expect(
        outcome.status === 'invalid_request' && outcome.fieldErrors['expectedConcurrencyToken'][0],
      ).toContain('Name the token');
    });
  });
});
