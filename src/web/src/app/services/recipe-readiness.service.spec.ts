import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../core/runtime-config.service';
import { RecipeTransitionRequest } from '../models/recipe-readiness.models';
import { RecipeReadinessService } from './recipe-readiness.service';

const READINESS_URL = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/readiness';
const TRANSITIONS_URL = 'https://gateway.example/api/v1/workspaces/cozy-fall/recipes/r1/readiness-transitions';

const TRANSITION: RecipeTransitionRequest = {
  targetStatus: 'Approved',
  reason: null,
  expectedConcurrencyToken: 'token-1',
};

function readinessPayload(): Record<string, unknown> {
  return {
    recipeId: 'r1',
    evaluatedVersionId: 'v3',
    evaluatedVersionNumber: 3,
    concurrencyToken: 'token-1',
    ruleSetVersion: '1.0.0',
    hasBlockers: false,
    blockerCount: 0,
    recommendationCount: 0,
    findings: [],
    disabledRuleIds: [],
    unknownConfiguredRuleIds: [],
  };
}

function recipePayload(status = 'Approved'): Record<string, unknown> {
  return {
    id: 'r1',
    title: 'Chili',
    description: null,
    headnote: null,
    notes: null,
    storageNotes: null,
    attributionText: null,
    sourceUrl: null,
    cuisineId: null,
    courseId: null,
    primaryTechniqueId: null,
    prepTimeMinutes: null,
    cookTimeMinutes: null,
    restTimeMinutes: null,
    totalTimeMinutes: null,
    yieldText: null,
    yieldQuantity: null,
    yieldUnitId: null,
    servingCount: null,
    servingSize: null,
    status,
    duplicatedFrom: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-02T00:00:00Z',
    concurrencyToken: 'token-2',
    currentVersion: null,
    ingredientGroups: [],
    instructionGroups: [],
    equipment: [],
    assetLinks: [],
    tags: [],
  };
}

describe('RecipeReadinessService', () => {
  let service: RecipeReadinessService;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;

    service = TestBed.inject(RecipeReadinessService);
  });

  afterEach(() => http.verify());

  describe('getReadiness', () => {
    it('reads the evaluation from the workspace-scoped route, with credentials', async () => {
      const pending = service.getReadiness('cozy-fall', 'r1');

      const request = http.expectOne(READINESS_URL);
      expect(request.request.method).toBe('GET');
      expect(request.request.withCredentials).toBeTrue();
      request.flush(readinessPayload());

      const outcome = await pending;
      expect(outcome.status).toBe('found');
      if (outcome.status === 'found') expect(outcome.readiness.ruleSetVersion).toBe('1.0.0');
    });

    /** No query parameters at all: there is no filtering and no paging on this route. */
    it('asks for the whole evaluation with no parameters', async () => {
      const pending = service.getReadiness('cozy-fall', 'r1');

      const request = http.expectOne(READINESS_URL);
      expect(request.request.params.keys().length).toBe(0);
      request.flush(readinessPayload());

      await pending;
    });

    it('reports an unknown or unreadable recipe as not found', async () => {
      const pending = service.getReadiness('cozy-fall', 'r1');
      http.expectOne(READINESS_URL).flush({}, { status: 404, statusText: 'Not Found' });

      expect((await pending).status).toBe('not_found');
    });

    it('reports a body it cannot decode as unavailable rather than as an evaluation', async () => {
      const pending = service.getReadiness('cozy-fall', 'r1');
      http.expectOne(READINESS_URL).flush({ recipeId: 'r1' });

      expect((await pending).status).toBe('unavailable');
    });
  });

  describe('transition', () => {
    it('posts the move with the required idempotency key and the recipe token', async () => {
      const pending = service.transition('cozy-fall', 'r1', TRANSITION, 'key-1');

      const request = http.expectOne(TRANSITIONS_URL);
      expect(request.request.method).toBe('POST');
      expect(request.request.withCredentials).toBeTrue();
      expect(request.request.headers.get('Idempotency-Key')).toBe('key-1');
      expect(request.request.body).toEqual({
        targetStatus: 'Approved',
        reason: null,
        expectedConcurrencyToken: 'token-1',
      });
      request.flush(recipePayload());

      const outcome = await pending;
      expect(outcome.status).toBe('moved');
      if (outcome.status === 'moved') {
        expect(outcome.recipe.status).toBe('Approved');
        expect(outcome.recipe.concurrencyToken).toBe('token-2');
      }
    });

    it('reports a replay so a caller can tell a repeat from a second move', async () => {
      const pending = service.transition('cozy-fall', 'r1', TRANSITION, 'key-1');
      http.expectOne(TRANSITIONS_URL).flush(recipePayload(), { headers: { 'Idempotent-Replayed': 'true' } });

      const outcome = await pending;
      expect(outcome.status === 'moved' && outcome.replayed).toBeTrue();
    });

    /** The server's own sentence names the legal targets, and it is carried through rather than reworded. */
    it('carries an invalid jump through with the states the recipe could have gone to', async () => {
      const pending = service.transition('cozy-fall', 'r1', TRANSITION, 'key-1');
      http.expectOne(TRANSITIONS_URL).flush(
        {
          code: 'recipes.transition.invalid_request',
          title: 'A recipe cannot go from Draft to Approved.',
          errors: { transition: ['From Draft it can go to InDevelopment, Archived.'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );

      const outcome = await pending;
      expect(outcome.status).toBe('invalid');
      if (outcome.status === 'invalid') {
        expect(outcome.message).toBe('A recipe cannot go from Draft to Approved.');
        expect(outcome.detail).toBe('From Draft it can go to InDevelopment, Archived.');
      }
    });

    it('carries a forbidden move through with the role it needs', async () => {
      const pending = service.transition('cozy-fall', 'r1', TRANSITION, 'key-1');
      http.expectOne(TRANSITIONS_URL).flush(
        {
          code: 'recipes.transition.forbidden',
          title: 'Moving a recipe from ReadyForReview to Approved is an Editor action.',
        },
        { status: 403, statusText: 'Forbidden' },
      );

      const outcome = await pending;
      expect(outcome.status).toBe('forbidden');
      if (outcome.status === 'forbidden') expect(outcome.message).toContain('Editor action');
    });

    /** Two 409s, two remedies: clear the blockers, or go and look at what moved. */
    it('separates blocked readiness from a stale token', async () => {
      const blocked = service.transition('cozy-fall', 'r1', TRANSITION, 'key-1');
      http.expectOne(TRANSITIONS_URL).flush(
        {
          code: 'recipes.transition.blocked.conflict',
          title: 'This recipe has 2 readiness blockers outstanding.',
          errors: { blockingRules: ['recipe.ingredients.unmatched', 'recipe.testing.none'] },
        },
        { status: 409, statusText: 'Conflict' },
      );
      const blockedOutcome = await blocked;
      expect(blockedOutcome.status).toBe('blocked');
      if (blockedOutcome.status === 'blocked') {
        expect(blockedOutcome.blockingRuleIds).toEqual(['recipe.ingredients.unmatched', 'recipe.testing.none']);
      }

      const stale = service.transition('cozy-fall', 'r1', TRANSITION, 'key-2');
      http
        .expectOne(TRANSITIONS_URL)
        .flush({ code: 'recipes.recipe.conflict' }, { status: 409, statusText: 'Conflict' });
      expect((await stale).status).toBe('conflict');
    });

    it('treats an uncoded 409 as a stale token', async () => {
      const pending = service.transition('cozy-fall', 'r1', TRANSITION, 'key-1');
      http.expectOne(TRANSITIONS_URL).flush({}, { status: 409, statusText: 'Conflict' });

      expect((await pending).status).toBe('conflict');
    });

    it('reports a reused key as its own outcome', async () => {
      const pending = service.transition('cozy-fall', 'r1', TRANSITION, 'key-1');
      http.expectOne(TRANSITIONS_URL).flush({}, { status: 422, statusText: 'Unprocessable Content' });

      expect((await pending).status).toBe('idempotency_key_conflict');
    });

    it('reports an unknown recipe as not found', async () => {
      const pending = service.transition('cozy-fall', 'r1', TRANSITION, 'key-1');
      http.expectOne(TRANSITIONS_URL).flush({}, { status: 404, statusText: 'Not Found' });

      expect((await pending).status).toBe('not_found');
    });

    it('sends a reason when the move carries one', async () => {
      const pending = service.transition(
        'cozy-fall',
        'r1',
        { targetStatus: 'InDevelopment', reason: 'Crumb too dense', expectedConcurrencyToken: 'token-1' },
        'key-1',
      );

      const request = http.expectOne(TRANSITIONS_URL);
      expect(request.request.body.reason).toBe('Crumb too dense');
      request.flush(recipePayload('InDevelopment'));

      await pending;
    });
  });

  describe('with no gateway resolved', () => {
    beforeEach(() => {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
      http = TestBed.inject(HttpTestingController);
      service = TestBed.inject(RecipeReadinessService);
    });

    it('reports unavailable without making a request', async () => {
      expect((await service.getReadiness('cozy-fall', 'r1')).status).toBe('unavailable');
      expect((await service.transition('cozy-fall', 'r1', TRANSITION, 'key-1')).status).toBe('unavailable');
      http.expectNone(() => true);
    });
  });
});
